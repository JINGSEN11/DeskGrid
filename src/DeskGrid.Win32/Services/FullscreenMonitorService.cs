using System.Runtime.InteropServices;
using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public sealed class FullscreenMonitorService : IFullscreenMonitor
{
    private const long WS_CAPTION = 0x00C00000L;
    private const int FullscreenTolerancePx = 2;

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly object _stateLock = new();
    private Timer? _timer;
    private HashSet<nint> _fullscreenMonitors = [];
    private bool _isFullscreen;
    private bool _disposed;

    public event EventHandler<bool>? FullscreenStateChanged;
    public event EventHandler<IReadOnlySet<nint>>? FullscreenMonitorsChanged;

    public bool IsFullscreenActive
    {
        get
        {
            lock (_stateLock)
                return _isFullscreen;
        }
    }

    public IReadOnlySet<nint> FullscreenMonitorHandles
    {
        get
        {
            lock (_stateLock)
                return new HashSet<nint>(_fullscreenMonitors);
        }
    }

    public bool IsMonitorFullscreen(nint monitorHandle)
    {
        lock (_stateLock)
        {
            if (monitorHandle == 0)
                return _isFullscreen;

            return _fullscreenMonitors.Contains(monitorHandle);
        }
    }

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => PollFullscreenState(), null, 500, 650);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void PollFullscreenState()
    {
        if (_disposed) return;

        var detectedMonitors = DetectFullscreenMonitors();
        var detectedAny = detectedMonitors.Count > 0;
        bool anyChanged;
        bool setChanged;

        lock (_stateLock)
        {
            anyChanged = detectedAny != _isFullscreen;
            setChanged = !_fullscreenMonitors.SetEquals(detectedMonitors);
            if (setChanged)
                _fullscreenMonitors = detectedMonitors;
            _isFullscreen = detectedAny;
        }

        if (setChanged)
            FullscreenMonitorsChanged?.Invoke(this, new HashSet<nint>(detectedMonitors));
        if (anyChanged)
            FullscreenStateChanged?.Invoke(this, detectedAny);
    }

    private HashSet<nint> DetectFullscreenMonitors()
    {
        var fullscreenMonitors = new HashSet<nint>();

        // 1. Exclusive D3D fullscreen check (only QUNS_RUNNING_D3D_FULL_SCREEN; ignores Focus Assist / QUNS_BUSY)
        if (NativeMethods.SHQueryUserNotificationState(out var state) == 0 &&
            state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
        {
            var fg = NativeMethods.GetForegroundWindow();
            var d3dMonitor = fg != 0
                ? NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST)
                : NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            if (d3dMonitor != 0)
                fullscreenMonitors.Add(d3dMonitor);
        }

        // 2. Per-monitor Z-order inspection: check the topmost normal window covering each monitor
        var decidedMonitors = new HashSet<nint>(fullscreenMonitors);

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == 0 || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
                return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _ownProcessId)
                return true;

            if (IsWindowCloaked(hwnd))
                return true;

            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            var cls = NativeMethods.GetWindowClassName(hwnd);
            if (IsIgnoredShellClass(cls))
                return true;

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                return true;

            if (rect.Width < 120 || rect.Height < 120)
                return true;

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == 0 || decidedMonitors.Contains(monitor))
                return true;

            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
                return true;

            if (IsWindowFullscreenOnMonitor(hwnd, rect, mi))
            {
                fullscreenMonitors.Add(monitor);
                decidedMonitors.Add(monitor);
            }
            else if (CoversSignificantArea(rect, mi.rcMonitor))
            {
                // A normal non-fullscreen window is visibly above any fullscreen window on this monitor
                decidedMonitors.Add(monitor);
            }

            return true;
        }, 0);

        return fullscreenMonitors;
    }

    private static bool IsWindowFullscreenOnMonitor(
        nint hwnd,
        NativeMethods.RECT rect,
        NativeMethods.MONITORINFO mi)
    {
        var coversFullMonitor =
            rect.Left <= mi.rcMonitor.Left + FullscreenTolerancePx &&
            rect.Top <= mi.rcMonitor.Top + FullscreenTolerancePx &&
            rect.Right >= mi.rcMonitor.Right - FullscreenTolerancePx &&
            rect.Bottom >= mi.rcMonitor.Bottom - FullscreenTolerancePx;

        if (!coversFullMonitor)
            return false;

        // Standard maximized windows still carry WS_CAPTION; borderless fullscreen games/videos cover full rcMonitor without standard titlebar
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;

        if (hasCaption && mi.rcWork.Bottom < mi.rcMonitor.Bottom && rect.Bottom <= mi.rcWork.Bottom + FullscreenTolerancePx)
            return false;

        return !hasCaption || rect.Bottom >= mi.rcMonitor.Bottom - FullscreenTolerancePx;
    }

    private static bool CoversSignificantArea(NativeMethods.RECT rect, NativeMethods.RECT monitorRect)
    {
        var intersectW = Math.Max(0, Math.Min(rect.Right, monitorRect.Right) - Math.Max(rect.Left, monitorRect.Left));
        var intersectH = Math.Max(0, Math.Min(rect.Bottom, monitorRect.Bottom) - Math.Max(rect.Top, monitorRect.Top));
        var monitorArea = (long)monitorRect.Width * monitorRect.Height;
        if (monitorArea <= 0)
            return false;

        return (long)intersectW * intersectH * 4 >= monitorArea;
    }

    private static bool IsWindowCloaked(nint hwnd)
    {
        return NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 &&
               cloaked != 0;
    }

    private static bool IsIgnoredShellClass(string cls) =>
        cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
