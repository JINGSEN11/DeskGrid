namespace DeskGrid.Core.Services;

public readonly record struct PhysicalRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

public sealed record DisplayMonitorInfo(
    string DeviceName,
    nint Handle,
    bool IsPrimary,
    PhysicalRect BoundsPx,
    PhysicalRect WorkingAreaPx,
    double DpiScaleX,
    double DpiScaleY)
{
    public double SafeDpiScaleX => double.IsFinite(DpiScaleX) && DpiScaleX > 0.1 ? DpiScaleX : 1.0;
    public double SafeDpiScaleY => double.IsFinite(DpiScaleY) && DpiScaleY > 0.1 ? DpiScaleY : 1.0;
}

public readonly record struct DockWindowPlacement(
    int PhysicalLeft,
    int PhysicalTop,
    int PhysicalWidth,
    int PhysicalHeight,
    double DipLeft,
    double DipTop);

/// <summary>
/// Re-entrancy guard and minimum-delta position filter preventing cross-monitor DPI / layout
/// ping-pong loops when positioning primary or secondary DockWindow instances.
/// </summary>
public sealed class DockPositionGuard
{
    public const double DefaultDipThreshold = 0.5;
    public const int DefaultPhysicalThresholdPx = 1;

    private bool _isUpdatingPosition;

    public bool IsUpdatingPosition => _isUpdatingPosition;

    public bool TryExecute(Action updateAction)
    {
        ArgumentNullException.ThrowIfNull(updateAction);
        if (_isUpdatingPosition)
            return false;

        _isUpdatingPosition = true;
        try
        {
            updateAction();
            return true;
        }
        finally
        {
            _isUpdatingPosition = false;
        }
    }

    public static bool ShouldUpdateDipPosition(
        double currentLeft,
        double currentTop,
        double targetLeft,
        double targetTop,
        double threshold = DefaultDipThreshold)
    {
        if (!double.IsFinite(targetLeft) || !double.IsFinite(targetTop))
            return false;

        if (!double.IsFinite(currentLeft) || !double.IsFinite(currentTop))
            return true;

        return Math.Abs(currentLeft - targetLeft) >= threshold ||
               Math.Abs(currentTop - targetTop) >= threshold;
    }

    public static bool ShouldUpdatePhysicalPosition(
        PhysicalRect? currentRect,
        int targetLeft,
        int targetTop,
        int thresholdPx = DefaultPhysicalThresholdPx)
    {
        if (!currentRect.HasValue)
            return true;

        var safeThreshold = Math.Max(0, thresholdPx);
        return Math.Abs(currentRect.Value.Left - targetLeft) > safeThreshold ||
               Math.Abs(currentRect.Value.Top - targetTop) > safeThreshold;
    }
}

/// <summary>
/// Pure filter deciding whether a window handle must be excluded from Dock running-window tracking.
/// Guarantees all own-process windows (primary Dock, secondary Docks, boxes, popups) are 100% filtered.
/// </summary>
public static class WindowExclusionFilter
{
    public static bool ShouldExcludeWindow(
        nint hwnd,
        uint windowProcessId,
        uint ownProcessId,
        IReadOnlySet<nint>? excludedHandles = null,
        uint rootAncestorProcessId = 0,
        uint ownerProcessId = 0)
    {
        if (hwnd == 0)
            return true;

        if (windowProcessId == 0 || windowProcessId == ownProcessId)
            return true;

        if (rootAncestorProcessId != 0 && rootAncestorProcessId == ownProcessId)
            return true;

        if (ownerProcessId != 0 && ownerProcessId == ownProcessId)
            return true;

        if (excludedHandles is not null && excludedHandles.Contains(hwnd))
            return true;

        return false;
    }
}

public static class DockPositionCalculator
{
    /// <summary>
    /// Computes both exact physical pixel placement (for PerMonitorV2 SetWindowPos)
    /// and DPI-converted DIP placement anchored strictly to the target monitor's own bounds/working area.
    /// Never references SystemParameters.WorkArea so secondary monitors (including negative coordinates
    /// and mixed-DPI displays) can never be pulled back toward the primary monitor.
    /// </summary>
    public static DockWindowPlacement CalculateBottomPlacement(
        DisplayMonitorInfo monitor,
        double windowDipWidth,
        double windowDipHeight,
        bool autoHideSystemTaskbar)
    {
        var dpiX = monitor.SafeDpiScaleX;
        var dpiY = monitor.SafeDpiScaleY;

        var safeDipW = double.IsFinite(windowDipWidth) && windowDipWidth > 0 ? windowDipWidth : 0.0;
        var safeDipH = double.IsFinite(windowDipHeight) && windowDipHeight > 0 ? windowDipHeight : 0.0;

        var physW = (int)Math.Round(safeDipW * dpiX);
        var physH = (int)Math.Round(safeDipH * dpiY);

        var anchorRect = autoHideSystemTaskbar ? monitor.BoundsPx : monitor.WorkingAreaPx;
        if (anchorRect.Width <= 0 || anchorRect.Height <= 0)
            anchorRect = monitor.BoundsPx;

        var rawLeft = anchorRect.Left + (int)Math.Round((anchorRect.Width - physW) / 2.0);
        var maxLeft = Math.Max(anchorRect.Left, anchorRect.Right - physW);
        var physLeft = Math.Clamp(rawLeft, anchorRect.Left, maxLeft);

        var bottomEdge = autoHideSystemTaskbar ? monitor.BoundsPx.Bottom : anchorRect.Bottom;
        var rawTop = bottomEdge - physH;
        var physTop = Math.Max(anchorRect.Top, rawTop);

        return new DockWindowPlacement(
            PhysicalLeft: physLeft,
            PhysicalTop: physTop,
            PhysicalWidth: physW,
            PhysicalHeight: physH,
            DipLeft: Math.Round(physLeft / dpiX, 2),
            DipTop: Math.Round(physTop / dpiY, 2));
    }

    /// <summary>
    /// Clamps a popup position (in physical pixels) within the target monitor's working area.
    /// </summary>
    public static (int PhysicalLeft, int PhysicalTop) ClampPopupAboveAnchor(
        DisplayMonitorInfo monitor,
        int anchorCenterX,
        int anchorTopY,
        int popupWidthPx,
        int popupHeightPx,
        int marginPx = 8,
        int verticalGapPx = 10)
    {
        var work = monitor.WorkingAreaPx.Width > 0 ? monitor.WorkingAreaPx : monitor.BoundsPx;
        var minLeft = work.Left + marginPx;
        var maxLeft = Math.Max(minLeft, work.Right - popupWidthPx - marginPx);
        var desiredLeft = anchorCenterX - popupWidthPx / 2;
        var left = Math.Clamp(desiredLeft, minLeft, maxLeft);

        var minTop = work.Top + marginPx;
        var desiredTop = anchorTopY - popupHeightPx - verticalGapPx;
        var top = Math.Max(minTop, desiredTop);
        return (left, top);
    }

    /// <summary>
    /// Selects the monitor containing the cursor (or nearest monitor if outside virtual bounds).
    /// </summary>
    public static DisplayMonitorInfo? FindMonitorForPoint(
        IReadOnlyList<DisplayMonitorInfo> monitors,
        int cursorX,
        int cursorY)
    {
        if (monitors.Count == 0)
            return null;

        foreach (var m in monitors)
        {
            if (m.BoundsPx.Contains(cursorX, cursorY))
                return m;
        }

        // Fallback to nearest monitor by squared distance to its bounding rectangle
        DisplayMonitorInfo? nearest = null;
        var bestDistSq = long.MaxValue;
        foreach (var m in monitors)
        {
            var clampedX = Math.Clamp(cursorX, m.BoundsPx.Left, Math.Max(m.BoundsPx.Left, m.BoundsPx.Right - 1));
            var clampedY = Math.Clamp(cursorY, m.BoundsPx.Top, Math.Max(m.BoundsPx.Top, m.BoundsPx.Bottom - 1));
            long dx = cursorX - clampedX;
            long dy = cursorY - clampedY;
            var distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                nearest = m;
            }
        }

        return nearest ?? monitors[0];
    }

    /// <summary>
    /// Returns true if the cursor physical coordinates lie within the monitor's bottom edge hotzone (<= edgeTolerancePx).
    /// </summary>
    public static bool IsPointAtMonitorBottomEdge(
        DisplayMonitorInfo monitor,
        int cursorX,
        int cursorY,
        int edgeTolerancePx = 2)
    {
        var bounds = monitor.BoundsPx;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        if (cursorX < bounds.Left || cursorX >= bounds.Right)
            return false;

        var safeTolerance = Math.Max(0, edgeTolerancePx);
        var bottomPixel = bounds.Bottom - 1;
        return cursorY >= bottomPixel - safeTolerance && cursorY <= bounds.Bottom;
    }

    /// <summary>
    /// Selects which monitors should host Dock instances for a given MultiMonitorMode.
    /// </summary>
    public static IReadOnlyList<DisplayMonitorInfo> SelectTargetMonitors(
        IReadOnlyList<DisplayMonitorInfo> allMonitors,
        string multiMonitorMode,
        int? cursorX = null,
        int? cursorY = null)
    {
        if (allMonitors.Count == 0)
            return [];

        var normalized = Models.DockMultiMonitorModes.Normalize(multiMonitorMode);
        if (normalized == Models.DockMultiMonitorModes.AllMonitors)
            return allMonitors;

        if (normalized == Models.DockMultiMonitorModes.FollowMouse && cursorX.HasValue && cursorY.HasValue)
        {
            var cursorMonitor = FindMonitorForPoint(allMonitors, cursorX.Value, cursorY.Value);
            if (cursorMonitor is not null)
                return [cursorMonitor];
        }

        var primary = allMonitors.FirstOrDefault(m => m.IsPrimary) ?? allMonitors[0];
        return [primary];
    }
}
