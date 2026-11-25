using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DeskGrid.Core.Models;
using DeskGrid.Core.Services;
using Microsoft.Win32;

namespace DeskGrid.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly Action _onSettingsApplied;
    private readonly Action<AppConfig> _onConfigReplaced;
    private readonly Action _onEmergencyRestore;
    private bool _loading = true;

    public SettingsWindow(
        AppConfig config,
        ConfigService configService,
        Action onSettingsApplied,
        Action<AppConfig> onConfigReplaced,
        Action onEmergencyRestore)
    {
        InitializeComponent();

        _config = config;
        _configService = configService;
        _onSettingsApplied = onSettingsApplied;
        _onConfigReplaced = onConfigReplaced;
        _onEmergencyRestore = onEmergencyRestore;

        LoadControlsFromConfig();
        _loading = false;
    }

    public void SyncFromConfig()
    {
        _loading = true;
        LoadControlsFromConfig();
        _loading = false;
    }

    private void LoadControlsFromConfig()
    {
        ThemeCombo.SelectedIndex = _config.Appearance.Theme switch
        {
            ThemePreference.Light => 1,
            ThemePreference.Dark => 2,
            _ => 0
        };
        BackdropCombo.SelectedIndex = _config.Appearance.Backdrop == BackdropPreference.Mica ? 1 : 0;
        OpacitySlider.Value = _config.Appearance.DefaultBoxOpacity;
        OpacityValueText.Text = $"{Math.Round(_config.Appearance.DefaultBoxOpacity * 100)}%";

        StartupCheck.IsChecked = _config.Behavior.LaunchAtStartup;
        DoubleClickDesktopCheck.IsChecked = _config.Behavior.DoubleClickDesktopToToggleBoxes;
        GridSnapCheck.IsChecked = _config.Behavior.EnableGridSnap;

        ShowDockCheck.IsChecked = _config.ShowDock;
        DockOptionsPanel.IsEnabled = _config.ShowDock;
        MagnificationCheck.IsChecked = _config.Dock.EnableMagnification;
        MagScaleSlider.Value = _config.Dock.MagnificationScale;
        MagScaleValueText.Text = $"{_config.Dock.MagnificationScale:0.00}x";
        FullscreenHideCheck.IsChecked = _config.Dock.AutoHideOnFullscreen;
        HideTaskbarCheck.IsChecked = _config.Dock.AutoHideSystemTaskbar;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

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

        _config.Appearance.DefaultBoxOpacity = Math.Round(OpacitySlider.Value, 2);
        OpacityValueText.Text = $"{Math.Round(_config.Appearance.DefaultBoxOpacity * 100)}%";

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

        _configService.Save(_config);
        _onSettingsApplied();
    }

    private void OnExportConfig(object sender, RoutedEventArgs e)
    {
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
        catch
        {
            // Ignore
        }
    }

    private void OnEmergencyRestore(object sender, RoutedEventArgs e)
    {
        _loading = true;
        HideTaskbarCheck.IsChecked = false;
        _config.Dock.AutoHideSystemTaskbar = false;
        _loading = false;

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
            var fresh = FirstRunInitializer.CreateDefaultConfig(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
            _configService.Save(fresh);
            _onConfigReplaced(fresh);
            Close();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
