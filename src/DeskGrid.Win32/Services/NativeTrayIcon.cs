using System.Runtime.InteropServices;
using System.Windows.Interop;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

/// <summary>
/// Zero-WinForms native Win32 system tray icon using Shell_NotifyIconW, allowing
/// DeskGrid.App to display a modern WPF Fluent ContextMenu on right-click.
/// </summary>
public sealed class NativeTrayIcon : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_USER + 0x101;
    private const uint TrayIconId = 1;
    private static readonly nint IDI_APPLICATION = 32512;

    private HwndSource? _messageWindow;
    private bool _added;
    private bool _disposed;

    public event EventHandler? LeftClicked;
    public event EventHandler? LeftDoubleClicked;
    public event EventHandler? RightClicked;

    public void Show(string tooltip)
    {
        if (_disposed) return;

        if (_messageWindow is null)
        {
            var parameters = new HwndSourceParameters("DeskGridTrayHost")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _messageWindow = new HwndSource(parameters);
            _messageWindow.AddHook(WndProc);
        }

        var hIcon = NativeMethods.LoadIcon(0, IDI_APPLICATION);
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _messageWindow.Handle,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = hIcon,
            szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip
        };

        var msg = _added ? NativeMethods.NIM_MODIFY : NativeMethods.NIM_ADD;
        if (NativeMethods.Shell_NotifyIcon(msg, ref data))
            _added = true;
    }

    public void Remove()
    {
        if (!_added || _messageWindow is null) return;

        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _messageWindow.Handle,
            uID = TrayIconId
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _added = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg == TrayCallbackMessage)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMsg)
            {
                case NativeMethods.WM_LBUTTONUP:
                    LeftClicked?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case NativeMethods.WM_LBUTTONDBLCLK:
                    LeftDoubleClicked?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case NativeMethods.WM_RBUTTONUP:
                    RightClicked?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Remove();
        _messageWindow?.RemoveHook(WndProc);
        _messageWindow?.Dispose();
        _messageWindow = null;
    }
}
