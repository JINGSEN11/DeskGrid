using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskGrid.Core.Models;
using DeskGrid.Core.Services;
using Microsoft.Win32;

namespace DeskGrid.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly Action _onSettingsApplied;
    private readonly Action<double> _onGlobalBoxOpacityChanged;
    private readonly Action<AppConfig> _onConfigReplaced;
    private readonly Action _onEmergencyRestore;
    private readonly Action _onSafeExit;
    private readonly DispatcherTimer _opacitySaveDebounceTimer;
    private bool _hasPendingOpacitySave;
    private bool _loading = true;

    public bool IsClosed { get; private set; }

    public SettingsWindow(
        AppConfig config,
        ConfigService configService,
        Action onSettingsApplied,
        Action<double> onGlobalBoxOpacityChanged,
        Action<AppConfig> onConfigReplaced,
        Action onEmergencyRestore,
        Action onSafeExit)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _onSettingsApplied = onSettingsApplied ?? throw new ArgumentNullException(nameof(onSettingsApplied));
        _onGlobalBoxOpacityChanged = onGlobalBoxOpacityChanged ?? throw new ArgumentNullException(nameof(onGlobalBoxOpacityChanged));
        _onConfigReplaced = onConfigReplaced ?? throw new ArgumentNullException(nameof(onConfigReplaced));
        _onEmergencyRestore = onEmergencyRestore ?? throw new ArgumentNullException(nameof(onEmergencyRestore));
        _onSafeExit = onSafeExit ?? throw new ArgumentNullException(nameof(onSafeExit));

        EnsureConfigSections();

        _opacitySaveDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _opacitySaveDebounceTimer.Tick += (_, _) => FlushPendingOpacitySave();

        InitializeComponent();

        LoadControlsFromConfig();
        _loading = false;
    }

    public void SyncFromConfig()
    {
        if (IsClosed || !IsInitialized)
            return;

        _loading = true;
        try
        {
            LoadControlsFromConfig();
        }
        finally
        {
            _loading = false;
        }
    }

    private void EnsureConfigSections()
    {
        _config.Appearance ??= new AppearanceSettings();
        _config.Behavior ??= new BehaviorSettings();
        _config.Dock ??= new DockConfig();
        _config.Dock.PinnedItems ??= [];
        _config.Boxes ??= [];
    }

    private void LoadControlsFromConfig()
    {
        EnsureConfigSections();

        ThemeCombo.SelectedIndex = _config.Appearance.Theme switch
        {
            ThemePreference.Light => 1,
            ThemePreference.Dark => 2,
            _ => 0
        };
        BackdropCombo.SelectedIndex = _config.Appearance.Backdrop == BackdropPreference.Mica ? 1 : 0;

        var defaultOpacity = double.IsFinite(_config.Appearance.DefaultBoxOpacity)
            ? Math.Clamp(_config.Appearance.DefaultBoxOpacity, OpacitySlider.Minimum, OpacitySlider.Maximum)
            : 0.35;
        OpacitySlider.Value = defaultOpacity;
        OpacityValueText.Text = $"{Math.Round(defaultOpacity * 100)}%";

        StartupCheck.IsChecked = _config.Behavior.LaunchAtStartup;
        DoubleClickDesktopCheck.IsChecked = _config.Behavior.DoubleClickDesktopToToggleBoxes;
        GridSnapCheck.IsChecked = _config.Behavior.EnableGridSnap;

        ShowDockCheck.IsChecked = _config.ShowDock;
        DockOptionsPanel.IsEnabled = _config.ShowDock;
        MagnificationCheck.IsChecked = _config.Dock.EnableMagnification;

        var magScale = double.IsFinite(_config.Dock.MagnificationScale)
            ? Math.Clamp(_config.Dock.MagnificationScale, MagScaleSlider.Minimum, MagScaleSlider.Maximum)
            : 1.42;
        MagScaleSlider.Value = magScale;
        MagScaleValueText.Text = $"{magScale:0.00}x";

        FullscreenHideCheck.IsChecked = _config.Dock.AutoHideOnFullscreen;
        HideTaskbarCheck.IsChecked = _config.Dock.AutoHideSystemTaskbar;
        UpdateTaskbarHintVisibility(_config.Dock.AutoHideSystemTaskbar);

        ShowDockClockCheck.IsChecked = _config.Dock.ShowDockClock;
        ClockOptionsPanel.IsEnabled = _config.Dock.ShowDockClock;
        Clock24HourCheck.IsChecked = _config.Dock.ClockUse24Hour;
        ClockShowSecondsCheck.IsChecked = _config.Dock.ClockShowSeconds;
        ClockShowDateCheck.IsChecked = _config.Dock.ClockShowDate;
    }

    private void UpdateTaskbarHintVisibility(bool isHideTaskbarEnabled)
    {
        if (TaskbarUncheckedHintText is not null)
            TaskbarUncheckedHintText.Visibility = isHideTaskbarEnabled ? Visibility.Collapsed : Visibility.Visible;
        if (TaskbarCheckedHintText is not null)
            TaskbarCheckedHintText.Visibility = isHideTaskbarEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpacitySliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsInitialized) return;
        EnsureConfigSections();

        var opacity = Math.Round(OpacitySlider.Value, 2);
        _config.Appearance.DefaultBoxOpacity = opacity;
        OpacityValueText.Text = $"{Math.Round(opacity * 100)}%";

        // Immediately sync background brush opacity across all currently open DesktopBoxWindow instances
        _onGlobalBoxOpacityChanged(opacity);

        // Debounce disk persistence while continuously dragging the slider
        _hasPendingOpacitySave = true;
        _opacitySaveDebounceTimer.Stop();
        _opacitySaveDebounceTimer.Start();
    }

    private void FlushPendingOpacitySave()
    {
        _opacitySaveDebounceTimer?.Stop();
        if (!_hasPendingOpacitySave)
            return;

        _hasPendingOpacitySave = false;
        _configService.Save(_config);
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        EnsureConfigSections();

        if (ThemeCombo.SelectedItem is ComboBoxItem themeItem && themeItem.Tag is string themeTag &&
            Enum.TryParse<ThemePreference>(themeTag, out var themePref))
        {
            _config.Appearance.Theme = themePref;
        }

        if (BackdropCombo.SelectedItem is ComboBoxItem bdItem && bdItem.Tag is string bdTag &&
            Enum.TryParse<BackdropPreference>(bdTag, out var bdPref))
        {
            _config.Appearance.Backdrop = bdPref;
        }

        _config.Behavior.LaunchAtStartup = StartupCheck.IsChecked == true;
        _config.Behavior.DoubleClickDesktopToToggleBoxes = DoubleClickDesktopCheck.IsChecked == true;
        _config.Behavior.EnableGridSnap = GridSnapCheck.IsChecked == true;

        _config.ShowDock = ShowDockCheck.IsChecked == true;
        _config.Dock.Enabled = _config.ShowDock;
        DockOptionsPanel.IsEnabled = _config.ShowDock;
        _config.Dock.EnableMagnification = MagnificationCheck.IsChecked == true;
        _config.Dock.MagnificationScale = Math.Round(MagScaleSlider.Value, 2);
        MagScaleValueText.Text = $"{_config.Dock.MagnificationScale:0.00}x";
        _config.Dock.AutoHideOnFullscreen = FullscreenHideCheck.IsChecked == true;
        _config.Dock.AutoHideSystemTaskbar = HideTaskbarCheck.IsChecked == true;
        UpdateTaskbarHintVisibility(_config.Dock.AutoHideSystemTaskbar);

        _config.Dock.ShowDockClock = ShowDockClockCheck.IsChecked == true;
        ClockOptionsPanel.IsEnabled = _config.Dock.ShowDockClock;
        _config.Dock.ClockUse24Hour = Clock24HourCheck.IsChecked == true;
        _config.Dock.ClockShowSeconds = ClockShowSecondsCheck.IsChecked == true;
        _config.Dock.ClockShowDate = ClockShowDateCheck.IsChecked == true;

        _opacitySaveDebounceTimer.Stop();
        _hasPendingOpacitySave = false;
        _configService.Save(_config);
        _onSettingsApplied();
    }

    private void OnExportConfig(object sender, RoutedEventArgs e)
    {
        FlushPendingOpacitySave();
        var dlg = new SaveFileDialog
        {
            Title = "导出 DeskGrid 配置",
            Filter = "DeskGrid 配置文件 (*.deskgrid)|*.deskgrid|JSON 文件 (*.json)|*.json",
            FileName = $"DeskGrid-Backup-{DateTime.Now:yyyyMMdd}.deskgrid"
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _configService.ExportTo(dlg.FileName, _config);
                MessageBox.Show(this, "配置导出成功。", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"导出失败：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnImportConfig(object sender, RoutedEventArgs e)
    {
        _opacitySaveDebounceTimer.Stop();
        _hasPendingOpacitySave = false;
        var dlg = new OpenFileDialog
        {
            Title = "导入 DeskGrid 配置",
            Filter = "DeskGrid 配置文件 (*.deskgrid;*.json)|*.deskgrid;*.json|所有文件 (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var imported = _configService.ImportFrom(dlg.FileName);
                _onConfigReplaced(imported);
                MessageBox.Show(this, "配置导入成功，桌面与 Dock 已刷新。", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"导入失败：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _configService.ConfigDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DeskGrid] Failed to open config directory: {ex}");
            MessageBox.Show(this, $"无法打开配置目录：{ex.Message}", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnEmergencyRestore(object sender, RoutedEventArgs e)
    {
        _loading = true;
        HideTaskbarCheck.IsChecked = false;
        _config.Dock.AutoHideSystemTaskbar = false;
        UpdateTaskbarHintVisibility(false);
        _loading = false;

        _opacitySaveDebounceTimer.Stop();
        _hasPendingOpacitySave = false;
        _configService.Save(_config);
        _onEmergencyRestore();
        MessageBox.Show(this, "已恢复系统任务栏与原生桌面图标显示。", "DeskGrid", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "确定重置为初始默认布局吗？（不会删除磁盘上的任何真实文件）",
            "DeskGrid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            _opacitySaveDebounceTimer.Stop();
            _hasPendingOpacitySave = false;
            var fresh = FirstRunInitializer.CreateDefaultConfig(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
            _configService.Save(fresh);
            _onConfigReplaced(fresh);
            Close();
        }
    }

    private void OnNavSectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || AppearanceSection is null || DockSection is null || BackupSection is null || AboutSection is null)
            return;

        AppearanceSection.Visibility = NavAppearanceRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DockSection.Visibility = NavDockRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BackupSection.Visibility = NavBackupRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = NavAboutRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSafeExit(object sender, RoutedEventArgs e)
    {
        FlushPendingOpacitySave();
        _onSafeExit();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e) => FlushPendingOpacitySave();

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        _opacitySaveDebounceTimer?.Stop();
        base.OnClosed(e);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
