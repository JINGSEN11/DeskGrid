using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public sealed class NativeDesktopIconService : INativeDesktopIconService
{
    private const nint ToggleDesktopIconsCommand = 0x7402;

    private nint _cachedDefView;
    private nint _cachedListView;

    private (nint DefView, nint ListView) GetDesktopViews()
    {
        if (_cachedDefView != 0 && _cachedListView != 0 &&
            NativeMethods.IsWindow(_cachedDefView) && NativeMethods.IsWindow(_cachedListView))
        {
            return (_cachedDefView, _cachedListView);
        }

        var (_, defView, listView) = DesktopHostService.FindDesktopShellWindows();
        _cachedDefView = defView;
        _cachedListView = listView;
        return (defView, listView);
    }

    public bool AreIconsVisible()
    {
        var (_, listView) = GetDesktopViews();
        if (listView == 0)
            return true;

        return NativeMethods.IsWindowVisible(listView);
    }

    public bool SetIconsVisible(bool visible)
    {
        if (AreIconsVisible() == visible)
            return visible;

        return ToggleIcons();
    }

    public bool ToggleIcons()
    {
        var (defView, listView) = GetDesktopViews();
        if (defView != 0)
        {
            NativeMethods.SendMessageTimeout(
                defView,
                NativeMethods.WM_COMMAND,
                ToggleDesktopIconsCommand,
                0,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                200,
                out _);
            return AreIconsVisible();
        }

        if (listView != 0)
        {
            var currentlyVisible = NativeMethods.IsWindowVisible(listView);
            NativeMethods.ShowWindow(listView, currentlyVisible ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOW);
            return !currentlyVisible;
        }

        return true;
    }
}
