using System.Runtime.InteropServices;
using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public sealed class FullscreenMonitorService : IFullscreenMonitor
{
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private Timer? _timer;
    private bool _isFullscreen;
    private bool _disposed;

    public event EventHandler<bool>? FullscreenStateChanged;
    public bool IsFullscreenActive => _isFullscreen;

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => PollFullscreenState(), null, 500, 800);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void PollFullscreenState()
    {
        if (_disposed) return;

        var detected = DetectFullscreen();
        if (detected != _isFullscreen)
        {
            _isFullscreen = detected;
            FullscreenStateChanged?.Invoke(this, detected);
        }
    }

    private const long WS_CAPTION = 0x00C00000L;

    private bool DetectFullscreen()
    {
        // Only treat true exclusive D3D fullscreen as fullscreen (ignore QUNS_BUSY which triggers on Focus Assist / normal apps)
        if (NativeMethods.SHQueryUserNotificationState(out var state) == 0 &&
            state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
        {
            return true;
        }

        var fg = NativeMethods.GetForegroundWindow();
        if (fg == 0 || !NativeMethods.IsWindowVisible(fg))
            return false;

        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (pid == _ownProcessId)
            return false;

        var cls = NativeMethods.GetWindowClassName(fg);
        if (cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Standard maximized windows still carry WS_CAPTION; borderless fullscreen games/videos cover full rcMonitor without standard titlebar
        var style = NativeMethods.GetWindowLongPtr(fg, NativeMethods.GWL_STYLE).ToInt64();
        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;

        if (!NativeMethods.GetWindowRect(fg, out var rect))
            return false;

        var monitor = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
            return false;

        const int tolerance = 2;
        var coversFullMonitor =
            rect.Left <= mi.rcMonitor.Left + tolerance &&
            rect.Top <= mi.rcMonitor.Top + tolerance &&
            rect.Right >= mi.rcMonitor.Right - tolerance &&
            rect.Bottom >= mi.rcMonitor.Bottom - tolerance;

        if (!coversFullMonitor)
            return false;

        // If the window still has WS_CAPTION, ensure it's actually covering beyond WorkArea (i.e. covering the taskbar)
        if (hasCaption && mi.rcWork.Bottom < mi.rcMonitor.Bottom && rect.Bottom <= mi.rcWork.Bottom + tolerance)
            return false;

        return !hasCaption || rect.Bottom >= mi.rcMonitor.Bottom - tolerance;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
