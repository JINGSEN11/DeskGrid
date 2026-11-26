using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeskGrid.App.Helpers;
using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Services;

namespace DeskGrid.App.Services;

public sealed class TrayIconController : IDisposable
{
    private static readonly Uri AppIconPackUri = new("pack://application:,,,/Assets/app.ico", UriKind.Absolute);

    private readonly NativeTrayIcon _trayIcon = new();
    private readonly INativeDesktopIconService _nativeIcons;
    private readonly Func<bool> _areBoxesVisible;
    private readonly Func<bool> _isDockVisible;
    private readonly Action _onCreateBox;
    private readonly Action _onToggleBoxes;
    private readonly Action _onToggleDock;
    private readonly Action _onToggleNativeIcons;
    private readonly Action _onRestoreTaskbar;
    private readonly Action _onOpenSettings;
    private readonly Action _onExit;
    private ContextMenu? _activeContextMenu;

    public TrayIconController(
        INativeDesktopIconService nativeIcons,
        Func<bool> areBoxesVisible,
        Func<bool> isDockVisible,
        Action onCreateBox,
        Action onToggleBoxes,
        Action onToggleDock,
        Action onToggleNativeIcons,
        Action onRestoreTaskbar,
        Action onOpenSettings,
        Action onExit)
    {
        _nativeIcons = nativeIcons;
        _areBoxesVisible = areBoxesVisible;
        _isDockVisible = isDockVisible;
        _onCreateBox = onCreateBox;
        _onToggleBoxes = onToggleBoxes;
        _onToggleDock = onToggleDock;
        _onToggleNativeIcons = onToggleNativeIcons;
        _onRestoreTaskbar = onRestoreTaskbar;
        _onOpenSettings = onOpenSettings;
        _onExit = onExit;

        _trayIcon.LeftDoubleClicked += (_, _) => _onToggleBoxes();
        _trayIcon.RightClicked += (_, _) => ShowFluentTrayMenu();
        _trayIcon.Show("DeskGrid - 桌面整理助手", LoadAppIconBytes());
    }

    private static byte[]? LoadAppIconBytes()
    {
        try
        {
            var streamInfo = Application.GetResourceStream(AppIconPackUri);
            if (streamInfo?.Stream is null)
                return null;

            using var stream = streamInfo.Stream;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private void ShowFluentTrayMenu()
    {
        if (_activeContextMenu is not null)
        {
            _activeContextMenu.IsOpen = false;
            _activeContextMenu = null;
        }

        var menu = ThemePalette.CreateThemedContextMenu();
        menu.Placement = PlacementMode.MousePoint;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
                _activeContextMenu = null;
        };

        var newBoxMi = new MenuItem { Header = "新建桌面盒子..." };
        newBoxMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onCreateBox();
        };
        menu.Items.Add(newBoxMi);

        var toggleBoxesMi = new MenuItem
        {
            Header = _areBoxesVisible() ? "隐藏所有桌面盒子" : "显示所有桌面盒子"
        };
        toggleBoxesMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onToggleBoxes();
        };
        menu.Items.Add(toggleBoxesMi);

        var toggleDockMi = new MenuItem
        {
            Header = _isDockVisible() ? "隐藏 Dock 栏" : "显示 Dock 栏"
        };
        toggleDockMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onToggleDock();
        };
        menu.Items.Add(toggleDockMi);

        var toggleNativeMi = new MenuItem
        {
            Header = _nativeIcons.AreIconsVisible() ? "隐藏系统原生桌面图标" : "显示系统原生桌面图标"
        };
        toggleNativeMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onToggleNativeIcons();
        };
        menu.Items.Add(toggleNativeMi);

        menu.Items.Add(new Separator());

        var restoreTaskbarMi = new MenuItem { Header = "恢复系统任务栏" };
        restoreTaskbarMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onRestoreTaskbar();
        };
        menu.Items.Add(restoreTaskbarMi);

        var settingsMi = new MenuItem { Header = "偏好设置..." };
        settingsMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onOpenSettings();
        };
        menu.Items.Add(settingsMi);

        menu.Items.Add(new Separator());

        var exitMi = new MenuItem { Header = "安全退出 DeskGrid" };
        exitMi.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _onExit();
        };
        menu.Items.Add(exitMi);

        _activeContextMenu = menu;
        menu.IsOpen = true;
    }

    public void Dispose() => _trayIcon.Dispose();
}
