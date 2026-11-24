using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public sealed class NativeDesktopIconService : INativeDesktopIconService
{
    private const nint ToggleDesktopIconsCommand = 0x7402;

    public bool AreIconsVisible()
    {
        var (_, _, listView) = DesktopHostService.FindDesktopShellWindows();
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
        var (_, defView, listView) = DesktopHostService.FindDesktopShellWindows();
        if (defView != 0)
        {
            NativeMethods.SendMessage(defView, NativeMethods.WM_COMMAND, ToggleDesktopIconsCommand, 0);
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
