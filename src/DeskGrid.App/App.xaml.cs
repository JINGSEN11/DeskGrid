using System.Windows;
using DeskGrid.App.Helpers;
using DeskGrid.App.Services;
using DeskGrid.App.Views;
using DeskGrid.Core.Models;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Services;

namespace DeskGrid.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\DeskGrid_SingleInstance_Mutex_v1";

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
    private DockWindow? _dockWindow;
    private SettingsWindow? _settingsWindow;

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

        _dockManager.DockChanged += (_, _) => Dispatcher.Invoke(() => _dockWindow?.RefreshDock());

        _windowTracker.WindowsChanged += (_, _) => Dispatcher.BeginInvoke(() => _dockWindow?.RefreshDock());
        _windowTracker.Start();

        _fullscreenMonitor.FullscreenStateChanged += (_, isFs) => Dispatcher.BeginInvoke(() =>
        {
            if (!_config.ShowDock)
            {
                _dockWindow?.HideDock();
                return;
            }
            _dockWindow?.SetFullscreenHidden(isFs);
        });
        _fullscreenMonitor.Start();

        _desktopHost.EmptyDesktopDoubleClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_config.Behavior.DoubleClickDesktopToToggleBoxes)
                ToggleAllBoxesVisibility();
        });
        _desktopHost.StartEmptyDesktopDoubleClickMonitor(Dispatcher);

        // Spawn all boxes
        foreach (var box in _config.Boxes)
            SpawnBoxWindow(box);

        // Spawn Dock
        _dockWindow = new DockWindow(
            _config,
            _dockManager,
            _windowTracker,
            _nativeIcons,
            _shellService,
            ToggleAllBoxesVisibility,
            OpenCreateBoxDialog,
            OpenSettingsWindow);

        if (_config.ShowDock)
            _dockWindow.Show();

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
                _dockWindow?.UpdateQuickToolStates();
            },
            EmergencyRestoreSystemState,
            OpenSettingsWindow,
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
            GetSiblingBounds);

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

        _dockWindow?.UpdateQuickToolStates();
    }

    private void ToggleDockVisibility()
    {
        _config.ShowDock = !_config.ShowDock;
        _config.Dock.Enabled = _config.ShowDock;
        _configService.Save(_config);
        ApplyAllSettingsLive();
        _settingsWindow?.SyncFromConfig();
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

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _config,
            _configService,
            ApplyAllSettingsLive,
            ReplaceConfigAndReload,
            EmergencyRestoreSystemState);

        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ApplyAllSettingsLive()
    {
        ThemePalette.ApplyToApplication(this, _config.Appearance.Theme);

        foreach (var win in _boxWindows.Values)
            win.ApplyVisualSettings();

        if (_dockWindow is not null)
        {
            if (_config.ShowDock)
            {
                if (!_dockWindow.IsVisible && !(_fullscreenMonitor.IsFullscreenActive && _config.Dock.AutoHideOnFullscreen))
                    _dockWindow.Show();
                _dockWindow.ApplySettings();
                _dockWindow.RefreshDock();
            }
            else
            {
                _dockWindow.HideDock();
            }
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

        _taskbarService.SetTaskbarHidden(_config.ShowDock && _config.Dock.AutoHideSystemTaskbar);
        if (_config.ShowDock)
            _dockWindow?.RepositionDock();
    }

    private void ReplaceConfigAndReload(AppConfig newConfig)
    {
        foreach (var win in _boxWindows.Values.ToList())
            win.Close();
        _boxWindows.Clear();

        _dockWindow?.Close();
        _dockWindow = null;

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
        _dockManager.DockChanged += (_, _) => Dispatcher.Invoke(() => _dockWindow?.RefreshDock());

        foreach (var box in _config.Boxes)
            SpawnBoxWindow(box);

        _dockWindow = new DockWindow(
            _config,
            _dockManager,
            _windowTracker,
            _nativeIcons,
            _shellService,
            ToggleAllBoxesVisibility,
            OpenCreateBoxDialog,
            OpenSettingsWindow);

        if (_config.ShowDock)
            _dockWindow.Show();

        ApplySystemSettings();
    }

    private void EmergencyRestoreSystemState()
    {
        _taskbarService.RestoreIfHidden();
        _nativeIcons.SetIconsVisible(true);
        _dockWindow?.UpdateQuickToolStates();
        _dockWindow?.RepositionDock();
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
