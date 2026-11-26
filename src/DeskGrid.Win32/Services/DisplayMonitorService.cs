using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Interop;
using FormsScreen = System.Windows.Forms.Screen;

namespace DeskGrid.Win32.Services;

/// <summary>
/// Enumerates physical monitors via System.Windows.Forms.Screen.AllScreens with accurate
/// per-monitor DPI scaling and provides PerMonitorV2-safe physical window positioning helpers.
/// </summary>
public static class DisplayMonitorService
{
    public static IReadOnlyList<DisplayMonitorInfo> GetAllMonitors()
    {
        var result = new List<DisplayMonitorInfo>();

        try
        {
            var screens = FormsScreen.AllScreens;
            for (var i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                var bounds = screen.Bounds;
                var work = screen.WorkingArea;

                var centerPt = new NativeMethods.POINT
                {
                    X = bounds.Left + Math.Max(0, bounds.Width / 2),
                    Y = bounds.Top + Math.Max(0, bounds.Height / 2)
                };
                var hMonitor = NativeMethods.MonitorFromPoint(centerPt, NativeMethods.MONITOR_DEFAULTTONEAREST);

                var (dpiScaleX, dpiScaleY) = QueryMonitorDpiScale(hMonitor);
                var deviceName = !string.IsNullOrWhiteSpace(screen.DeviceName)
                    ? screen.DeviceName
                    : $"DISPLAY_{i + 1}";

                result.Add(new DisplayMonitorInfo(
                    DeviceName: deviceName,
                    Handle: hMonitor,
                    IsPrimary: screen.Primary,
                    BoundsPx: new PhysicalRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                    WorkingAreaPx: new PhysicalRect(work.Left, work.Top, work.Right, work.Bottom),
                    DpiScaleX: dpiScaleX,
                    DpiScaleY: dpiScaleY));
            }
        }
        catch
        {
            // Fallback below if WinForms Screen enumeration fails in headless/unusual environments
        }

        if (result.Count == 0)
        {
            var w = (int)Math.Max(800, Math.Round(SystemParameters.PrimaryScreenWidth));
            var h = (int)Math.Max(600, Math.Round(SystemParameters.PrimaryScreenHeight));
            var work = SystemParameters.WorkArea;
            result.Add(new DisplayMonitorInfo(
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
                DpiScaleY: 1.0));
        }

        return result;
    }

    public static DisplayMonitorInfo GetPrimaryMonitor()
    {
        var monitors = GetAllMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static DisplayMonitorInfo GetMonitorForCursor(IReadOnlyList<DisplayMonitorInfo>? cachedMonitors = null)
    {
        var monitors = cachedMonitors is { Count: > 0 } ? cachedMonitors : GetAllMonitors();
        if (DesktopHostService.TryGetCursorScreenPosition(out var x, out var y))
        {
            var hit = DockPositionCalculator.FindMonitorForPoint(monitors, x, y);
            if (hit is not null)
                return hit;
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
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
