using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskGrid.App.Controls;
using DeskGrid.App.Helpers;
using DeskGrid.Core.Abstractions;
using DeskGrid.Core.Models;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Services;
using Microsoft.Win32;

namespace DeskGrid.App.Views;

public partial class DockWindow : Window
{
    private static readonly SolidColorBrush DangerMenuBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));

    private readonly AppConfig _appConfig;
    private readonly DockManager _dockManager;
    private readonly IWindowTracker _windowTracker;
    private readonly INativeDesktopIconService _nativeIcons;
    private readonly ShellIconService _shellService;

    private readonly Action _onToggleBoxes;
    private readonly Action _onCreateBox;
    private readonly Action _onOpenSettings;
    private readonly Action _onShowSystemTaskbar;
    private readonly Action _onSafeExit;

    private readonly DispatcherTimer _clockTimer;
    private readonly DockEdgeRevealStateMachine _edgeRevealStateMachine = new();
    private DisplayMonitorInfo _targetMonitor;
    private DockLayoutMetrics _layoutMetrics;
    private bool _isFullscreenOnMonitor;
    private bool _baselineTopmost = true;
    private bool _isClosed;
    private int _fadeAnimationGeneration;

    private string _lastClockTimeText = string.Empty;
    private string _lastClockDateText = string.Empty;
    private string _lastClockTooltipText = string.Empty;

    private WindowListPopup? _activePopup;
    private ContextMenu? _activeContextMenu;
    private Point _dragStartPoint;
    private DockSlotItem? _dragCandidate;
    private Border? _dragCandidateTile;
    private bool _isDragging;
    private bool _pendingRefreshAfterDrag;
    private int _dragSourcePinnedIndex = -1;
    private bool _reorderCommittedDuringDrag;

    public DisplayMonitorInfo TargetMonitor => _targetMonitor;
    public bool IsClosed => _isClosed;
    public bool IsFullscreenHidden => _isFullscreenOnMonitor && _appConfig.Dock.AutoHideOnFullscreen;
    public bool IsTemporarilyRevealed => _edgeRevealStateMachine.State == DockEdgeRevealVisualState.TemporarilyRevealed;
    public bool HasActiveOverlay =>
        PowerMenuPopup.IsOpen ||
        (_activeContextMenu is { IsOpen: true }) ||
        (_activePopup is { IsLoaded: true, IsVisible: true }) ||
        _isDragging;

    public DockWindow(
        AppConfig appConfig,
        DockManager dockManager,
        IWindowTracker windowTracker,
        INativeDesktopIconService nativeIcons,
        ShellIconService shellService,
        Action onToggleBoxes,
        Action onCreateBox,
        Action onOpenSettings,
        Action onShowSystemTaskbar,
        Action onSafeExit,
        DisplayMonitorInfo? targetMonitor = null)
    {
        InitializeComponent();

        _appConfig = appConfig;
        _dockManager = dockManager;
        _windowTracker = windowTracker;
        _nativeIcons = nativeIcons;
        _shellService = shellService;
        _onToggleBoxes = onToggleBoxes;
        _onCreateBox = onCreateBox;
        _onOpenSettings = onOpenSettings;
        _onShowSystemTaskbar = onShowSystemTaskbar;
        _onSafeExit = onSafeExit;
        _targetMonitor = targetMonitor ?? DisplayMonitorService.GetPrimaryMonitor();
        _baselineTopmost = Topmost;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _layoutMetrics = ComputeCurrentLayoutMetrics();
        ApplyLayoutMetricsToChrome(_layoutMetrics);

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClockDisplay(force: false);

        SourceInitialized += (_, _) =>
        {
            DesktopHostService.ConfigureFloatingDockWindow(this);
            RepositionDock();
        };
        Loaded += (_, _) =>
        {
            ApplySettings();
            RefreshDock();
            RepositionDock();
        };
        ContentRendered += (_, _) => RepositionDock();
        SizeChanged += (_, _) => RepositionDock();
        Closed += (_, _) =>
        {
            _isClosed = true;
            _clockTimer.Stop();
            PowerMenuPopup.IsOpen = false;
            CloseActiveContextMenu();
            try { _activePopup?.Close(); } catch { }
            _activePopup = null;
        };
    }

    public void SetTargetMonitor(DisplayMonitorInfo monitor, bool smoothTransition = false)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var monitorChanged =
            _targetMonitor.Handle != monitor.Handle ||
            !string.Equals(_targetMonitor.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase) ||
            _targetMonitor.BoundsPx != monitor.BoundsPx ||
            _targetMonitor.WorkingAreaPx != monitor.WorkingAreaPx ||
            Math.Abs(_targetMonitor.SafeDpiScaleX - monitor.SafeDpiScaleX) > 0.01 ||
            Math.Abs(_targetMonitor.SafeDpiScaleY - monitor.SafeDpiScaleY) > 0.01;

        _targetMonitor = monitor;
        if (!monitorChanged)
            return;

        RepositionDock();

        if (smoothTransition && IsVisible && !IsFullscreenHidden)
        {
            _fadeAnimationGeneration++;
            var anim = new DoubleAnimation(0.80, 1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, anim);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded)
        {
            RefreshDock(forceIconReload: true);
            RepositionDock();
        }
    }

    private DockLayoutMetrics ComputeCurrentLayoutMetrics(double? overrideDockScale = null, double? overrideMaxMagScale = null)
    {
        return DockLayoutMetrics.Calculate(
            dockScale: overrideDockScale ?? _appConfig.Dock.DockScale,
            maxMagnificationScale: overrideMaxMagScale ?? _appConfig.Dock.MagnificationScale,
            enableMagnification: _appConfig.Dock.EnableMagnification,
            baseMagnificationRadius: _appConfig.Dock.MagnificationRadius);
    }

    public void ApplyDockScale(double dockScale)
    {
        var clamped = DockLayoutMetrics.ClampScale(dockScale);
        _layoutMetrics = ComputeCurrentLayoutMetrics(overrideDockScale: clamped);

        ApplyLayoutMetricsToChrome(_layoutMetrics);
        PinnedPanel.DockScale = _layoutMetrics.Scale;
        RunningPanel.DockScale = _layoutMetrics.Scale;

        ApplyMetricsToPanelTiles(PinnedPanel, _layoutMetrics);
        ApplyMetricsToPanelTiles(RunningPanel, _layoutMetrics);

        RepositionDock();
    }

    public void ApplyMagnificationScale(double scale)
    {
        var clamped = Math.Clamp(scale, 1.05, 2.0);
        PinnedPanel.MaxScale = clamped;
        RunningPanel.MaxScale = clamped;
        _layoutMetrics = ComputeCurrentLayoutMetrics(overrideMaxMagScale: clamped);
        DockRootGrid.Margin = new Thickness(
            _layoutMetrics.SideBufferWidth,
            _layoutMetrics.TopBufferHeight,
            _layoutMetrics.SideBufferWidth,
            _layoutMetrics.BottomBufferHeight);
        RepositionDock();
    }

    public void ApplySettings()
    {
        _layoutMetrics = ComputeCurrentLayoutMetrics();
        ApplyLayoutMetricsToChrome(_layoutMetrics);

        PinnedPanel.EnableMagnification = _appConfig.Dock.EnableMagnification;
        PinnedPanel.DockScale = _layoutMetrics.Scale;
        PinnedPanel.MaxScale = _appConfig.Dock.MagnificationScale;
        PinnedPanel.EffectRadius = _appConfig.Dock.MagnificationRadius;

        RunningPanel.EnableMagnification = _appConfig.Dock.EnableMagnification;
        RunningPanel.DockScale = _layoutMetrics.Scale;
        RunningPanel.MaxScale = _appConfig.Dock.MagnificationScale;
        RunningPanel.EffectRadius = _appConfig.Dock.MagnificationRadius;

        if (!_appConfig.Dock.EnableMagnification)
        {
            PinnedPanel.SmoothResetScales();
            RunningPanel.SmoothResetScales();
        }

        ApplyMetricsToPanelTiles(PinnedPanel, _layoutMetrics);
        ApplyMetricsToPanelTiles(RunningPanel, _layoutMetrics);

        ApplyClockSettings();
        UpdateQuickToolStates();
        if (IsFullscreenHidden && !_appConfig.Dock.RevealDockOnBottomEdge)
        {
            _edgeRevealStateMachine.Reset(isFullscreenHidden: true);
            CancelFadeAnimation();
            RestoreBaselineTopmost();
            HideDock();
        }
        RepositionDock();
    }

    private void ApplyLayoutMetricsToChrome(DockLayoutMetrics m)
    {
        DockRootGrid.Margin = new Thickness(m.SideBufferWidth, m.TopBufferHeight, m.SideBufferWidth, m.BottomBufferHeight);

        var pillCorner = new CornerRadius(m.PillCornerRadius);
        DockShadowBackdrop.CornerRadius = pillCorner;
        DockPill.CornerRadius = pillCorner;
        DockSheenBorder.CornerRadius = new CornerRadius(Math.Max(1.0, m.PillCornerRadius - 1.0));
        DockContentPanel.Margin = new Thickness(m.PillPaddingHorizontal, m.PillPaddingVertical, m.PillPaddingHorizontal, m.PillPaddingVertical);

        ApplyToolButtonMetrics(ToggleNativeIconsButton, m, isFirst: true);
        ApplyToolButtonMetrics(ToggleBoxesButton, m, isFirst: false);
        ApplyToolButtonMetrics(NewBoxButton, m, isFirst: false);
        ApplyToolButtonMetrics(SettingsButton, m, isFirst: false);

        var dividerMargin = new Thickness(m.DividerHorizontalMargin, 0, m.DividerHorizontalMargin, 0);
        QuickToolsDivider.Height = m.DividerHeight;
        QuickToolsDivider.Margin = dividerMargin;
        RunningDivider.Height = m.DividerHeight;
        RunningDivider.Margin = dividerMargin;
        ClockDivider.Height = m.DividerHeight;
        ClockDivider.Margin = dividerMargin;

        DockClockButton.Padding = new Thickness(m.ClockPaddingHorizontal, m.ClockPaddingVertical, m.ClockPaddingHorizontal, m.ClockPaddingVertical);
        DockClockButton.CornerRadius = new CornerRadius(m.ClockCornerRadius);
        ClockTimeText.FontSize = m.ClockTimeFontSize;
        ClockDateText.FontSize = m.ClockDateFontSize;
    }

    private static void ApplyToolButtonMetrics(Button button, DockLayoutMetrics m, bool isFirst)
    {
        button.Width = m.ToolButtonSize;
        button.Height = m.ToolButtonSize;
        button.FontSize = m.ToolButtonFontSize;
        button.Margin = isFirst ? new Thickness(0) : new Thickness(m.ToolButtonSpacing, 0, 0, 0);
    }

    private void ApplyMetricsToPanelTiles(DockMagnifyingPanel panel, DockLayoutMetrics m)
    {
        foreach (var tile in panel.Children.OfType<Border>())
        {
            if (tile.Tag is DockSlotItem slot)
                ApplyTileLayoutMetrics(tile, slot, m);
        }
    }

    private void ApplyTileLayoutMetrics(Border tile, DockSlotItem slot, DockLayoutMetrics m)
    {
        tile.Width = m.TileWidth;
        tile.Height = m.TileHeight;
        tile.Margin = new Thickness(m.TileHorizontalMargin, 0, m.TileHorizontalMargin, 0);
        tile.Padding = new Thickness(m.TilePaddingHorizontal, m.TilePaddingTop, m.TilePaddingHorizontal, m.TilePaddingBottom);
        tile.CornerRadius = new CornerRadius(m.TileCornerRadius);

        if (tile.Child is Grid grid && grid.RowDefinitions.Count >= 2)
        {
            grid.RowDefinitions[1].Height = new GridLength(m.IndicatorRowHeight);

            if (grid.Children.Count >= 1 && grid.Children[0] is ContentControl iconHost)
            {
                if (iconHost.Content is Image img)
                {
                    img.Width = m.IconVisualSize;
                    img.Height = m.IconVisualSize;
                }
                else if (iconHost.Content is TextBlock glyph)
                {
                    glyph.FontSize = Math.Round(m.IconSize * 0.56, 1);
                }
            }

            if (grid.Children.Count >= 2 && grid.Children[1] is Border indicator)
            {
                indicator.Height = m.IndicatorHeight;
                indicator.CornerRadius = new CornerRadius(Math.Round(m.IndicatorHeight * 0.55, 1));
                ApplyIndicatorState(indicator, slot, m);
            }
        }
    }

    private void ApplyClockSettings()
    {
        var showClock = _appConfig.Dock.ShowDockClock;
        var vis = showClock ? Visibility.Visible : Visibility.Collapsed;
        ClockDivider.Visibility = vis;
        DockClockButton.Visibility = vis;

        if (showClock)
        {
            ClockDateText.Visibility = _appConfig.Dock.ClockShowDate ? Visibility.Visible : Visibility.Collapsed;
            UpdateClockDisplay(force: true);
            if (!_clockTimer.IsEnabled)
                _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
            PowerMenuPopup.IsOpen = false;
        }
    }

    private void UpdateClockDisplay(bool force)
    {
        if (!_appConfig.Dock.ShowDockClock)
            return;

        var now = DateTime.Now;
        var timeText = DockClockFormatter.FormatTime(now, _appConfig.Dock.ClockUse24Hour, _appConfig.Dock.ClockShowSeconds);
        var dateText = DockClockFormatter.FormatDate(now);
        var tooltipText = DockClockFormatter.FormatTooltip(now, _appConfig.Dock.ClockUse24Hour);

        if (force || !string.Equals(_lastClockTimeText, timeText, StringComparison.Ordinal))
        {
            _lastClockTimeText = timeText;
            ClockTimeText.Text = timeText;
        }

        if (force || !string.Equals(_lastClockDateText, dateText, StringComparison.Ordinal))
        {
            _lastClockDateText = dateText;
            ClockDateText.Text = dateText;
        }

        if (force || !string.Equals(_lastClockTooltipText, tooltipText, StringComparison.Ordinal))
        {
            _lastClockTooltipText = tooltipText;
            DockClockButton.ToolTip = tooltipText;
        }
    }

    public void UpdateQuickToolStates()
    {
        var nativeVisible = _nativeIcons.AreIconsVisible();
        ToggleNativeIconsButton.Content = nativeVisible ? "\uE7B3" : "\uED1A";
        ToggleNativeIconsButton.Foreground = nativeVisible
            ? (Brush)FindResource("DgTextPrimaryBrush")
            : (Brush)FindResource("DgAccentBrush");
        ToggleNativeIconsButton.ToolTip = nativeVisible
            ? "点击隐藏系统原生桌面图标"
            : "原生桌面图标已隐藏（点击恢复显示）";

        ToggleBoxesButton.Foreground = _appConfig.Behavior.BoxesVisible
            ? (Brush)FindResource("DgTextPrimaryBrush")
            : (Brush)FindResource("DgAccentBrush");
        ToggleBoxesButton.ToolTip = _appConfig.Behavior.BoxesVisible
            ? "点击隐藏所有桌面盒子"
            : "桌面盒子已隐藏（点击恢复显示）";
    }

    public void RefreshDock(bool forceIconReload = false)
    {
        if (_isClosed)
            return;

        if (_isDragging)
        {
            _pendingRefreshAfterDrag = true;
            return;
        }

        var running = _windowTracker.GetRunningWindows();
        var snapshot = _dockManager.BuildSnapshot(running);
        var metrics = _layoutMetrics;

        var pinnedChanged = SyncPanelSlots(PinnedPanel, snapshot.PinnedSlots, metrics, forceIconReload);
        var runningChanged = SyncPanelSlots(RunningPanel, snapshot.RunningOnlySlots, metrics, forceIconReload);

        var prevDividerVis = RunningDivider.Visibility;
        RunningDivider.Visibility = snapshot.RunningOnlySlots.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateQuickToolStates();
        if (pinnedChanged || runningChanged || prevDividerVis != RunningDivider.Visibility)
            RepositionDock();
    }

    private bool SyncPanelSlots(
        DockMagnifyingPanel panel,
        IReadOnlyList<DockSlotItem> slots,
        DockLayoutMetrics metrics,
        bool forceIconReload)
    {
        var sameOrder = panel.Children.Count == slots.Count;
        if (sameOrder)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (panel.Children[i] is not Border tile ||
                    tile.Tag is not DockSlotItem prev ||
                    !string.Equals(prev.Id, slots[i].Id, StringComparison.Ordinal))
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        if (sameOrder)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                var tile = (Border)panel.Children[i];
                var prev = (DockSlotItem)tile.Tag;
                UpdateDockSlotElement(tile, prev, slots[i], metrics, forceIconReload);
            }
            return false;
        }

        // Preserve surviving tiles so currently magnified icons can smoothly animate back to 1.0 instead of snapping
        var existingById = new Dictionary<string, Border>(StringComparer.Ordinal);
        foreach (var child in panel.Children.OfType<Border>())
        {
            if (child.Tag is DockSlotItem existingSlot)
                existingById.TryAdd(existingSlot.Id, child);
        }

        panel.Children.Clear();
        foreach (var slot in slots)
        {
            if (existingById.TryGetValue(slot.Id, out var reusedTile) && reusedTile.Tag is DockSlotItem prevSlot)
            {
                UpdateDockSlotElement(reusedTile, prevSlot, slot, metrics, forceIconReload);
                panel.Children.Add(reusedTile);
            }
            else
            {
                panel.Children.Add(BuildDockSlotElement(slot, metrics));
            }
        }

        panel.SmoothResetScales();
        return true;
    }

    public void RepositionDock()
    {
        if (_isClosed || !IsLoaded) return;

        UpdateLayout();
        var placement = DockPositionCalculator.CalculateBottomPlacement(
            _targetMonitor,
            ActualWidth,
            ActualHeight,
            _appConfig.Dock.AutoHideSystemTaskbar);

        if (!DisplayMonitorService.MoveWindowPhysical(this, placement.PhysicalLeft, placement.PhysicalTop))
        {
            Left = placement.DipLeft;
            Top = placement.DipTop;
        }
    }

    public void SetFullscreenHidden(bool isFullscreenOnMonitor)
    {
        if (_isClosed)
            return;

        _isFullscreenOnMonitor = isFullscreenOnMonitor;

        if (!_appConfig.ShowDock || !_appConfig.Dock.Enabled)
        {
            _edgeRevealStateMachine.Reset(isFullscreenHidden: false);
            HideDock();
            return;
        }

        var shouldHideForFullscreen = isFullscreenOnMonitor && _appConfig.Dock.AutoHideOnFullscreen;
        if (!shouldHideForFullscreen)
        {
            _edgeRevealStateMachine.Reset(isFullscreenHidden: false);
            RestoreNormalVisibility();
            return;
        }

        // Fullscreen auto-hide is active on this monitor
        if (!_appConfig.Dock.RevealDockOnBottomEdge ||
            _edgeRevealStateMachine.State == DockEdgeRevealVisualState.Normal)
        {
            _edgeRevealStateMachine.Reset(isFullscreenHidden: true);
            CancelFadeAnimation();
            RestoreBaselineTopmost();
            HideDock();
        }
    }

    public void UpdateEdgeRevealTick(int cursorX, int cursorY, long nowMs)
    {
        if (_isClosed || !_appConfig.ShowDock || !_appConfig.Dock.Enabled)
            return;

        var isAtBottomEdge = DockPositionCalculator.IsPointAtMonitorBottomEdge(
            _targetMonitor,
            cursorX,
            cursorY,
            edgeTolerancePx: 2);

        var isInsideDock = IsVisible &&
                           DisplayMonitorService.TryGetWindowPhysicalRect(this, out var winRect) &&
                           winRect.Contains(cursorX, cursorY);

        var step = _edgeRevealStateMachine.Update(
            nowMs: nowMs,
            isFullscreenHiddenActive: _isFullscreenOnMonitor && _appConfig.Dock.AutoHideOnFullscreen,
            revealOnBottomEdgeEnabled: _appConfig.Dock.RevealDockOnBottomEdge,
            isCursorAtBottomEdge: isAtBottomEdge,
            isCursorInsideDock: isInsideDock,
            hasOpenInteractiveOverlay: HasActiveOverlay);

        if (!step.StateChanged)
            return;

        switch (step.State)
        {
            case DockEdgeRevealVisualState.TemporarilyRevealed:
                RevealTemporarilyOverFullscreen();
                break;

            case DockEdgeRevealVisualState.FullscreenHidden:
                HideBackToFullscreenQuietly();
                break;

            case DockEdgeRevealVisualState.Normal:
                RestoreNormalVisibility();
                break;
        }
    }

    private void RevealTemporarilyOverFullscreen()
    {
        CancelFadeAnimation();
        var wasVisible = IsVisible;

        Topmost = true;
        if (!wasVisible)
        {
            Opacity = 0.0;
            Show();
        }

        ApplyClockSettings();
        RepositionDock();
        DisplayMonitorService.SetWindowTopmostNoActivate(this, topmost: true);

        var anim = new DoubleAnimation(Opacity, 1.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void HideBackToFullscreenQuietly()
    {
        if (!IsVisible)
        {
            RestoreBaselineTopmost();
            return;
        }

        var generation = ++_fadeAnimationGeneration;
        var anim = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            if (_isClosed || generation != _fadeAnimationGeneration)
                return;

            if (_edgeRevealStateMachine.State == DockEdgeRevealVisualState.FullscreenHidden)
            {
                BeginAnimation(OpacityProperty, null);
                RestoreBaselineTopmost();
                HideDock();
                Opacity = 1.0;
            }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void RestoreNormalVisibility()
    {
        CancelFadeAnimation();
        RestoreBaselineTopmost();
        Opacity = 1.0;

        if (!IsVisible)
            Show();

        ApplyClockSettings();
        RepositionDock();
    }

    private void CancelFadeAnimation()
    {
        _fadeAnimationGeneration++;
        BeginAnimation(OpacityProperty, null);
    }

    private void RestoreBaselineTopmost()
    {
        Topmost = _baselineTopmost;
        DisplayMonitorService.SetWindowTopmostNoActivate(this, _baselineTopmost);
    }

    public void HideDock()
    {
        CancelFadeAnimation();
        _clockTimer.Stop();
        PowerMenuPopup.IsOpen = false;
        CloseActiveContextMenu();
        try { _activePopup?.Close(); } catch { }
        _activePopup = null;
        if (IsVisible)
            Hide();
    }

    private int GetHighDpiDockIconRequestPixels(double iconSize)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var effectiveDpi = Math.Max(
            Math.Max(dpi.DpiScaleX, dpi.DpiScaleY),
            Math.Max(_targetMonitor.SafeDpiScaleX, _targetMonitor.SafeDpiScaleY));
        var maxScale = _appConfig.Dock.EnableMagnification ? Math.Max(1.0, _appConfig.Dock.MagnificationScale) : 1.0;
        var baseRequestSize = Math.Max(iconSize, DockLayoutMetrics.ReferenceIconSize * DockLayoutMetrics.MaxScale);
        return ShellIconService.ComputePhysicalIconPixels(baseRequestSize * maxScale, effectiveDpi);
    }

    private Border BuildDockSlotElement(DockSlotItem slot, DockLayoutMetrics metrics)
    {
        var tile = new Border
        {
            Width = metrics.TileWidth,
            Height = metrics.TileHeight,
            Margin = new Thickness(metrics.TileHorizontalMargin, 0, metrics.TileHorizontalMargin, 0),
            Padding = new Thickness(metrics.TilePaddingHorizontal, metrics.TilePaddingTop, metrics.TilePaddingHorizontal, metrics.TilePaddingBottom),
            CornerRadius = new CornerRadius(metrics.TileCornerRadius),
            Background = slot.IsActive ? (Brush)FindResource("DgHoverBrush") : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = slot,
            ToolTip = BuildSlotTooltip(slot),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(metrics.IndicatorRowHeight) });

        var iconHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            IsTabStop = false,
            Content = BuildSlotIconVisual(slot, metrics)
        };
        Grid.SetRow(iconHost, 0);
        grid.Children.Add(iconHost);

        var indicator = new Border
        {
            Height = metrics.IndicatorHeight,
            CornerRadius = new CornerRadius(Math.Round(metrics.IndicatorHeight * 0.55, 1)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ApplyIndicatorState(indicator, slot, metrics);
        Grid.SetRow(indicator, 1);
        grid.Children.Add(indicator);

        tile.Child = grid;

        tile.MouseEnter += (_, _) =>
        {
            if (tile.Tag is DockSlotItem current && !current.IsActive)
                tile.Background = (Brush)FindResource("DgHoverBrush");
        };
        tile.MouseLeave += (_, _) =>
        {
            if (tile.Tag is DockSlotItem current && !current.IsActive)
                tile.Background = Brushes.Transparent;
        };

        tile.MouseLeftButtonDown += (_, e) =>
        {
            _dragStartPoint = e.GetPosition(this);
            _dragCandidate = tile.Tag as DockSlotItem;
            _dragCandidateTile = tile;
            _isDragging = false;
        };

        tile.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || _isDragging)
                return;

            var pt = e.GetPosition(this);
            var thresholdX = Math.Max(5.0, SystemParameters.MinimumHorizontalDragDistance);
            var thresholdY = Math.Max(5.0, SystemParameters.MinimumVerticalDragDistance);
            if (Math.Abs(pt.X - _dragStartPoint.X) < thresholdX &&
                Math.Abs(pt.Y - _dragStartPoint.Y) < thresholdY)
            {
                return;
            }

            var candidate = _dragCandidate;
            var draggedTile = _dragCandidateTile;
            if (!candidate.IsPinned && string.IsNullOrWhiteSpace(candidate.TargetPath))
            {
                _dragCandidate = null;
                _dragCandidateTile = null;
                return;
            }

            _isDragging = true;
            _pendingRefreshAfterDrag = false;
            _reorderCommittedDuringDrag = false;
            _dragSourcePinnedIndex = candidate.IsPinned ? FindPinnedTileIndex(candidate.Id) : -1;

            if (_dragSourcePinnedIndex >= 0)
            {
                PinnedPanel.BeginDragReorder();
                if (draggedTile is not null)
                    draggedTile.Opacity = 0.38;
            }

            try
            {
                var payload = new InternalDragPayload(
                    InternalDragSource.Dock,
                    null,
                    candidate.Id,
                    candidate.TargetPath,
                    candidate.DisplayName,
                    IsPinned: candidate.IsPinned);
                var data = DragDropFormats.CreateDataObject(payload);
                var allowedEffects = candidate.IsPinned
                    ? DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link
                    : DragDropEffects.Copy | DragDropEffects.Link;
                DragDrop.DoDragDrop(tile, data, allowedEffects);
            }
            finally
            {
                if (draggedTile is not null)
                    draggedTile.Opacity = 1.0;

                HidePinnedDropIndicator();
                if (_dragSourcePinnedIndex >= 0)
                    PinnedPanel.EndDragReorder(snapOffsets: _reorderCommittedDuringDrag);

                var needsRefresh = _pendingRefreshAfterDrag;
                _dragCandidate = null;
                _dragCandidateTile = null;
                _isDragging = false;
                _dragSourcePinnedIndex = -1;
                _reorderCommittedDuringDrag = false;
                _pendingRefreshAfterDrag = false;

                if (needsRefresh)
                    RefreshDock();
            }
        };

        tile.MouseLeftButtonUp += (_, _) =>
        {
            var hadCandidate = _dragCandidate is not null;
            _dragCandidate = null;
            _dragCandidateTile = null;
            if (_isDragging || !hadCandidate) return;
            if (tile.Tag is DockSlotItem current)
                HandleSlotClick(current, tile);
        };

        tile.MouseRightButtonUp += (_, e) =>
        {
            if (tile.Tag is DockSlotItem current)
                ShowSlotContextMenu(current, tile);
            e.Handled = true;
        };

        return tile;
    }

    private void UpdateDockSlotElement(Border tile, DockSlotItem prev, DockSlotItem next, DockLayoutMetrics metrics, bool forceIconReload)
    {
        tile.Tag = next;
        tile.ToolTip = BuildSlotTooltip(next);
        tile.Background = next.IsActive || tile.IsMouseOver
            ? (Brush)FindResource("DgHoverBrush")
            : Brushes.Transparent;

        ApplyTileLayoutMetrics(tile, next, metrics);

        if (tile.Child is Grid grid && grid.Children.Count >= 2)
        {
            if (grid.Children[0] is ContentControl iconHost &&
                (forceIconReload ||
                 prev.PrimaryWindowHandle != next.PrimaryWindowHandle ||
                 prev.IsRunning != next.IsRunning ||
                 !string.Equals(prev.TargetPath, next.TargetPath, StringComparison.OrdinalIgnoreCase)))
            {
                iconHost.Content = BuildSlotIconVisual(next, metrics);
            }

            if (grid.Children[1] is Border indicator)
                ApplyIndicatorState(indicator, next, metrics);
        }
    }

    private UIElement BuildSlotIconVisual(DockSlotItem slot, DockLayoutMetrics metrics)
    {
        var requestPx = GetHighDpiDockIconRequestPixels(metrics.IconSize);
        var bmp = slot.IsRunning
            ? _shellService.GetIconForWindow(slot.PrimaryWindowHandle, slot.TargetPath, requestPx)
            : _shellService.GetIconForPath(slot.TargetPath, requestPx);

        if (bmp is not null)
        {
            var img = new Image
            {
                Source = bmp,
                Width = metrics.IconVisualSize,
                Height = metrics.IconVisualSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        return new TextBlock
        {
            Text = slot.Kind == BoxItemKind.Folder ? "\uE8B7" : "\uE756",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = Math.Round(metrics.IconSize * 0.56, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("DgAccentBrush")
        };
    }

    private void ApplyIndicatorState(Border indicator, DockSlotItem slot, DockLayoutMetrics metrics)
    {
        if (!slot.IsRunning)
        {
            indicator.Visibility = Visibility.Collapsed;
            return;
        }

        indicator.Visibility = Visibility.Visible;
        indicator.Width = slot.IsActive
            ? metrics.IndicatorActiveWidth
            : (slot.RunningCount > 1 ? metrics.IndicatorMultiWidth : metrics.IndicatorSingleWidth);
        indicator.Background = slot.IsActive
            ? (Brush)FindResource("DgAccentBrush")
            : (Brush)FindResource("DgTextSecondaryBrush");
    }

    private static string BuildSlotTooltip(DockSlotItem slot)
    {
        if (slot.RunningCount == 0)
            return slot.DisplayName;
        if (slot.RunningCount == 1)
            return $"{slot.DisplayName}\n{slot.RunningWindows[0].Title}";
        return $"{slot.DisplayName}（{slot.RunningCount} 个窗口）";
    }

    private void HandleSlotClick(DockSlotItem slot, FrameworkElement anchor)
    {
        PowerMenuPopup.IsOpen = false;
        var decision = DockManager.DecideClickAction(slot);
        switch (decision.Action)
        {
            case DockClickActionKind.Launch:
                try { _shellService.OpenPath(slot.TargetPath, slot.Arguments); }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法启动 \"{slot.DisplayName}\"：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;

            case DockClickActionKind.ActivateWindow:
                _windowTracker.ActivateWindow(decision.WindowHandle);
                break;

            case DockClickActionKind.MinimizeWindow:
                _windowTracker.MinimizeWindow(decision.WindowHandle);
                break;

            case DockClickActionKind.ShowWindowListPopup:
                ShowMultiWindowPopup(slot, anchor);
                break;
        }
    }

    private void ShowMultiWindowPopup(DockSlotItem slot, FrameworkElement anchor)
    {
        PowerMenuPopup.IsOpen = false;
        PinnedPanel.SmoothResetScales();
        RunningPanel.SmoothResetScales();
        try { _activePopup?.Close(); } catch { }

        var popup = new WindowListPopup(
            slot.DisplayName,
            slot.RunningWindows,
            hwnd => _windowTracker.ActivateWindow(hwnd),
            hwnd => _windowTracker.CloseWindow(hwnd));

        _activePopup = popup;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activePopup, popup))
                _activePopup = null;
        };

        var screenPt = anchor.PointToScreen(new Point(anchor.ActualWidth / 2.0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);

        popup.Loaded += (_, _) =>
        {
            var popupPhysW = (int)Math.Round(popup.ActualWidth * dpi.DpiScaleX);
            var popupPhysH = (int)Math.Round(popup.ActualHeight * dpi.DpiScaleY);
            var (physLeft, physTop) = DockPositionCalculator.ClampPopupAboveAnchor(
                _targetMonitor,
                (int)Math.Round(screenPt.X),
                (int)Math.Round(screenPt.Y),
                popupPhysW,
                popupPhysH,
                marginPx: (int)Math.Round(8 * dpi.DpiScaleX),
                verticalGapPx: (int)Math.Round(10 * dpi.DpiScaleY));

            if (!DisplayMonitorService.MoveWindowPhysical(popup, physLeft, physTop))
            {
                popup.Left = physLeft / dpi.DpiScaleX;
                popup.Top = physTop / dpi.DpiScaleY;
            }
        };
        popup.Show();
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void CloseActiveContextMenu()
    {
        if (_activeContextMenu is not null)
        {
            _activeContextMenu.IsOpen = false;
            _activeContextMenu = null;
        }
    }

    private void AppendDockSystemMenuItems(ContextMenu menu)
    {
        menu.Items.Add(new Separator());

        if (_appConfig.Dock.AutoHideSystemTaskbar)
        {
            var showTaskbarMi = new MenuItem { Header = "显示系统任务栏" };
            showTaskbarMi.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _onShowSystemTaskbar();
            };
            menu.Items.Add(showTaskbarMi);
        }

        var safeExitMi = new MenuItem
        {
            Header = "安全退出 DeskGrid",
            Foreground = DangerMenuBrush,
            FontWeight = FontWeights.SemiBold
        };
        safeExitMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onSafeExit();
        };
        menu.Items.Add(safeExitMi);
    }

    private void OnDockBackgroundRightClick(object sender, MouseButtonEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        CloseActiveContextMenu();
        PinnedPanel.SmoothResetScales();
        RunningPanel.SmoothResetScales();

        var menu = ThemePalette.CreateThemedContextMenu();
        menu.Placement = PlacementMode.MousePoint;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
                _activeContextMenu = null;
        };

        var addPinnedMi = new MenuItem { Header = "添加固定应用..." };
        addPinnedMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            PromptAddPinnedApps();
        };
        menu.Items.Add(addPinnedMi);

        menu.Items.Add(new Separator());

        var newBoxMi = new MenuItem { Header = "新建桌面盒子..." };
        newBoxMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onCreateBox();
        };
        menu.Items.Add(newBoxMi);

        var settingsMi = new MenuItem { Header = "偏好设置..." };
        settingsMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onOpenSettings();
        };
        menu.Items.Add(settingsMi);

        AppendDockSystemMenuItems(menu);

        menu.PlacementTarget = DockPill;
        _activeContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void PromptAddPinnedApps()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要固定到 Dock 栏的应用或快捷方式",
            Filter = "应用程序与快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            Multiselect = true,
            DereferenceLinks = false
        };

        if (dlg.ShowDialog() == true)
        {
            var pinnedAny = false;
            foreach (var file in dlg.FileNames)
            {
                if (PinDroppedOrSelectedPath(file, displayName: null))
                    pinnedAny = true;
            }

            if (pinnedAny)
                RefreshDock();
        }
    }

    private bool PinDroppedOrSelectedPath(string path, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var resolved = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? _shellService.ResolveShortcutTarget(path)
            : null;
        return _dockManager.PinItem(path, displayName, resolved);
    }

    private void ShowSlotContextMenu(DockSlotItem slot, FrameworkElement target)
    {
        PowerMenuPopup.IsOpen = false;
        PinnedPanel.SmoothResetScales();
        RunningPanel.SmoothResetScales();
        var menu = ThemePalette.CreateThemedContextMenu();

        var targetPath = !string.IsNullOrWhiteSpace(slot.TargetPath)
            ? slot.TargetPath
            : slot.RunningWindows.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w.ExecutablePath))?.ExecutablePath ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            var openNewMi = new MenuItem { Header = slot.IsRunning ? "打开新实例" : "打开" };
            openNewMi.Click += (_, _) =>
            {
                try { _shellService.OpenPath(targetPath, slot.Arguments); } catch { }
            };
            menu.Items.Add(openNewMi);
        }

        if (slot.IsPinned)
        {
            var unpinMi = new MenuItem { Header = "从 Dock 取消固定" };
            unpinMi.Click += (_, _) => _dockManager.UnpinItem(slot.Id);
            menu.Items.Add(unpinMi);
        }
        else
        {
            var pinMi = new MenuItem
            {
                Header = "固定到 Dock",
                IsEnabled = !string.IsNullOrWhiteSpace(targetPath)
            };
            pinMi.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(targetPath))
                {
                    var resolved = targetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                        ? _shellService.ResolveShortcutTarget(targetPath)
                        : targetPath;
                    if (_dockManager.PinItem(targetPath, slot.DisplayName, resolved))
                        RefreshDock();
                }
            };
            menu.Items.Add(pinMi);
        }

        if (slot.IsRunning)
        {
            menu.Items.Add(new Separator());
            var closeAllMi = new MenuItem
            {
                Header = slot.RunningCount > 1 ? $"关闭全部 {slot.RunningCount} 个窗口" : "关闭窗口"
            };
            closeAllMi.Click += (_, _) =>
            {
                foreach (var w in slot.RunningWindows)
                    _windowTracker.CloseWindow(w.Handle);
            };
            menu.Items.Add(closeAllMi);
        }

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
                _activeContextMenu = null;
        };
        menu.PlacementTarget = target;
        _activeContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OnClockMouseEnter(object sender, MouseEventArgs e)
    {
        DockClockButton.Background = (Brush)FindResource("DgHoverBrush");
    }

    private void OnClockMouseLeave(object sender, MouseEventArgs e)
    {
        if (!PowerMenuPopup.IsOpen)
            DockClockButton.Background = Brushes.Transparent;
    }

    private void OnPowerPopupClosed(object? sender, EventArgs e)
    {
        if (!DockClockButton.IsMouseOver)
            DockClockButton.Background = Brushes.Transparent;
    }

    private void OnClockClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PinnedPanel.SmoothResetScales();
        RunningPanel.SmoothResetScales();
        try { _activePopup?.Close(); } catch { }
        _activePopup = null;

        PowerMenuPopup.IsOpen = !PowerMenuPopup.IsOpen;
        if (!PowerMenuPopup.IsOpen && !DockClockButton.IsMouseOver)
            DockClockButton.Background = Brushes.Transparent;
    }

    private void OnPowerLockClick(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        DockClockButton.Background = Brushes.Transparent;
        SystemPowerService.LockScreen();
    }

    private void OnPowerSleepClick(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        DockClockButton.Background = Brushes.Transparent;
        SystemPowerService.Sleep();
    }

    private void OnPowerRestartClick(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        DockClockButton.Background = Brushes.Transparent;

        var confirm = MessageBox.Show(
            "确定要立即重启计算机吗？请先保存所有未保存的工作。",
            "DeskGrid - 重启确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            try
            {
                SystemPowerService.Restart();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"执行重启失败：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnPowerShutdownClick(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        DockClockButton.Background = Brushes.Transparent;

        var confirm = MessageBox.Show(
            "确定要立即关闭计算机吗？请先保存所有未保存的工作。",
            "DeskGrid - 关机确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            try
            {
                SystemPowerService.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"执行关机失败：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnToggleNativeIcons(object sender, RoutedEventArgs e)
    {
        _nativeIcons.ToggleIcons();
        UpdateQuickToolStates();
    }

    private void OnToggleBoxes(object sender, RoutedEventArgs e)
    {
        _onToggleBoxes();
        UpdateQuickToolStates();
    }

    private void OnNewBox(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        _onCreateBox();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        CloseActiveContextMenu();
        PinnedPanel.SmoothResetScales();
        RunningPanel.SmoothResetScales();
        try { _activePopup?.Close(); } catch { }
        _activePopup = null;

        var menu = ThemePalette.CreateThemedContextMenu();
        menu.Placement = PlacementMode.Top;
        menu.VerticalOffset = -8;
        menu.PlacementTarget = SettingsButton;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
                _activeContextMenu = null;
        };

        var settingsMi = new MenuItem { Header = "偏好设置..." };
        settingsMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onOpenSettings();
        };
        menu.Items.Add(settingsMi);

        var addPinnedMi = new MenuItem { Header = "添加固定应用..." };
        addPinnedMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            PromptAddPinnedApps();
        };
        menu.Items.Add(addPinnedMi);

        var newBoxMi = new MenuItem { Header = "新建桌面盒子..." };
        newBoxMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onCreateBox();
        };
        menu.Items.Add(newBoxMi);

        AppendDockSystemMenuItems(menu);

        _activeContextMenu = menu;
        menu.IsOpen = true;
    }

    private int FindPinnedTileIndex(string itemId)
    {
        for (var i = 0; i < PinnedPanel.Children.Count; i++)
        {
            if (PinnedPanel.Children[i] is Border b &&
                b.Tag is DockSlotItem slot &&
                string.Equals(slot.Id, itemId, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private bool TryComputePinnedReorder(DragEventArgs e, string itemId, out int oldIndex, out int newIndex, out HorizontalDropTarget dropTarget)
    {
        oldIndex = _dragSourcePinnedIndex >= 0 ? _dragSourcePinnedIndex : FindPinnedTileIndex(itemId);
        newIndex = oldIndex;
        dropTarget = default;

        if (oldIndex < 0 || PinnedPanel.Children.Count == 0)
            return false;

        var ptInPinned = e.GetPosition(PinnedPanel);
        // Restrict strictly to the center PinnedPanel zone; exclude QuickToolsPanel (left), RunningPanel and Clock (right)
        var inPinnedZone = ptInPinned.X >= -6.0 &&
                           ptInPinned.X <= PinnedPanel.ActualWidth + 6.0 &&
                           ptInPinned.Y >= -28.0 &&
                           ptInPinned.Y <= PinnedPanel.ActualHeight + 16.0;
        if (!inPinnedZone)
            return false;

        var bounds = PinnedPanel.GetLayoutSlotBounds();
        if (bounds.Count == 0)
            return false;

        dropTarget = DragReorderCalculator.ComputeHorizontalDropTarget(ptInPinned.X, bounds);
        newIndex = DragReorderCalculator.ResolveTargetIndex(oldIndex, dropTarget.InsertionSlot, bounds.Count);
        return true;
    }

    private void ShowPinnedDropIndicator(double indicatorX)
    {
        var indicatorHeight = Math.Max(20.0, Math.Min(PinnedPanel.ActualHeight - 6.0, _layoutMetrics.IconSize * 0.88));
        PinnedDropIndicator.Height = indicatorHeight;
        var top = Math.Max(0.0, (PinnedPanel.ActualHeight - indicatorHeight) * 0.5 - 2.0);
        Canvas.SetLeft(PinnedDropIndicator, Math.Round(indicatorX - PinnedDropIndicator.Width * 0.5));
        Canvas.SetTop(PinnedDropIndicator, Math.Round(top));
        PinnedDropIndicator.Visibility = Visibility.Visible;
    }

    private void HidePinnedDropIndicator()
    {
        PinnedDropIndicator.Visibility = Visibility.Collapsed;
    }

    private void OnDockDragEnter(object sender, DragEventArgs e) => OnDockDragOver(sender, e);

    private void OnDockDragOver(object sender, DragEventArgs e)
    {
        var internalPayload = DragDropFormats.TryGetInternalPayload(e.Data);
        if (internalPayload?.Source == InternalDragSource.Dock)
        {
            DockPill.SetResourceReference(Border.BorderBrushProperty, "DgGlassBorderBrush");

            if (internalPayload.IsPinned &&
                TryComputePinnedReorder(e, internalPayload.ItemId, out var oldIndex, out var newIndex, out var dropTarget))
            {
                e.Effects = DragDropEffects.Move;
                if (newIndex != oldIndex)
                {
                    PinnedPanel.SetDragInsertionSlot(dropTarget.InsertionSlot);
                    ShowPinnedDropIndicator(dropTarget.IndicatorX);
                }
                else
                {
                    PinnedPanel.SetDragInsertionSlot(null);
                    HidePinnedDropIndicator();
                }
            }
            else
            {
                // Explicitly forbid dropping into Quick Tools (left), Running Windows, or Clock (right) zones
                PinnedPanel.SetDragInsertionSlot(null);
                HidePinnedDropIndicator();
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
            return;
        }

        HidePinnedDropIndicator();
        var paths = DragDropFormats.GetDroppedPaths(e.Data);
        if (paths.Length > 0)
        {
            e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Link)
                ? DragDropEffects.Link
                : (e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.Move);
            DockPill.BorderBrush = (Brush)FindResource("DgAccentBrush");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDockDragLeave(object sender, DragEventArgs e)
    {
        DockPill.SetResourceReference(Border.BorderBrushProperty, "DgGlassBorderBrush");
        HidePinnedDropIndicator();
        PinnedPanel.SetDragInsertionSlot(null);
    }

    private void OnDockDrop(object sender, DragEventArgs e)
    {
        DockPill.SetResourceReference(Border.BorderBrushProperty, "DgGlassBorderBrush");
        HidePinnedDropIndicator();

        var internalPayload = DragDropFormats.TryGetInternalPayload(e.Data);
        if (internalPayload?.Source == InternalDragSource.Dock)
        {
            if (internalPayload.IsPinned &&
                TryComputePinnedReorder(e, internalPayload.ItemId, out var oldIndex, out var newIndex, out _) &&
                newIndex != oldIndex)
            {
                _reorderCommittedDuringDrag = true;
                _pendingRefreshAfterDrag = true;
                _dockManager.ReorderPinnedItem(oldIndex, newIndex);
            }
            else
            {
                PinnedPanel.SetDragInsertionSlot(null);
            }

            e.Handled = true;
            return;
        }

        var paths = DragDropFormats.GetDroppedPaths(e.Data);
        foreach (var path in paths)
        {
            var name = internalPayload is not null && internalPayload.TargetPath == path
                ? internalPayload.DisplayName
                : null;
            PinDroppedOrSelectedPath(path, name);
        }

        RefreshDock();
        e.Handled = true;
    }
}
