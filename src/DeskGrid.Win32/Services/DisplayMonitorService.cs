using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

/// <summary>
/// Caches physical monitor geometry and per-monitor DPI in memory using native Win32
/// EnumDisplayMonitors / GetMonitorInfo / GetDpiForMonitor.
/// Eliminates high-frequency GDI/WinForms Screen.AllScreens enumeration from all timers.
/// </summary>
public static class DisplayMonitorService
{
    private static readonly object CacheLock = new();
    private static IReadOnlyList<DisplayMonitorInfo> _cachedMonitors = [];
    private static bool _cacheInitialized;

    /// <summary>
    /// Returns the in-memory cached monitor list with zero GDI/Win32 calls on hot paths.
    /// Automatically populates once on first access if RefreshMonitors() has not yet been called.
    /// </summary>
    public static IReadOnlyList<DisplayMonitorInfo> GetCachedMonitors()
    {
        lock (CacheLock)
        {
            if (_cacheInitialized && _cachedMonitors.Count > 0)
                return _cachedMonitors;
        }

        return RefreshMonitors();
    }

    /// <summary>
    /// Returns cached monitors by default; pass forceRefresh: true only on startup,
    /// SystemEvents.DisplaySettingsChanged, or taskbar work-area transitions.
    /// </summary>
    public static IReadOnlyList<DisplayMonitorInfo> GetAllMonitors(bool forceRefresh = false)
    {
        return forceRefresh ? RefreshMonitors() : GetCachedMonitors();
    }

    /// <summary>
    /// Re-enumerates physical monitors via Win32 EnumDisplayMonitors + GetMonitorInfo + GetDpiForMonitor
    /// and updates the in-memory cache atomically.
    /// </summary>
    public static IReadOnlyList<DisplayMonitorInfo> RefreshMonitors()
    {
        var enumerated = EnumerateNativeMonitors();
        if (enumerated.Count == 0)
            enumerated.Add(BuildFallbackPrimaryMonitor());

        var snapshot = enumerated.ToArray();
        lock (CacheLock)
        {
            _cachedMonitors = snapshot;
            _cacheInitialized = true;
        }

        return snapshot;
    }

    public static DisplayMonitorInfo GetPrimaryMonitor()
    {
        var monitors = GetCachedMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static DisplayMonitorInfo GetMonitorForCursor(IReadOnlyList<DisplayMonitorInfo>? cachedMonitors = null)
    {
        var monitors = cachedMonitors is { Count: > 0 } ? cachedMonitors : GetCachedMonitors();
        if (DesktopHostService.TryGetCursorScreenPosition(out var x, out var y))
        {
            var hit = DockPositionCalculator.FindMonitorForPoint(monitors, x, y);
            if (hit is not null)
                return hit;
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>
    /// Resolves a monitor's live physical bounds, working area, and DPI directly via Win32 GetMonitorInfo.
    /// Never falls back to SystemParameters.WorkArea for secondary displays.
    /// </summary>
    public static bool TryGetMonitorInfoByHandle(nint hMonitor, out DisplayMonitorInfo monitor)
    {
        monitor = null!;
        if (hMonitor == 0)
            return false;

        try
        {
            var mi = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };

            if (!NativeMethods.GetMonitorInfoEx(hMonitor, ref mi))
                return false;

            var (dpiScaleX, dpiScaleY) = QueryMonitorDpiScale(hMonitor);
            var deviceName = !string.IsNullOrWhiteSpace(mi.szDevice)
                ? mi.szDevice
                : $"DISPLAY_{hMonitor}";

            monitor = new DisplayMonitorInfo(
                DeviceName: deviceName,
                Handle: hMonitor,
                IsPrimary: (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                BoundsPx: new PhysicalRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                WorkingAreaPx: new PhysicalRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
                DpiScaleX: dpiScaleX,
                DpiScaleY: dpiScaleY);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Queries the monitor currently hosting a WPF Window via Win32 MonitorFromWindow + GetMonitorInfo.
    /// </summary>
    public static bool TryGetMonitorInfoForWindow(Window window, out DisplayMonitorInfo monitor)
    {
        monitor = null!;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return false;

        var hMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return TryGetMonitorInfoByHandle(hMonitor, out monitor);
    }

    public static nint GetWindowMonitorHandle(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return 0;

        return NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
    }

    public static bool TryGetWindowPhysicalRect(Window window, out PhysicalRect rect)
    {
        rect = default;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var nativeRect))
            return false;

        rect = new PhysicalRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
        return true;
    }

    public static bool MoveWindowPhysical(Window window, int physicalLeft, int physicalTop)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return false;

        return NativeMethods.SetWindowPos(
            hwnd,
            0,
            physicalLeft,
            physicalTop,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    public static void SetWindowTopmostNoActivate(Window window, bool topmost)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return;

        NativeMethods.SetWindowPos(
            hwnd,
            topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private static List<DisplayMonitorInfo> EnumerateNativeMonitors()
    {
        var result = new List<DisplayMonitorInfo>();

        try
        {
            var index = 0;
            NativeMethods.EnumDisplayMonitors(
                0,
                0,
                (nint hMonitor, nint _, ref NativeMethods.RECT _, nint _) =>
                {
                    index++;
                    if (TryGetMonitorInfoByHandle(hMonitor, out var info))
                    {
                        result.Add(info);
                    }
                    else
                    {
                        var mi = new NativeMethods.MONITORINFO
                        {
                            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
                        };
                        if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                        {
                            var (dpiX, dpiY) = QueryMonitorDpiScale(hMonitor);
                            result.Add(new DisplayMonitorInfo(
                                DeviceName: $"DISPLAY_{index}",
                                Handle: hMonitor,
                                IsPrimary: (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                                BoundsPx: new PhysicalRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                                WorkingAreaPx: new PhysicalRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
                                DpiScaleX: dpiX,
                                DpiScaleY: dpiY));
                        }
                    }

                    return true;
                },
                0);
        }
        catch
        {
            // Fallback handled by caller
        }

        // Stable ordering: primary first, then left-to-right, top-to-bottom
        result.Sort((a, b) =>
        {
            var primaryCmp = b.IsPrimary.CompareTo(a.IsPrimary);
            if (primaryCmp != 0) return primaryCmp;
            var leftCmp = a.BoundsPx.Left.CompareTo(b.BoundsPx.Left);
            return leftCmp != 0 ? leftCmp : a.BoundsPx.Top.CompareTo(b.BoundsPx.Top);
        });

        return result;
    }

    private static DisplayMonitorInfo BuildFallbackPrimaryMonitor()
    {
        var w = (int)Math.Max(800, Math.Round(SystemParameters.PrimaryScreenWidth));
        var h = (int)Math.Max(600, Math.Round(SystemParameters.PrimaryScreenHeight));
        var work = SystemParameters.WorkArea;
        return new DisplayMonitorInfo(
            DeviceName: "PRIMARY",
            Handle: 0,
            IsPrimary: true,
            BoundsPx: new PhysicalRect(0, 0, w, h),
            WorkingAreaPx: new PhysicalRect(
                (int)Math.Round(work.Left),
                (int)Math.Round(work.Top),
                (int)Math.Round(work.Right),
                (int)Math.Round(work.Bottom)),
            DpiScaleX: 1.0,
            DpiScaleY: 1.0);
    }

    private static (double ScaleX, double ScaleY) QueryMonitorDpiScale(nint hMonitor)
    {
        if (hMonitor != 0)
        {
            try
            {
                if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0 &&
                    dpiX >= 48 && dpiY >= 48)
                {
                    return (dpiX / 96.0, dpiY / 96.0);
                }
            }
            catch
            {
                // Older Windows builds without shcore.dll fall back to 1.0
            }
        }

        return (1.0, 1.0);
    }
}
