using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeskGrid.App.Helpers;
using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Services;

namespace DeskGrid.App.Services;

public sealed class TrayIconController : IDisposable
{
    private readonly NativeTrayIcon _trayIcon = new();
    private readonly INativeDesktopIconService _nativeIcons;
    private readonly Func<bool> _areBoxesVisible;
    private readonly Action _onCreateBox;
    private readonly Action _onToggleBoxes;
    private readonly Action _onToggleNativeIcons;
    private readonly Action _onRestoreTaskbar;
    private readonly Action _onOpenSettings;
    private readonly Action _onExit;

    public TrayIconController(
        INativeDesktopIconService nativeIcons,
        Func<bool> areBoxesVisible,
        Action onCreateBox,
        Action onToggleBoxes,
        Action onToggleNativeIcons,
        Action onRestoreTaskbar,
        Action onOpenSettings,
        Action onExit)
    {
        _nativeIcons = nativeIcons;
        _areBoxesVisible = areBoxesVisible;
        _onCreateBox = onCreateBox;
        _onToggleBoxes = onToggleBoxes;
        _onToggleNativeIcons = onToggleNativeIcons;
        _onRestoreTaskbar = onRestoreTaskbar;
        _onOpenSettings = onOpenSettings;
        _onExit = onExit;

        _trayIcon.LeftDoubleClicked += (_, _) => _onToggleBoxes();
        _trayIcon.RightClicked += (_, _) => ShowFluentTrayMenu();
        _trayIcon.Show("DeskGrid - 桌面整理助手");
    }

    private void ShowFluentTrayMenu()
    {
        var menu = ThemePalette.CreateThemedContextMenu();
        menu.Placement = PlacementMode.MousePoint;

        var newBoxMi = new MenuItem { Header = "新建桌面盒子..." };
        newBoxMi.Click += (_, _) => _onCreateBox();
        menu.Items.Add(newBoxMi);

        var toggleBoxesMi = new MenuItem
        {
            Header = _areBoxesVisible() ? "隐藏所有桌面盒子" : "显示所有桌面盒子"
        };
        toggleBoxesMi.Click += (_, _) => _onToggleBoxes();
        menu.Items.Add(toggleBoxesMi);

        var toggleNativeMi = new MenuItem
        {
            Header = _nativeIcons.AreIconsVisible() ? "隐藏系统原生桌面图标" : "显示系统原生桌面图标"
        };
        toggleNativeMi.Click += (_, _) => _onToggleNativeIcons();
        menu.Items.Add(toggleNativeMi);

        menu.Items.Add(new Separator());

        var restoreTaskbarMi = new MenuItem { Header = "恢复系统任务栏" };
        restoreTaskbarMi.Click += (_, _) => _onRestoreTaskbar();
        menu.Items.Add(restoreTaskbarMi);

        var settingsMi = new MenuItem { Header = "偏好设置..." };
        settingsMi.Click += (_, _) => _onOpenSettings();
        menu.Items.Add(settingsMi);

        menu.Items.Add(new Separator());

        var exitMi = new MenuItem { Header = "安全退出 DeskGrid" };
        exitMi.Click += (_, _) => _onExit();
        menu.Items.Add(exitMi);

        menu.IsOpen = true;
    }

    public void Dispose() => _trayIcon.Dispose();
}
