using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public sealed class DesktopHostService : IDisposable
{
    private nint _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private long _lastClickTick;
    private NativeMethods.POINT _lastClickPoint;
    private Dispatcher? _dispatcher;
    private bool _disposed;

    public event EventHandler? EmptyDesktopDoubleClicked;

    public static (nint DesktopHostParent, nint DefView, nint SysListView) FindDesktopShellWindows()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != 0)
        {
            var defView = NativeMethods.FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
            if (defView != 0)
            {
                var listView = NativeMethods.FindWindowEx(defView, 0, "SysListView32", null);
                return (progman, defView, listView);
            }
        }

        nint foundWorker = 0;
        nint foundDefView = 0;
        nint foundListView = 0;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var defView = NativeMethods.FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null);
            if (defView != 0)
            {
                foundWorker = hwnd;
                foundDefView = defView;
                foundListView = NativeMethods.FindWindowEx(defView, 0, "SysListView32", null);
                return false;
            }
            return true;
        }, 0);

        return (foundWorker, foundDefView, foundListView);
    }

    /// <summary>
    /// Pins a WPF Window to the desktop Z-layer (above wallpaper/desktop icons, below normal app windows),
    /// hides it from Alt+Tab, and prevents Win+D (Show Desktop) from minimizing it.
    /// </summary>
    public static void AttachWindowToDesktopLayer(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        if (hwnd == 0) return;

        // Hide from Alt+Tab and taskbar
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        // Set owner to Progman/WorkerW hosting SHELLDLL_DefView so Win+D does NOT minimize this box
        var (desktopParent, _, _) = FindDesktopShellWindows();
        if (desktopParent != 0)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, desktopParent);
        }

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(DesktopLayerWndProc);

        SendToBottom(hwnd);
    }

    public static void SendToBottom(nint hwnd)
    {
        if (hwnd == 0) return;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public static void ConfigureFloatingDockWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    public static void BringWindowToForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return;

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    public static bool TryGetCursorScreenPosition(out int x, out int y)
    {
        if (NativeMethods.GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    public static bool IsCursorOverWindow(nint targetHwnd, int screenX, int screenY)
    {
        if (targetHwnd == 0) return false;
        var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = screenX, Y = screenY });
        if (hit == 0) return false;
        if (hit == targetHwnd) return true;
        return NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT) == targetHwnd;
    }

    private static nint DesktopLayerWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && lParam != 0)
        {
            var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            pos.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        else if (msg == NativeMethods.WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == NativeMethods.SC_MINIMIZE)
        {
            handled = true;
            return 0;
        }

        return 0;
    }

    public void StartEmptyDesktopDoubleClickMonitor(Dispatcher dispatcher)
    {
        if (_mouseHook != 0) return;
        _dispatcher = dispatcher;
        _mouseProc = LowLevelMouseCallback;
        var moduleHandle = NativeMethods.GetModuleHandle(null);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
    }

    public void StopEmptyDesktopDoubleClickMonitor()
    {
        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
        _mouseProc = null;
    }

    private nint LowLevelMouseCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (uint)wParam == NativeMethods.WM_LBUTTONDOWN && lParam != 0)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var now = Environment.TickCount64;
            var dblTime = NativeMethods.GetDoubleClickTime();

            var dx = Math.Abs(hookStruct.pt.X - _lastClickPoint.X);
            var dy = Math.Abs(hookStruct.pt.Y - _lastClickPoint.Y);

            if (now - _lastClickTick <= dblTime && dx <= 6 && dy <= 6)
            {
                _lastClickTick = 0;
                var pt = hookStruct.pt;
                _dispatcher?.BeginInvoke(DispatcherPriority.Background, () => CheckAndRaiseEmptyDesktopDoubleClick(pt));
            }
            else
            {
                _lastClickTick = now;
                _lastClickPoint = hookStruct.pt;
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void CheckAndRaiseEmptyDesktopDoubleClick(NativeMethods.POINT pt)
    {
        var hitHwnd = NativeMethods.WindowFromPoint(pt);
        if (hitHwnd == 0) return;

        var cls = NativeMethods.GetWindowClassName(hitHwnd);
        var (_, defView, listView) = FindDesktopShellWindows();

        if (hitHwnd == listView || cls.Equals("SysListView32", StringComparison.OrdinalIgnoreCase))
        {
            // Verify this SysListView32 belongs to the desktop DefView
            var root = NativeMethods.GetAncestor(hitHwnd, NativeMethods.GA_ROOT);
            var rootCls = NativeMethods.GetWindowClassName(root);
            if (!rootCls.Equals("Progman", StringComparison.OrdinalIgnoreCase) &&
                !rootCls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Clicking empty desktop leaves 0 selected icons; clicking an icon selects it
            var sent = NativeMethods.SendMessageTimeout(
                hitHwnd,
                NativeMethods.LVM_GETSELECTEDCOUNT,
                0,
                0,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                150,
                out var selectedCount);
            if (sent != 0 && selectedCount == 0)
                EmptyDesktopDoubleClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (hitHwnd == defView ||
                 cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
                 cls.Equals("Progman", StringComparison.OrdinalIgnoreCase))
        {
            EmptyDesktopDoubleClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopEmptyDesktopDoubleClickMonitor();
    }
}
