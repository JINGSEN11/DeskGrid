using System.Runtime.InteropServices;
using DeskGrid.Core.Models;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public static class DwmBackdropService
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    public static bool IsWindows11OrGreater => Environment.OSVersion.Version.Build >= 22000;
    public static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

    public static void ApplyWindowChrome(
        nint hwnd,
        BackdropPreference backdrop,
        bool isDarkMode,
        bool roundedCorners = true,
        bool applySystemBackdrop = true)
    {
        if (hwnd == 0) return;

        var dark = isDarkMode ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (IsWindows11OrGreater && roundedCorners)
        {
            var corner = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }

        if (!applySystemBackdrop)
            return;

        if (SupportsSystemBackdrop)
        {
            var backdropType = backdrop == BackdropPreference.Mica
                ? NativeMethods.DWMSBT_MAINWINDOW
                : NativeMethods.DWMSBT_TRANSIENTWINDOW;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
        }
        else
        {
            ApplyWin10AcrylicFallback(hwnd, isDarkMode);
        }
    }

    public static void ApplyWin10AcrylicFallback(nint hwnd, bool isDarkMode)
    {
        var accent = new NativeMethods.ACCENT_POLICY
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = isDarkMode ? 0xB8202020u : 0xB8F3F3F3u
        };

        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new NativeMethods.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = size,
                Data = ptr
            };
            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
