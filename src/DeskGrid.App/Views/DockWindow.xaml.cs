using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly AppConfig _appConfig;
    private readonly DockManager _dockManager;
    private readonly IWindowTracker _windowTracker;
    private readonly INativeDesktopIconService _nativeIcons;
    private readonly ShellIconService _shellService;

    private readonly Action _onToggleBoxes;
    private readonly Action _onCreateBox;
    private readonly Action _onOpenSettings;

    private readonly DispatcherTimer _clockTimer;
    private string _lastClockTimeText = string.Empty;
    private string _lastClockDateText = string.Empty;
    private string _lastClockTooltipText = string.Empty;

    private WindowListPopup? _activePopup;
    private Point _dragStartPoint;
    private DockSlotItem? _dragCandidate;
    private Border? _dragCandidateTile;
    private bool _isDragging;
    private bool _pendingRefreshAfterDrag;
    private int _dragSourcePinnedIndex = -1;
    private bool _reorderCommittedDuringDrag;

    public DockWindow(
        AppConfig appConfig,
        DockManager dockManager,
        IWindowTracker windowTracker,
        INativeDesktopIconService nativeIcons,
        ShellIconService shellService,
        Action onToggleBoxes,
        Action onCreateBox,
        Action onOpenSettings)
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

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClockDisplay(force: false);

        SourceInitialized += (_, _) => DesktopHostService.ConfigureFloatingDockWindow(this);
        Loaded += (_, _) =>
        {
            ApplySettings();
            RefreshDock();
            RepositionDock();
        };
        ContentRendered += (_, _) => RepositionDock();
        SizeChanged += (_, _) => RepositionDock();
        Closed += (_, _) => _clockTimer.Stop();
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

    public void ApplySettings()
    {
        PinnedPanel.EnableMagnification = _appConfig.Dock.EnableMagnification;
        PinnedPanel.MaxScale = _appConfig.Dock.MagnificationScale;
        PinnedPanel.EffectRadius = _appConfig.Dock.MagnificationRadius;

        RunningPanel.EnableMagnification = _appConfig.Dock.EnableMagnification;
        RunningPanel.MaxScale = _appConfig.Dock.MagnificationScale;
        RunningPanel.EffectRadius = _appConfig.Dock.MagnificationRadius;

        if (!_appConfig.Dock.EnableMagnification)
        {
            PinnedPanel.SmoothResetScales();
            RunningPanel.SmoothResetScales();
        }

        ApplyClockSettings();
        UpdateQuickToolStates();
        RepositionDock();
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
        if (_isDragging)
        {
            _pendingRefreshAfterDrag = true;
            return;
        }

        var running = _windowTracker.GetRunningWindows();
        var snapshot = _dockManager.BuildSnapshot(running);
        var iconSize = _appConfig.Dock.BaseIconSize;

        var pinnedChanged = SyncPanelSlots(PinnedPanel, snapshot.PinnedSlots, iconSize, forceIconReload);
        var runningChanged = SyncPanelSlots(RunningPanel, snapshot.RunningOnlySlots, iconSize, forceIconReload);

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
        double iconSize,
        bool forceIconReload)
    {
        var expectedWidth = iconSize + 12;
        var sameOrder = panel.Children.Count == slots.Count;
        if (sameOrder)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (panel.Children[i] is not Border tile ||
                    tile.Tag is not DockSlotItem prev ||
                    !string.Equals(prev.Id, slots[i].Id, StringComparison.Ordinal) ||
                    Math.Abs(tile.Width - expectedWidth) > 0.1)
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
                UpdateDockSlotElement(tile, prev, slots[i], iconSize, forceIconReload);
            }
            return false;
        }

        // Preserve surviving tiles so currently magnified icons can smoothly animate back to 1.0 instead of snapping
        var existingById = new Dictionary<string, Border>(StringComparer.Ordinal);
        foreach (var child in panel.Children.OfType<Border>())
        {
            if (child.Tag is DockSlotItem existingSlot && Math.Abs(child.Width - expectedWidth) <= 0.1)
                existingById.TryAdd(existingSlot.Id, child);
        }

        panel.Children.Clear();
        foreach (var slot in slots)
        {
            if (existingById.TryGetValue(slot.Id, out var reusedTile) && reusedTile.Tag is DockSlotItem prevSlot)
            {
                UpdateDockSlotElement(reusedTile, prevSlot, slot, iconSize, forceIconReload);
                panel.Children.Add(reusedTile);
            }
            else
            {
                panel.Children.Add(BuildDockSlotElement(slot, iconSize));
            }
        }

        panel.SmoothResetScales();
        return true;
    }

    public void RepositionDock()
    {
        if (!IsLoaded) return;

        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        Left = Math.Round((screenWidth - ActualWidth) / 2.0);
        // Bottom margin inside DockWindow is 14px, so anchoring to bottom leaves clean floating breathing room
        var bottomAnchor = _appConfig.Dock.AutoHideSystemTaskbar
            ? screenHeight
            : workArea.Bottom;
        Top = Math.Round(bottomAnchor - ActualHeight);
    }

    public void SetFullscreenHidden(bool hidden)
    {
        if (!_appConfig.ShowDock || !_appConfig.Dock.Enabled)
        {
            HideDock();
            return;
        }

        if (hidden && _appConfig.Dock.AutoHideOnFullscreen)
        {
            HideDock();
        }
        else if (!IsVisible)
        {
            Show();
            ApplyClockSettings();
            RepositionDock();
        }
    }

    public void HideDock()
    {
        _clockTimer.Stop();
        PowerMenuPopup.IsOpen = false;
        try { _activePopup?.Close(); } catch { }
        _activePopup = null;
        if (IsVisible)
            Hide();
    }

    private int GetHighDpiDockIconRequestPixels(double iconSize)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var maxScale = _appConfig.Dock.EnableMagnification ? Math.Max(1.0, _appConfig.Dock.MagnificationScale) : 1.0;
        return ShellIconService.ComputePhysicalIconPixels(iconSize * maxScale, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
    }

    private Border BuildDockSlotElement(DockSlotItem slot, double iconSize)
    {
        var tile = new Border
        {
            Width = iconSize + 12,
            Height = iconSize + 14,
            Margin = new Thickness(4, 0, 4, 0),
            Padding = new Thickness(4, 3, 4, 2),
            CornerRadius = new CornerRadius(12),
            Background = slot.IsActive ? (Brush)FindResource("DgHoverBrush") : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = slot,
            ToolTip = BuildSlotTooltip(slot),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7) });

        var iconHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            IsTabStop = false,
            Content = BuildSlotIconVisual(slot, iconSize)
        };
        Grid.SetRow(iconHost, 0);
        grid.Children.Add(iconHost);

        var indicator = new Border
        {
            Height = 3.5,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ApplyIndicatorState(indicator, slot);
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

    private void UpdateDockSlotElement(Border tile, DockSlotItem prev, DockSlotItem next, double iconSize, bool forceIconReload)
    {
        tile.Tag = next;
        tile.ToolTip = BuildSlotTooltip(next);
        tile.Background = next.IsActive || tile.IsMouseOver
            ? (Brush)FindResource("DgHoverBrush")
            : Brushes.Transparent;

        if (tile.Child is Grid grid && grid.Children.Count >= 2)
        {
            if (grid.Children[0] is ContentControl iconHost &&
                (forceIconReload ||
                 prev.PrimaryWindowHandle != next.PrimaryWindowHandle ||
                 prev.IsRunning != next.IsRunning ||
                 !string.Equals(prev.TargetPath, next.TargetPath, StringComparison.OrdinalIgnoreCase)))
            {
                iconHost.Content = BuildSlotIconVisual(next, iconSize);
            }

            if (grid.Children[1] is Border indicator)
                ApplyIndicatorState(indicator, next);
        }
    }

    private UIElement BuildSlotIconVisual(DockSlotItem slot, double iconSize)
    {
        var requestPx = GetHighDpiDockIconRequestPixels(iconSize);
        var bmp = slot.IsRunning
            ? _shellService.GetIconForWindow(slot.PrimaryWindowHandle, slot.TargetPath, requestPx)
            : _shellService.GetIconForPath(slot.TargetPath, requestPx);

        if (bmp is not null)
        {
            var img = new Image
            {
                Source = bmp,
                Width = iconSize - 4,
                Height = iconSize - 4,
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
            FontSize = iconSize * 0.56,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("DgAccentBrush")
        };
    }

    private void ApplyIndicatorState(Border indicator, DockSlotItem slot)
    {
        if (!slot.IsRunning)
        {
            indicator.Visibility = Visibility.Collapsed;
            return;
        }

        indicator.Visibility = Visibility.Visible;
        indicator.Width = slot.IsActive ? 16 : (slot.RunningCount > 1 ? 12 : 5);
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
        var screenPt = anchor.PointToScreen(new Point(anchor.ActualWidth / 2.0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        var dipX = screenPt.X / dpi.DpiScaleX;
        var dipY = screenPt.Y / dpi.DpiScaleY;

        popup.Loaded += (_, _) =>
        {
            popup.Left = Math.Max(8, Math.Min(SystemParameters.PrimaryScreenWidth - popup.ActualWidth - 8, dipX - popup.ActualWidth / 2.0));
            popup.Top = Math.Max(8, dipY - popup.ActualHeight - 10);
        };
        popup.Show();
    }

    private void OnDockBackgroundRightClick(object sender, MouseButtonEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        PinnedPanel.SmoothResetScales();
        RunningPanel.SmoothResetScales();

        var menu = ThemePalette.CreateThemedContextMenu();
        menu.Placement = PlacementMode.MousePoint;

        var addPinnedMi = new MenuItem { Header = "添加固定应用..." };
        addPinnedMi.Click += (_, _) => PromptAddPinnedApps();
        menu.Items.Add(addPinnedMi);

        menu.Items.Add(new Separator());

        var newBoxMi = new MenuItem { Header = "新建桌面盒子..." };
        newBoxMi.Click += (_, _) => _onCreateBox();
        menu.Items.Add(newBoxMi);

        var settingsMi = new MenuItem { Header = "偏好设置..." };
        settingsMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onOpenSettings();
        };
        menu.Items.Add(settingsMi);

        menu.PlacementTarget = DockPill;
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

        menu.PlacementTarget = target;
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
        try { _activePopup?.Close(); } catch { }
        _activePopup = null;
        _onOpenSettings();
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
        var indicatorHeight = Math.Max(28.0, Math.Min(PinnedPanel.ActualHeight - 8.0, _appConfig.Dock.BaseIconSize * 0.88));
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
