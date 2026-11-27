using DeskGrid.Core.Models;
using DeskGrid.Core.Services;

namespace DeskGrid.Tests;

public sealed class DockEnhancementTests
{
    [TestFact]
    public void MultiMonitorPositioning_CalculatesMixedDpiAndNegativeCoordinatesAccurately()
    {
        var leftSecondary = new DisplayMonitorInfo(
            DeviceName: @"\\.\DISPLAY2",
            Handle: 2002,
            IsPrimary: false,
            BoundsPx: new PhysicalRect(-1920, 0, 0, 1080),
            WorkingAreaPx: new PhysicalRect(-1920, 0, 0, 1040),
            DpiScaleX: 1.0,
            DpiScaleY: 1.0);

        var primary4k = new DisplayMonitorInfo(
            DeviceName: @"\\.\DISPLAY1",
            Handle: 1001,
            IsPrimary: true,
            BoundsPx: new PhysicalRect(0, 0, 3840, 2160),
            WorkingAreaPx: new PhysicalRect(0, 0, 3840, 2088),
            DpiScaleX: 1.5,
            DpiScaleY: 1.5);

        var monitors = new[] { leftSecondary, primary4k };

        // PrimaryOnly returns only primary
        var primarySelection = DockPositionCalculator.SelectTargetMonitors(monitors, DockMultiMonitorModes.PrimaryOnly);
        AssertEx.Equal(1, primarySelection.Count);
        AssertEx.Equal(primary4k.DeviceName, primarySelection[0].DeviceName);

        // AllMonitors returns every monitor
        var allSelection = DockPositionCalculator.SelectTargetMonitors(monitors, DockMultiMonitorModes.AllMonitors);
        AssertEx.Equal(2, allSelection.Count);

        // FollowMouse picks monitor containing cursor
        var followLeft = DockPositionCalculator.SelectTargetMonitors(monitors, DockMultiMonitorModes.FollowMouse, -800, 500);
        AssertEx.Equal(1, followLeft.Count);
        AssertEx.Equal(leftSecondary.DeviceName, followLeft[0].DeviceName);

        var followRight = DockPositionCalculator.SelectTargetMonitors(monitors, DockMultiMonitorModes.FollowMouse, 1200, 900);
        AssertEx.Equal(1, followRight.Count);
        AssertEx.Equal(primary4k.DeviceName, followRight[0].DeviceName);

        // Mixed-DPI bottom placement on 4K 150% primary (working area bottom = 2088)
        var placement4kWork = DockPositionCalculator.CalculateBottomPlacement(primary4k, windowDipWidth: 600, windowDipHeight: 120, autoHideSystemTaskbar: false);
        AssertEx.Equal(900, placement4kWork.PhysicalWidth);
        AssertEx.Equal(180, placement4kWork.PhysicalHeight);
        AssertEx.Equal(1470, placement4kWork.PhysicalLeft); // (3840 - 900) / 2
        AssertEx.Equal(1908, placement4kWork.PhysicalTop);  // 2088 - 180
        AssertEx.Near(980.0, placement4kWork.DipLeft, 0.01);
        AssertEx.Near(1272.0, placement4kWork.DipTop, 0.01);

        // AutoHideSystemTaskbar anchors to full monitor bottom (2160)
        var placement4kFull = DockPositionCalculator.CalculateBottomPlacement(primary4k, windowDipWidth: 600, windowDipHeight: 120, autoHideSystemTaskbar: true);
        AssertEx.Equal(1980, placement4kFull.PhysicalTop); // 2160 - 180

        // Secondary monitor with negative X coordinates
        var placementLeft = DockPositionCalculator.CalculateBottomPlacement(leftSecondary, windowDipWidth: 600, windowDipHeight: 120, autoHideSystemTaskbar: false);
        AssertEx.Equal(-1260, placementLeft.PhysicalLeft); // -1920 + (1920 - 600) / 2
        AssertEx.Equal(920, placementLeft.PhysicalTop);    // 1040 - 120

        // Multi-window popup clamped strictly inside secondary monitor bounds
        var (popupLeft, popupTop) = DockPositionCalculator.ClampPopupAboveAnchor(
            leftSecondary,
            anchorCenterX: -1910,
            anchorTopY: 960,
            popupWidthPx: 260,
            popupHeightPx: 140);
        AssertEx.Equal(-1912, popupLeft); // -1920 + 8 margin
        AssertEx.Equal(810, popupTop);    // 960 - 140 - 10
    }

    [TestFact]
    public void MultiMonitorPositioning_ReentrancyGuardAndThresholdPreventCrossDpiAndNegativeCoordLoops()
    {
        // Secondary monitor with negative X and negative Y coordinates and 125% DPI (above-left of primary)
        var topLeftSecondary = new DisplayMonitorInfo(
            DeviceName: @"\\.\DISPLAY3",
            Handle: 3003,
            IsPrimary: false,
            BoundsPx: new PhysicalRect(-2560, -1440, 0, 0),
            WorkingAreaPx: new PhysicalRect(-2560, -1440, 0, -50),
            DpiScaleX: 1.25,
            DpiScaleY: 1.25);

        // Secondary monitor to the right of primary with 200% HiDPI scaling
        var rightHiDpiSecondary = new DisplayMonitorInfo(
            DeviceName: @"\\.\DISPLAY4",
            Handle: 4004,
            IsPrimary: false,
            BoundsPx: new PhysicalRect(3840, 0, 7680, 2160),
            WorkingAreaPx: new PhysicalRect(3840, 0, 7680, 2064),
            DpiScaleX: 2.0,
            DpiScaleY: 2.0);

        // Verify negative X & Y secondary placement stays strictly within its own working area (never pulled toward primary 0,0)
        var topLeftPlacement = DockPositionCalculator.CalculateBottomPlacement(
            topLeftSecondary,
            windowDipWidth: 500,
            windowDipHeight: 100,
            autoHideSystemTaskbar: false);
        AssertEx.Equal(625, topLeftPlacement.PhysicalWidth);
        AssertEx.Equal(125, topLeftPlacement.PhysicalHeight);
        AssertEx.Equal(-1592, topLeftPlacement.PhysicalLeft); // -2560 + round((2560 - 625) / 2) = -1592
        AssertEx.Equal(-175, topLeftPlacement.PhysicalTop);   // -50 - 125
        AssertEx.True(topLeftPlacement.PhysicalLeft >= topLeftSecondary.WorkingAreaPx.Left);
        AssertEx.True(topLeftPlacement.PhysicalLeft + topLeftPlacement.PhysicalWidth <= topLeftSecondary.WorkingAreaPx.Right);
        AssertEx.True(topLeftPlacement.PhysicalTop >= topLeftSecondary.WorkingAreaPx.Top);
        AssertEx.True(topLeftPlacement.PhysicalTop + topLeftPlacement.PhysicalHeight <= topLeftSecondary.WorkingAreaPx.Bottom);

        // Verify 200% HiDPI secondary monitor placement
        var hiDpiPlacement = DockPositionCalculator.CalculateBottomPlacement(
            rightHiDpiSecondary,
            windowDipWidth: 500,
            windowDipHeight: 100,
            autoHideSystemTaskbar: false);
        AssertEx.Equal(1000, hiDpiPlacement.PhysicalWidth);
        AssertEx.Equal(200, hiDpiPlacement.PhysicalHeight);
        AssertEx.Equal(5260, hiDpiPlacement.PhysicalLeft); // 3840 + (3840 - 1000) / 2
        AssertEx.Equal(1864, hiDpiPlacement.PhysicalTop);  // 2064 - 200
        AssertEx.Near(2630.0, hiDpiPlacement.DipLeft, 0.01);
        AssertEx.Near(932.0, hiDpiPlacement.DipTop, 0.01);

        // 1. Re-entrancy lock blocks recursive positioning triggered by SizeChanged / LocationChanged / OnDpiChanged
        var guard = new DockPositionGuard();
        var outerExecutions = 0;
        var nestedExecutions = 0;

        var outerRan = guard.TryExecute(() =>
        {
            outerExecutions++;
            AssertEx.True(guard.IsUpdatingPosition);

            // Simulate nested re-entrant callbacks firing during SetWindowPos / Left / Top mutation
            var nested1 = guard.TryExecute(() => nestedExecutions++);
            var nested2 = guard.TryExecute(() => nestedExecutions++);
            AssertEx.False(nested1);
            AssertEx.False(nested2);
        });

        AssertEx.True(outerRan);
        AssertEx.Equal(1, outerExecutions);
        AssertEx.Equal(0, nestedExecutions);
        AssertEx.False(guard.IsUpdatingPosition);

        // Lock must always release cleanly even if an exception is thrown
        var threw = false;
        try
        {
            guard.TryExecute(() => throw new InvalidOperationException("Simulated Win32 error"));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        AssertEx.True(threw);
        AssertEx.False(guard.IsUpdatingPosition);

        // 2. Minimum change threshold (< 0.5 DIP and <= 1px physical) prevents rounding ping-pong loops
        AssertEx.False(DockPositionGuard.ShouldUpdateDipPosition(100.0, 200.0, 100.0, 200.0));
        AssertEx.False(DockPositionGuard.ShouldUpdateDipPosition(100.0, 200.0, 100.49, 199.51));
        AssertEx.True(DockPositionGuard.ShouldUpdateDipPosition(100.0, 200.0, 100.5, 200.0));
        AssertEx.True(DockPositionGuard.ShouldUpdateDipPosition(100.0, 200.0, 100.0, 200.5));
        AssertEx.True(DockPositionGuard.ShouldUpdateDipPosition(double.NaN, double.NaN, -1273.6, -140.0));
        AssertEx.False(DockPositionGuard.ShouldUpdateDipPosition(100.0, 200.0, double.NaN, 200.0));

        var currentSecondaryRect = new PhysicalRect(
            topLeftPlacement.PhysicalLeft,
            topLeftPlacement.PhysicalTop,
            topLeftPlacement.PhysicalLeft + topLeftPlacement.PhysicalWidth,
            topLeftPlacement.PhysicalTop + topLeftPlacement.PhysicalHeight);

        AssertEx.False(DockPositionGuard.ShouldUpdatePhysicalPosition(currentSecondaryRect, topLeftPlacement.PhysicalLeft, topLeftPlacement.PhysicalTop));
        AssertEx.False(DockPositionGuard.ShouldUpdatePhysicalPosition(currentSecondaryRect, topLeftPlacement.PhysicalLeft + 1, topLeftPlacement.PhysicalTop - 1));
        AssertEx.True(DockPositionGuard.ShouldUpdatePhysicalPosition(currentSecondaryRect, topLeftPlacement.PhysicalLeft + 2, topLeftPlacement.PhysicalTop));
        AssertEx.True(DockPositionGuard.ShouldUpdatePhysicalPosition(null, topLeftPlacement.PhysicalLeft, topLeftPlacement.PhysicalTop));
    }

    [TestFact]
    public void WindowExclusionFilter_ExcludesAllOwnProcessPrimaryAndSecondaryDocks()
    {
        const uint ownPid = 4242;
        const uint chromePid = 9999;
        var excludedHandles = new HashSet<nint> { 1001, 1002, 1003 };

        // Zero handle or zero PID always excluded
        AssertEx.True(WindowExclusionFilter.ShouldExcludeWindow(0, chromePid, ownPid, excludedHandles));
        AssertEx.True(WindowExclusionFilter.ShouldExcludeWindow(5001, 0, ownPid, excludedHandles));

        // Main Dock and all secondary Dock handles owned by current process are 100% excluded
        AssertEx.True(WindowExclusionFilter.ShouldExcludeWindow(1001, ownPid, ownPid, excludedHandles));
        AssertEx.True(WindowExclusionFilter.ShouldExcludeWindow(1002, ownPid, ownPid, excludedHandles));
        AssertEx.True(WindowExclusionFilter.ShouldExcludeWindow(2001, ownPid, ownPid, null));

        // Explicitly registered Dock HWND is excluded even if PID lookup returned another value
        AssertEx.True(WindowExclusionFilter.ShouldExcludeWindow(1003, chromePid, ownPid, excludedHandles));

        // Child/owned windows whose root ancestor or owner belongs to DeskGrid process are excluded
        AssertEx.True(WindowExclusionFilter.ShouldExcludeWindow(6001, chromePid, ownPid, excludedHandles, rootAncestorProcessId: ownPid));
        AssertEx.True(WindowExclusionFilter.ShouldExcludeWindow(6002, chromePid, ownPid, excludedHandles, ownerProcessId: ownPid));

        // Normal external app windows are tracked
        AssertEx.False(WindowExclusionFilter.ShouldExcludeWindow(7001, chromePid, ownPid, excludedHandles));
    }

    [TestFact]
    public void DockLayoutMetrics_ClampsScaleAndReservesUnclippedTopHeadroom()
    {
        AssertEx.Near(0.70, DockLayoutMetrics.ClampScale(0.10));
        AssertEx.Near(1.30, DockLayoutMetrics.ClampScale(2.50));
        AssertEx.Near(1.00, DockLayoutMetrics.ClampScale(double.NaN));
        AssertEx.Near(1.00, DockLayoutMetrics.ClampScale(double.PositiveInfinity));
        AssertEx.Near(1.00, DockLayoutMetrics.ClampScale(-1.0));
        AssertEx.Near(0.85, DockLayoutMetrics.ClampScale(0.854));

        var small = DockLayoutMetrics.Calculate(dockScale: 0.70, maxMagnificationScale: 1.42, enableMagnification: true);
        var normal = DockLayoutMetrics.Calculate(dockScale: 1.00, maxMagnificationScale: 1.42, enableMagnification: true);
        var large = DockLayoutMetrics.Calculate(dockScale: 1.30, maxMagnificationScale: 1.70, enableMagnification: true);

        // Reference 40px * DockScale
        AssertEx.Near(28.0, small.IconSize);
        AssertEx.Near(40.0, normal.IconSize);
        AssertEx.Near(52.0, large.IconSize);

        // Monotonic scaling across visual elements
        AssertEx.True(small.TileWidth < normal.TileWidth && normal.TileWidth < large.TileWidth);
        AssertEx.True(small.TileHorizontalMargin < normal.TileHorizontalMargin && normal.TileHorizontalMargin < large.TileHorizontalMargin);
        AssertEx.True(small.DividerHeight < normal.DividerHeight && normal.DividerHeight < large.DividerHeight);
        AssertEx.True(small.ClockTimeFontSize < normal.ClockTimeFontSize && normal.ClockTimeFontSize < large.ClockTimeFontSize);
        AssertEx.True(small.PillPaddingHorizontal < normal.PillPaddingHorizontal && normal.PillPaddingHorizontal < large.PillPaddingHorizontal);
        AssertEx.True(small.EffectiveMagnificationRadius < normal.EffectiveMagnificationRadius && normal.EffectiveMagnificationRadius < large.EffectiveMagnificationRadius);

        // Even at max DockScale (1.30) + max MagnificationScale (1.70), top buffer must exceed upward protrusion
        var maxProtrusion = (1.70 - 1.0) * (large.TileHeight + large.MagnificationLiftPixels);
        AssertEx.True(large.TopBufferHeight > maxProtrusion,
            $"TopBufferHeight ({large.TopBufferHeight}) must exceed max upward protrusion ({maxProtrusion}).");
    }

    [TestFact]
    public void BottomEdgeHotzoneAndStateMachine_HandlesDwellAutoHideOverlaysAndPerMonitorIsolation()
    {
        var monA = new DisplayMonitorInfo(
            DeviceName: @"\\.\DISPLAY1",
            Handle: 1,
            IsPrimary: true,
            BoundsPx: new PhysicalRect(0, 0, 1920, 1080),
            WorkingAreaPx: new PhysicalRect(0, 0, 1920, 1040),
            DpiScaleX: 1.0,
            DpiScaleY: 1.0);

        var monB = new DisplayMonitorInfo(
            DeviceName: @"\\.\DISPLAY2",
            Handle: 2,
            IsPrimary: false,
            BoundsPx: new PhysicalRect(1920, 0, 3840, 1080),
            WorkingAreaPx: new PhysicalRect(1920, 0, 3840, 1040),
            DpiScaleX: 1.0,
            DpiScaleY: 1.0);

        // Bottom edge hotzone checks (<= 2px from bottom pixel 1079 -> 1077..1079)
        AssertEx.True(DockPositionCalculator.IsPointAtMonitorBottomEdge(monA, 960, 1079, edgeTolerancePx: 2));
        AssertEx.True(DockPositionCalculator.IsPointAtMonitorBottomEdge(monA, 960, 1077, edgeTolerancePx: 2));
        AssertEx.False(DockPositionCalculator.IsPointAtMonitorBottomEdge(monA, 960, 1076, edgeTolerancePx: 2));
        AssertEx.False(DockPositionCalculator.IsPointAtMonitorBottomEdge(monA, 2500, 1079, edgeTolerancePx: 2));
        AssertEx.True(DockPositionCalculator.IsPointAtMonitorBottomEdge(monB, 2500, 1079, edgeTolerancePx: 2));

        var smA = new DockEdgeRevealStateMachine(revealDwellMs: 135, hideDelayMs: 350);
        var smB = new DockEdgeRevealStateMachine(revealDwellMs: 135, hideDelayMs: 350);

        // Enter fullscreen on Monitor A only; Monitor B stays Normal
        var sA = smA.Update(0, isFullscreenHiddenActive: true, revealOnBottomEdgeEnabled: true, false, false, false);
        var sB = smB.Update(0, isFullscreenHiddenActive: false, revealOnBottomEdgeEnabled: true, false, false, false);
        AssertEx.Equal(DockEdgeRevealVisualState.FullscreenHidden, sA.State);
        AssertEx.Equal(DockEdgeRevealVisualState.Normal, sB.State);

        // Brief brush of bottom edge (< 135ms) must NOT trigger reveal
        AssertEx.False(smA.HasPendingTransition);
        smA.Update(100, true, true, isCursorAtBottomEdge: true, isCursorInsideDock: false, hasOpenInteractiveOverlay: false);
        AssertEx.True(smA.HasPendingTransition);
        sA = smA.Update(200, true, true, isCursorAtBottomEdge: true, isCursorInsideDock: false, hasOpenInteractiveOverlay: false);
        AssertEx.Equal(DockEdgeRevealVisualState.FullscreenHidden, sA.State);

        // Leave edge -> dwell timer resets
        smA.Update(220, true, true, isCursorAtBottomEdge: false, isCursorInsideDock: false, hasOpenInteractiveOverlay: false);
        AssertEx.False(smA.HasPendingTransition);

        // Re-enter edge and dwell >= 135ms -> reveals
        smA.Update(300, true, true, isCursorAtBottomEdge: true, isCursorInsideDock: false, hasOpenInteractiveOverlay: false);
        sA = smA.Update(435, true, true, isCursorAtBottomEdge: true, isCursorInsideDock: false, hasOpenInteractiveOverlay: false);
        AssertEx.Equal(DockEdgeRevealVisualState.TemporarilyRevealed, sA.State);
        AssertEx.True(sA.StateChanged);

        // Cursor moves into Dock -> remains revealed indefinitely
        sA = smA.Update(1200, true, true, isCursorAtBottomEdge: false, isCursorInsideDock: true, hasOpenInteractiveOverlay: false);
        AssertEx.Equal(DockEdgeRevealVisualState.TemporarilyRevealed, sA.State);

        // Cursor leaves Dock, but a menu/popup is open -> must NOT hide even after > 350ms
        smA.Update(1300, true, true, isCursorAtBottomEdge: false, isCursorInsideDock: false, hasOpenInteractiveOverlay: true);
        sA = smA.Update(2000, true, true, isCursorAtBottomEdge: false, isCursorInsideDock: false, hasOpenInteractiveOverlay: true);
        AssertEx.Equal(DockEdgeRevealVisualState.TemporarilyRevealed, sA.State);

        // Overlay closes while cursor is outside Dock -> stays visible before 350ms, hides at >= 350ms
        sA = smA.Update(2100, true, true, isCursorAtBottomEdge: false, isCursorInsideDock: false, hasOpenInteractiveOverlay: false);
        AssertEx.Equal(DockEdgeRevealVisualState.TemporarilyRevealed, sA.State);
        sA = smA.Update(2449, true, true, isCursorAtBottomEdge: false, isCursorInsideDock: false, hasOpenInteractiveOverlay: false);
        AssertEx.Equal(DockEdgeRevealVisualState.TemporarilyRevealed, sA.State);
        sA = smA.Update(2450, true, true, isCursorAtBottomEdge: false, isCursorInsideDock: false, hasOpenInteractiveOverlay: false);
        AssertEx.Equal(DockEdgeRevealVisualState.FullscreenHidden, sA.State);
        AssertEx.True(sA.StateChanged);

        // If RevealDockOnBottomEdge is disabled, sustained dwell never reveals
        smA.Update(3000, true, revealOnBottomEdgeEnabled: false, isCursorAtBottomEdge: true, false, false);
        sA = smA.Update(3500, true, revealOnBottomEdgeEnabled: false, isCursorAtBottomEdge: true, false, false);
        AssertEx.Equal(DockEdgeRevealVisualState.FullscreenHidden, sA.State);

        // Fullscreen exits -> returns to Normal
        sA = smA.Update(3600, isFullscreenHiddenActive: false, revealOnBottomEdgeEnabled: true, false, false, false);
        AssertEx.Equal(DockEdgeRevealVisualState.Normal, sA.State);
        AssertEx.True(sA.StateChanged);
    }

    [TestFact]
    public void DockV030Settings_PersistSanitizeAndFallbackCleanly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskGridTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new ConfigService(tempDir);
            var cfg = svc.LoadOrCreate(FirstRunInitializer.CreateDefaultConfig);

            AssertEx.Equal(DockMultiMonitorModes.PrimaryOnly, cfg.Dock.MultiMonitorMode);
            AssertEx.Near(1.00, cfg.Dock.DockScale);
            AssertEx.True(cfg.Dock.RevealDockOnBottomEdge);

            cfg.Dock.MultiMonitorMode = DockMultiMonitorModes.AllMonitors;
            cfg.Dock.DockScale = 1.24;
            cfg.Dock.RevealDockOnBottomEdge = false;
            svc.Save(cfg);

            var reloaded = svc.LoadOrCreate(() => new AppConfig());
            AssertEx.Equal(DockMultiMonitorModes.AllMonitors, reloaded.Dock.MultiMonitorMode);
            AssertEx.Near(1.24, reloaded.Dock.DockScale);
            AssertEx.False(reloaded.Dock.RevealDockOnBottomEdge);

            // Invalid/out-of-range values sanitize cleanly
            File.WriteAllText(
                svc.ConfigFilePath,
                """{ "schemaVersion": 2, "showDock": true, "dock": { "multiMonitorMode": "UnknownMode", "dockScale": 3.5 } }""");
            var sanitized = svc.LoadOrCreate(() => new AppConfig());
            AssertEx.Equal(DockMultiMonitorModes.PrimaryOnly, sanitized.Dock.MultiMonitorMode);
            AssertEx.Near(1.30, sanitized.Dock.DockScale);
            AssertEx.True(sanitized.Dock.RevealDockOnBottomEdge);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
