using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskGrid.App.Helpers;
using DeskGrid.App.Services;
using DeskGrid.App.Views;
using DeskGrid.Core.Models;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Services;
using Microsoft.Win32;

namespace DeskGrid.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\DeskGrid_SingleInstance_Mutex_v1";
    private const int FollowMouseSwitchDwellMs = 150;

    private Mutex? _singleInstanceMutex;
    private ConfigService _configService = null!;
    private AppConfig _config = null!;
    private BoxManager _boxManager = null!;
    private DockManager _dockManager = null!;

    private readonly ShellIconService _shellService = new();
    private readonly NativeDesktopIconService _nativeIcons = new();
    private readonly DesktopHostService _desktopHost = new();
    private readonly WindowTrackerService _windowTracker = new();
    private readonly FullscreenMonitorService _fullscreenMonitor = new();
    private readonly StartupRegistryService _startupService = new();
    private TaskbarService _taskbarService = null!;
    private TrayIconController? _trayController;

    private readonly Dictionary<string, DesktopBoxWindow> _boxWindows = new(StringComparer.Ordinal);
    private readonly List<DockWindow> _dockWindows = [];
    private SettingsWindow? _settingsWindow;

    private DispatcherTimer? _followMouseTimer;
    private DispatcherTimer? _edgeRevealTimer;
    private string? _pendingFollowMonitorDevice;
    private long _pendingFollowSinceMs;
    private int _lastFollowCursorX = int.MinValue;
    private int _lastFollowCursorY = int.MinValue;
    private int _lastEdgeCursorX = int.MinValue;
    private int _lastEdgeCursorY = int.MinValue;
    private int _dockRebuildGeneration;
    private string _activeMultiMonitorMode = string.Empty;
    private bool _activeDockEnabled;
    private bool _displaySettingsSubscribed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DeskGrid 已在后台运行，请从系统托盘打开。", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _configService = new ConfigService();
        _taskbarService = new TaskbarService(_configService.ConfigDirectory);

        // Safety: recover taskbar if a previous run terminated abnormally
        _taskbarService.RecoverFromCrashIfNeeded();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeRestoreSystemState();
        DispatcherUnhandledException += (_, args) =>
        {
            SafeRestoreSystemState();
            LogError("DispatcherUnhandledException", args.Exception);
            MessageBox.Show($"DeskGrid 遇到意外错误并已保护系统状态：{args.Exception.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _config = _configService.LoadOrCreate(() =>
            FirstRunInitializer.CreateDefaultConfig(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height));
        ThemePalette.ApplyToApplication(this, _config.Appearance.Theme);

        InitializeDomainAndUI();
    }

    private void InitializeDomainAndUI()
    {
        _boxManager = new BoxManager(_config, _configService);
        _dockManager = new DockManager(_config, _configService);

        _boxManager.BoxAdded += (_, box) => Dispatcher.Invoke(() => SpawnBoxWindow(box));
        _boxManager.BoxRemoved += (_, boxId) => Dispatcher.Invoke(() => CloseBoxWindow(boxId));
        _boxManager.BoxItemsChanged += (_, boxId) => Dispatcher.Invoke(() =>
        {
            if (_boxWindows.TryGetValue(boxId, out var win))
                win.RefreshItems();
        });

        _dockManager.DockChanged += (_, _) => Dispatcher.Invoke(RefreshAllDocks);

        _windowTracker.WindowsChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshAllDocks);
        _windowTracker.Start();

        _fullscreenMonitor.FullscreenMonitorsChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, SyncFullscreenStateToDocks);
        _fullscreenMonitor.Start();

        _desktopHost.EmptyDesktopDoubleClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_config.Behavior.DoubleClickDesktopToToggleBoxes)
                ToggleAllBoxesVisibility();
        });
        _desktopHost.StartEmptyDesktopDoubleClickMonitor(Dispatcher);

        // Prime cached monitor geometry once at startup (zero GDI enumeration during timer ticks)
        DisplayMonitorService.RefreshMonitors();

        if (!_displaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _displaySettingsSubscribed = true;
        }

        _followMouseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(95)
        };
        _followMouseTimer.Tick += (_, _) => OnFollowMouseTick();

        _edgeRevealTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(85)
        };
        _edgeRevealTimer.Tick += (_, _) => OnEdgeRevealTick();

        // Spawn all boxes
        foreach (var box in _config.Boxes)
            SpawnBoxWindow(box);

        // Spawn Dock instance(s) according to MultiMonitorMode
        ScheduleRebuildDockWindows(refreshDisplayCache: false);

        // Spawn System Tray
        _trayController ??= new TrayIconController(
            _nativeIcons,
            () => _config.Behavior.BoxesVisible,
            () => _config.ShowDock,
            OpenCreateBoxDialog,
            ToggleAllBoxesVisibility,
            ToggleDockVisibility,
            () =>
            {
                _nativeIcons.ToggleIcons();
                UpdateAllDockQuickToolStates();
            },
            EmergencyRestoreSystemState,
            ShowSettingsWindow,
            ShutdownSafely);

        ApplySystemSettings();
    }

    private void SpawnBoxWindow(BoxConfig box)
    {
        if (_boxWindows.ContainsKey(box.Id))
            return;

        var win = new DesktopBoxWindow(
            box,
            _config,
            _boxManager,
            _dockManager,
            _shellService,
            GetSiblingBounds,
            ShowDockBar,
            ShutdownSafely);

        _boxWindows[box.Id] = win;
        if (_config.Behavior.BoxesVisible)
            win.Show();
    }

    private void CloseBoxWindow(string boxId)
    {
        if (_boxWindows.Remove(boxId, out var win))
            win.Close();
    }

    private IEnumerable<RectBounds> GetSiblingBounds(string excludeBoxId)
    {
        foreach (var (id, win) in _boxWindows)
        {
            if (id == excludeBoxId) continue;
            yield return win.GetLogicalBounds();
        }
    }

    private void ToggleAllBoxesVisibility()
    {
        _config.Behavior.BoxesVisible = !_config.Behavior.BoxesVisible;
        _configService.Save(_config);

        foreach (var win in _boxWindows.Values)
        {
            if (_config.Behavior.BoxesVisible)
                win.Show();
            else
                win.Hide();
        }

        UpdateAllDockQuickToolStates();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_config is null)
                return;

            ScheduleRebuildDockWindows(refreshDisplayCache: true);
        });
    }

    private DockWindow CreateDockWindow(DisplayMonitorInfo monitor)
    {
        return new DockWindow(
            _config,
            _dockManager,
            _windowTracker,
            _nativeIcons,
            _shellService,
            ToggleAllBoxesVisibility,
            OpenCreateBoxDialog,
            ShowSettingsWindow,
            ShowSystemTaskbarAndDisableAutoHide,
            ShutdownSafely,
            monitor);
    }

    private void CloseAndClearAllDockWindows()
    {
        _followMouseTimer?.Stop();
        _edgeRevealTimer?.Stop();
        _pendingFollowMonitorDevice = null;
        _lastFollowCursorX = int.MinValue;
        _lastFollowCursorY = int.MinValue;
        _lastEdgeCursorX = int.MinValue;
        _lastEdgeCursorY = int.MinValue;

        foreach (var dock in _dockWindows.ToList())
        {
            try
            {
                var wasClosed = dock.IsClosed;
                dock.CleanupBeforeClose();
                if (!wasClosed)
                    dock.Close();
            }
            catch
            {
                // Ignore window teardown exceptions
            }
        }

        _dockWindows.Clear();
    }

    /// <summary>
    /// Safely tears down existing Dock instances (detaching all timers, rendering listeners,
    /// and window tracker handles) first, then schedules creation of fresh DockWindow instances
    /// asynchronously via Dispatcher.BeginInvoke so ComboBox.SelectionChanged or DisplaySettingsChanged
    /// never deadlocks inside a synchronous nested layout/redraw stack.
    /// </summary>
    private void ScheduleRebuildDockWindows(bool refreshDisplayCache)
    {
        var generation = ++_dockRebuildGeneration;
        var dockEnabled = _config.ShowDock && _config.Dock.Enabled;
        var mode = DockMultiMonitorModes.Normalize(_config.Dock.MultiMonitorMode);
        _activeDockEnabled = dockEnabled;
        _activeMultiMonitorMode = mode;

        // Always cleanly tear down and unsubscribe all existing Dock windows before creating replacements
        CloseAndClearAllDockWindows();

        if (!dockEnabled)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != _dockRebuildGeneration)
                return;

            if (!_config.ShowDock || !_config.Dock.Enabled)
                return;

            if (refreshDisplayCache)
                DisplayMonitorService.RefreshMonitors();

            var allMonitors = DisplayMonitorService.GetCachedMonitors();
            int? cursorX = null;
            int? cursorY = null;
            if (DesktopHostService.TryGetCursorScreenPosition(out var cx, out var cy))
            {
                cursorX = cx;
                cursorY = cy;
            }

            var currentMode = DockMultiMonitorModes.Normalize(_config.Dock.MultiMonitorMode);
            var targetMonitors = DockPositionCalculator.SelectTargetMonitors(allMonitors, currentMode, cursorX, cursorY);

            foreach (var monitor in targetMonitors)
            {
                var dock = CreateDockWindow(monitor);
                _dockWindows.Add(dock);

                var isFs = _fullscreenMonitor.IsMonitorFullscreen(monitor.Handle);
                if (isFs && _config.Dock.AutoHideOnFullscreen)
                {
                    dock.SetFullscreenHidden(true);
                }
                else
                {
                    dock.Show();
                }
            }

            UpdateDockMonitorTimers();
        });
    }

    private void UpdateDockMonitorTimers()
    {
        var mode = DockMultiMonitorModes.Normalize(_config.Dock.MultiMonitorMode);
        var shouldFollowMouse = _config.ShowDock &&
                                _config.Dock.Enabled &&
                                mode == DockMultiMonitorModes.FollowMouse &&
                                _dockWindows.Count > 0;
        if (shouldFollowMouse)
        {
            if (_followMouseTimer is not null && !_followMouseTimer.IsEnabled)
                _followMouseTimer.Start();
        }
        else
        {
            _followMouseTimer?.Stop();
            _pendingFollowMonitorDevice = null;
        }

        var shouldPollEdgeReveal = _config.ShowDock &&
                                   _config.Dock.Enabled &&
                                   _config.Dock.AutoHideOnFullscreen &&
                                   _config.Dock.RevealDockOnBottomEdge &&
                                   _dockWindows.Any(d => !d.IsClosed && (d.IsFullscreenHidden || d.IsTemporarilyRevealed));
        if (shouldPollEdgeReveal)
        {
            if (_edgeRevealTimer is not null && !_edgeRevealTimer.IsEnabled)
                _edgeRevealTimer.Start();
        }
        else
        {
            _edgeRevealTimer?.Stop();
        }
    }

    private void SyncFullscreenStateToDocks()
    {
        if (!_config.ShowDock || !_config.Dock.Enabled)
        {
            foreach (var dock in _dockWindows)
                dock.HideDock();
            UpdateDockMonitorTimers();
            return;
        }

        foreach (var dock in _dockWindows)
        {
            if (dock.IsClosed)
                continue;

            var isMonitorFullscreen = _fullscreenMonitor.IsMonitorFullscreen(dock.TargetMonitor.Handle);
            dock.SetFullscreenHidden(isMonitorFullscreen);
        }

        UpdateDockMonitorTimers();
    }

    private void OnFollowMouseTick()
    {
        if (!_config.ShowDock || !_config.Dock.Enabled || _dockWindows.Count == 0)
            return;

        if (DockMultiMonitorModes.Normalize(_config.Dock.MultiMonitorMode) != DockMultiMonitorModes.FollowMouse)
            return;

        var dock = _dockWindows[0];
        if (dock.IsClosed || dock.HasActiveOverlay)
        {
            _pendingFollowMonitorDevice = null;
            return;
        }

        if (!DesktopHostService.TryGetCursorScreenPosition(out var cursorX, out var cursorY))
            return;

        // Skip all coordinate checks when cursor has not moved and no switch dwell is pending
        if (cursorX == _lastFollowCursorX && cursorY == _lastFollowCursorY && _pendingFollowMonitorDevice is null)
            return;

        _lastFollowCursorX = cursorX;
        _lastFollowCursorY = cursorY;

        // Pure in-memory Rect coordinate lookup (ZERO GDI / Screen.AllScreens enumeration)
        var monitors = DisplayMonitorService.GetCachedMonitors();
        var cursorMonitor = DockPositionCalculator.FindMonitorForPoint(monitors, cursorX, cursorY);
        if (cursorMonitor is null)
            return;

        var sameAsCurrent =
            string.Equals(cursorMonitor.DeviceName, dock.TargetMonitor.DeviceName, StringComparison.OrdinalIgnoreCase) &&
            (cursorMonitor.Handle == 0 || cursorMonitor.Handle == dock.TargetMonitor.Handle);

        if (sameAsCurrent)
        {
            _pendingFollowMonitorDevice = null;
            return;
        }

        var nowMs = Environment.TickCount64;
        if (!string.Equals(_pendingFollowMonitorDevice, cursorMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            _pendingFollowMonitorDevice = cursorMonitor.DeviceName;
            _pendingFollowSinceMs = nowMs;
            return;
        }

        if (nowMs - _pendingFollowSinceMs < FollowMouseSwitchDwellMs)
            return;

        _pendingFollowMonitorDevice = null;
        dock.SetTargetMonitor(cursorMonitor, smoothTransition: true);
        var isFs = _fullscreenMonitor.IsMonitorFullscreen(cursorMonitor.Handle);
        dock.SetFullscreenHidden(isFs);
        UpdateDockMonitorTimers();
    }

    private void OnEdgeRevealTick()
    {
        if (!_config.ShowDock || !_config.Dock.Enabled ||
            !_config.Dock.AutoHideOnFullscreen || !_config.Dock.RevealDockOnBottomEdge)
        {
            _edgeRevealTimer?.Stop();
            return;
        }

        if (!DesktopHostService.TryGetCursorScreenPosition(out var cursorX, out var cursorY))
            return;

        var cursorMoved = cursorX != _lastEdgeCursorX || cursorY != _lastEdgeCursorY;
        _lastEdgeCursorX = cursorX;
        _lastEdgeCursorY = cursorY;

        var nowMs = Environment.TickCount64;
        var anyNeedsPolling = false;

        foreach (var dock in _dockWindows)
        {
            if (dock.IsClosed)
                continue;

            if (dock.IsFullscreenHidden || dock.IsTemporarilyRevealed)
            {
                anyNeedsPolling = true;
                if (!cursorMoved && !dock.HasActiveEdgeRevealCountdown)
                    continue;

                dock.UpdateEdgeRevealTick(cursorX, cursorY, nowMs);
            }
        }

        if (!anyNeedsPolling)
            _edgeRevealTimer?.Stop();
    }

    private void RefreshAllDocks()
    {
        foreach (var dock in _dockWindows)
        {
            if (!dock.IsClosed)
                dock.RefreshDock();
        }
    }

    private void UpdateAllDockQuickToolStates()
    {
        foreach (var dock in _dockWindows)
        {
            if (!dock.IsClosed)
                dock.UpdateQuickToolStates();
        }
    }

    private void RepositionAllDocks()
    {
        foreach (var dock in _dockWindows)
        {
            if (!dock.IsClosed)
                dock.RepositionDock();
        }
    }

    private void ToggleDockVisibility()
    {
        _config.ShowDock = !_config.ShowDock;
        _config.Dock.Enabled = _config.ShowDock;
        _configService.Save(_config);
        ApplyAllSettingsLive();
        if (_settingsWindow is { IsClosed: false, IsLoaded: true })
            _settingsWindow.SyncFromConfig();
    }

    private void ShowDockBar()
    {
        if (_config.ShowDock)
            return;

        _config.ShowDock = true;
        _config.Dock.Enabled = true;
        _configService.Save(_config);
        ApplyAllSettingsLive();
        if (_settingsWindow is { IsClosed: false, IsLoaded: true })
            _settingsWindow.SyncFromConfig();
    }

    private void ShowSystemTaskbarAndDisableAutoHide()
    {
        _config.Dock.AutoHideSystemTaskbar = false;
        _configService.Save(_config);
        _taskbarService.RestoreTaskbar();

        if (_config.ShowDock)
        {
            DisplayMonitorService.RefreshMonitors();
            RepositionAllDocks();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (_config.ShowDock)
                {
                    DisplayMonitorService.RefreshMonitors();
                    RepositionAllDocks();
                }
            });
        }

        if (_settingsWindow is { IsClosed: false, IsLoaded: true })
            _settingsWindow.SyncFromConfig();
    }

    private void OpenCreateBoxDialog()
    {
        var dlg = new CreateBoxDialog();
        if (dlg.ShowDialog() == true)
        {
            if (!_config.Behavior.BoxesVisible)
                ToggleAllBoxesVisibility();

            _boxManager.CreateBox(dlg.BoxTitle, dlg.SelectedMode, dlg.SelectedFolderPath);
        }
    }

    public void ShowSettingsWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowSettingsWindow);
            return;
        }

        try
        {
            if (_settingsWindow is { IsClosed: false } existing && existing.IsLoaded)
            {
                existing.SyncFromConfig();
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                if (!existing.IsVisible)
                    existing.Show();

                EnsureWindowInWorkArea(existing);
                ScheduleSettingsWindowActivation(existing);
                return;
            }

            // Discard any stale/closed instance before creating a fresh window
            _settingsWindow = null;

            var window = new SettingsWindow(
                _config,
                _configService,
                ApplyAllSettingsLive,
                ApplyGlobalBoxOpacityLive,
                ApplyDockScaleLive,
                ApplyDockMagnificationScaleLive,
                ReplaceConfigAndReload,
                EmergencyRestoreSystemState,
                ShutdownSafely);

            // Never bind Owner to HWND_BOTTOM DesktopBoxWindow or WS_EX_NOACTIVATE DockWindow
            window.Owner = null;
            window.ShowInTaskbar = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.WindowState = WindowState.Normal;
            EnsureWindowInWorkArea(window);

            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settingsWindow, window))
                    _settingsWindow = null;
            };

            _settingsWindow = window;
            window.Show();
            EnsureWindowInWorkArea(window);
            ScheduleSettingsWindowActivation(window);
        }
        catch (Exception ex)
        {
            _settingsWindow = null;
            var logPath = LogError("ShowSettingsWindow", ex);
            var details = string.IsNullOrEmpty(logPath)
                ? ex.Message
                : $"{ex.Message}\n\n错误日志：{logPath}";
            MessageBox.Show(
                $"无法打开偏好设置窗口：{details}",
                "DeskGrid",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ScheduleSettingsWindowActivation(SettingsWindow window)
    {
        // Delay foreground activation to ApplicationIdle so closing ContextMenus or
        // WS_EX_NOACTIVATE Dock clicks cannot steal focus back from SettingsWindow.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (window.IsClosed || !window.IsLoaded)
                return;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            EnsureWindowInWorkArea(window);

            window.Topmost = true;
            window.Topmost = false;
            DesktopHostService.BringWindowToForeground(window);
            window.Activate();
            window.Focus();
        });
    }

    private static void EnsureWindowInWorkArea(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        if (!double.IsFinite(workArea.Width) || workArea.Width <= 0 ||
            !double.IsFinite(workArea.Height) || workArea.Height <= 0)
        {
            return;
        }

        var width = window.ActualWidth > 0
            ? window.ActualWidth
            : (double.IsFinite(window.Width) && window.Width > 0 ? window.Width : 660.0);
        var height = window.ActualHeight > 0
            ? window.ActualHeight
            : (double.IsFinite(window.Height) && window.Height > 0 ? window.Height : 580.0);

        var maxLeft = Math.Max(workArea.Left, workArea.Right - width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - height);

        var isLeftOutOfRange = !double.IsFinite(window.Left) ||
                               window.Left < workArea.Left ||
                               window.Left > maxLeft;
        var isTopOutOfRange = !double.IsFinite(window.Top) ||
                              window.Top < workArea.Top ||
                              window.Top > maxTop;

        if (isLeftOutOfRange)
        {
            var centeredLeft = workArea.Left + (workArea.Width - width) / 2.0;
            window.Left = Math.Round(Math.Clamp(centeredLeft, workArea.Left, maxLeft));
        }

        if (isTopOutOfRange)
        {
            var centeredTop = workArea.Top + (workArea.Height - height) / 2.0;
            window.Top = Math.Round(Math.Clamp(centeredTop, workArea.Top, maxTop));
        }
    }

    private string? LogError(string context, Exception ex)
    {
        var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{context}] {ex}{Environment.NewLine}";
        Trace.WriteLine(message);
        try
        {
            var dir = _configService?.ConfigDirectory;
            if (string.IsNullOrWhiteSpace(dir))
                return null;

            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "deskgrid-error.log");
            File.AppendAllText(logPath, message);
            return logPath;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyGlobalBoxOpacityLive(double opacity)
    {
        var clamped = Math.Clamp(opacity, 0.15, 0.90);
        foreach (var box in _config.Boxes)
            box.Opacity = clamped;

        foreach (var win in _boxWindows.Values)
            win.ApplyOpacity(clamped);
    }

    private void ApplyDockScaleLive(double scale)
    {
        var clamped = DockLayoutMetrics.ClampScale(scale);
        _config.Dock.DockScale = clamped;
        foreach (var dock in _dockWindows)
        {
            if (!dock.IsClosed)
                dock.ApplyDockScale(clamped);
        }
    }

    private void ApplyDockMagnificationScaleLive(double scale)
    {
        var clamped = Math.Clamp(scale, 1.05, 2.0);
        _config.Dock.MagnificationScale = clamped;
        foreach (var dock in _dockWindows)
        {
            if (!dock.IsClosed)
                dock.ApplyMagnificationScale(clamped);
        }
    }

    private void ApplyAllSettingsLive()
    {
        ThemePalette.ApplyToApplication(this, _config.Appearance.Theme);
        if (_settingsWindow is { IsClosed: false })
            _settingsWindow.ApplyThemeVisuals();

        foreach (var win in _boxWindows.Values)
            win.ApplyVisualSettings();

        var desiredEnabled = _config.ShowDock && _config.Dock.Enabled;
        var desiredMode = DockMultiMonitorModes.Normalize(_config.Dock.MultiMonitorMode);
        var needsDockRebuild =
            desiredEnabled != _activeDockEnabled ||
            !string.Equals(desiredMode, _activeMultiMonitorMode, StringComparison.OrdinalIgnoreCase) ||
            (desiredEnabled && _dockWindows.Count == 0);

        if (needsDockRebuild)
        {
            ScheduleRebuildDockWindows(refreshDisplayCache: true);
        }
        else if (desiredEnabled)
        {
            foreach (var dock in _dockWindows)
            {
                if (!dock.IsClosed)
                {
                    var isFs = _fullscreenMonitor.IsMonitorFullscreen(dock.TargetMonitor.Handle);
                    dock.SetFullscreenHidden(isFs);
                    dock.ApplySettings();
                    dock.ScheduleReposition();
                }
            }
            UpdateDockMonitorTimers();
        }

        ApplySystemSettings();
    }

    private void ApplySystemSettings()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
                _startupService.SetEnabled(_config.Behavior.LaunchAtStartup, exePath);
        }
        catch
        {
            // Ignore registry failure
        }

        var wantTaskbarHidden = _config.ShowDock && _config.Dock.AutoHideSystemTaskbar;
        var taskbarChanged = wantTaskbarHidden != _taskbarService.IsHiddenByApp;
        _taskbarService.SetTaskbarHidden(wantTaskbarHidden);

        if (taskbarChanged && _config.ShowDock)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (_config.ShowDock)
                {
                    DisplayMonitorService.RefreshMonitors();
                    RepositionAllDocks();
                }
            });
        }
    }

    private void ReplaceConfigAndReload(AppConfig newConfig)
    {
        foreach (var win in _boxWindows.Values.ToList())
            win.Close();
        _boxWindows.Clear();

        CloseAndClearAllDockWindows();

        _boxManager.Dispose();

        _config = newConfig;
        ThemePalette.ApplyToApplication(this, _config.Appearance.Theme);

        _boxManager = new BoxManager(_config, _configService);
        _dockManager = new DockManager(_config, _configService);

        _boxManager.BoxAdded += (_, box) => Dispatcher.Invoke(() => SpawnBoxWindow(box));
        _boxManager.BoxRemoved += (_, boxId) => Dispatcher.Invoke(() => CloseBoxWindow(boxId));
        _boxManager.BoxItemsChanged += (_, boxId) => Dispatcher.Invoke(() =>
        {
            if (_boxWindows.TryGetValue(boxId, out var win))
                win.RefreshItems();
        });
        _dockManager.DockChanged += (_, _) => Dispatcher.Invoke(RefreshAllDocks);

        foreach (var box in _config.Boxes)
            SpawnBoxWindow(box);

        ScheduleRebuildDockWindows(refreshDisplayCache: true);
        ApplySystemSettings();
    }

    private void EmergencyRestoreSystemState()
    {
        _taskbarService.RestoreTaskbar();
        _nativeIcons.SetIconsVisible(true);
        UpdateAllDockQuickToolStates();
        RepositionAllDocks();
    }

    private void SafeRestoreSystemState()
    {
        try { _taskbarService?.RestoreIfHidden(); } catch { }
        try { _nativeIcons?.SetIconsVisible(true); } catch { }
    }

    private void ShutdownSafely()
    {
        SafeRestoreSystemState();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SafeRestoreSystemState();
        if (_displaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _displaySettingsSubscribed = false;
        }
        CloseAndClearAllDockWindows();
        _trayController?.Dispose();
        _desktopHost.Dispose();
        _fullscreenMonitor.Dispose();
        _windowTracker.Dispose();
        _boxManager?.Dispose();
        _taskbarService?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
