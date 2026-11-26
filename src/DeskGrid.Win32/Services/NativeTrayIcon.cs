using System.Buffers.Binary;
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
    private nint _ownedIconHandle;
    private bool _added;
    private bool _disposed;

    public event EventHandler? LeftClicked;
    public event EventHandler? LeftDoubleClicked;
    public event EventHandler? RightClicked;

    public void Show(string tooltip, byte[]? icoBytes = null)
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

        var hIcon = ResolveTrayIconHandle(icoBytes);
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

    private nint ResolveTrayIconHandle(byte[]? icoBytes)
    {
        var cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        var cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON);
        if (cx <= 0) cx = 16;
        if (cy <= 0) cy = 16;

        nint customHandle = 0;

        if (icoBytes is { Length: > 22 })
            customHandle = CreateHandleFromIcoBytes(icoBytes, cx, cy);

        if (customHandle == 0)
        {
            var hModule = NativeMethods.GetModuleHandle(null);
            if (hModule != 0)
            {
                customHandle = NativeMethods.LoadImage(
                    hModule,
                    IDI_APPLICATION,
                    NativeMethods.IMAGE_ICON,
                    cx,
                    cy,
                    NativeMethods.LR_DEFAULTCOLOR);
            }
        }

        if (customHandle != 0)
        {
            ReleaseOwnedIcon();
            _ownedIconHandle = customHandle;
            return _ownedIconHandle;
        }

        return NativeMethods.LoadIcon(0, IDI_APPLICATION);
    }

    private static nint CreateHandleFromIcoBytes(byte[] icoBytes, int targetWidth, int targetHeight)
    {
        try
        {
            // ICONDIR: reserved(2) == 0, type(2) == 1 (icon), count(2)
            var reserved = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(0, 2));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(2, 2));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(4, 2));
            if (reserved != 0 || type != 1 || count == 0 || icoBytes.Length < 6 + count * 16)
                return 0;

            var desired = Math.Max(targetWidth, targetHeight);
            int bestScore = int.MaxValue;
            int bestOffset = 0;
            int bestLength = 0;

            for (var i = 0; i < count; i++)
            {
                var entryOff = 6 + i * 16;
                var w = icoBytes[entryOff] == 0 ? 256 : icoBytes[entryOff];
                var h = icoBytes[entryOff + 1] == 0 ? 256 : icoBytes[entryOff + 1];
                var bytesInRes = BinaryPrimitives.ReadInt32LittleEndian(icoBytes.AsSpan(entryOff + 8, 4));
                var imageOffset = BinaryPrimitives.ReadInt32LittleEndian(icoBytes.AsSpan(entryOff + 12, 4));

                if (bytesInRes <= 0 || imageOffset < 0 || imageOffset + bytesInRes > icoBytes.Length)
                    continue;

                var size = Math.Max(w, h);
                // Prefer exact match, then smallest size >= desired (crisp downscale), then largest < desired
                var score = size == desired
                    ? 0
                    : size > desired
                        ? (size - desired)
                        : 1000 + (desired - size);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestOffset = imageOffset;
                    bestLength = bytesInRes;
                }
            }

            if (bestLength <= 0)
                return 0;

            var slice = new byte[bestLength];
            Buffer.BlockCopy(icoBytes, bestOffset, slice, 0, bestLength);
            return NativeMethods.CreateIconFromResourceEx(
                slice,
                (uint)slice.Length,
                fIcon: true,
                dwVer: 0x00030000,
                cxDesired: targetWidth,
                cyDesired: targetHeight,
                flags: NativeMethods.LR_DEFAULTCOLOR);
        }
        catch
        {
            return 0;
        }
    }

    private void ReleaseOwnedIcon()
    {
        if (_ownedIconHandle != 0)
        {
            NativeMethods.DestroyIcon(_ownedIconHandle);
            _ownedIconHandle = 0;
        }
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
                    // Promote the tray host window before opening the context menu so Windows grants
                    // foreground activation privileges to subsequent windows (e.g. SettingsWindow).
                    NativeMethods.SetForegroundWindow(hwnd);
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
        ReleaseOwnedIcon();
        _messageWindow?.RemoveHook(WndProc);
        _messageWindow?.Dispose();
        _messageWindow = null;
    }
}
