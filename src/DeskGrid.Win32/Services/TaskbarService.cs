using System.IO;
using System.Runtime.InteropServices;
using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

/// <summary>
/// Hides and restores Windows 10/11 (including Win11 24H2/25H2 Build 26200) native taskbars safely.
/// Uses AppBar ABS_AUTOHIDE + ShowWindow/SetWindowPos SWP_HIDEWINDOW + lightweight visibility watchdog
/// to eliminate bottom-edge badge/red-dot artifacts, and persists original AppBar state in a crash sentinel
/// so normal exit or crash recovery restores 100% of original taskbar state.
/// </summary>
public sealed class TaskbarService : ITaskbarService
{
    private const uint HideSwpFlags =
        NativeMethods.SWP_NOMOVE |
        NativeMethods.SWP_NOSIZE |
        NativeMethods.SWP_NOZORDER |
        NativeMethods.SWP_NOACTIVATE |
        NativeMethods.SWP_HIDEWINDOW;

    private const uint ShowSwpFlags =
        NativeMethods.SWP_NOMOVE |
        NativeMethods.SWP_NOSIZE |
        NativeMethods.SWP_NOZORDER |
        NativeMethods.SWP_NOACTIVATE |
        NativeMethods.SWP_SHOWWINDOW;

    private readonly string _sentinelFilePath;
    private readonly object _lock = new();
    private Timer? _enforceHiddenTimer;
    private int? _originalAppBarState;
    private bool _isHiddenByApp;
    private bool _disposed;

    public bool IsHiddenByApp => _isHiddenByApp;

    public TaskbarService(string configDirectory)
    {
        _sentinelFilePath = Path.Combine(configDirectory, "taskbar.hidden.flag");
    }

    public void RecoverFromCrashIfNeeded()
    {
        lock (_lock)
        {
            if (File.Exists(_sentinelFilePath))
            {
                var savedState = ReadSentinelOriginalState();
                StopEnforceTimerLocked();
                RestoreAppBarState(savedState);
                ShowAllTaskbars();
                TryDeleteSentinel();
                _originalAppBarState = null;
                _isHiddenByApp = false;
            }
        }
    }

    public void SetTaskbarHidden(bool hidden)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            if (hidden)
            {
                if (!_isHiddenByApp)
                {
                    _originalAppBarState = QueryAppBarState();
                    WriteSentinel(_originalAppBarState.Value);
                    SetAppBarAutoHide(_originalAppBarState.Value, enableAutoHide: true);
                }

                HideAllTaskbars();
                _isHiddenByApp = true;
                StartEnforceTimerLocked();
            }
            else
            {
                StopEnforceTimerLocked();
                var restoreState = _originalAppBarState ?? ReadSentinelOriginalState();
                if (_isHiddenByApp || File.Exists(_sentinelFilePath))
                {
                    RestoreAppBarState(restoreState);
                }
                ShowAllTaskbars();
                TryDeleteSentinel();
                _originalAppBarState = null;
                _isHiddenByApp = false;
            }
        }
    }

    public void RestoreIfHidden()
    {
        lock (_lock)
        {
            if (_isHiddenByApp || File.Exists(_sentinelFilePath))
            {
                StopEnforceTimerLocked();
                var restoreState = _originalAppBarState ?? ReadSentinelOriginalState();
                RestoreAppBarState(restoreState);
                ShowAllTaskbars();
                TryDeleteSentinel();
                _originalAppBarState = null;
                _isHiddenByApp = false;
            }
        }
    }

    private void StartEnforceTimerLocked()
    {
        _enforceHiddenTimer ??= new Timer(_ => EnforceHiddenTick(), null, Timeout.Infinite, Timeout.Infinite);
        _enforceHiddenTimer.Change(800, 1000);
    }

    private void StopEnforceTimerLocked()
    {
        _enforceHiddenTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void EnforceHiddenTick()
    {
        lock (_lock)
        {
            if (!_isHiddenByApp || _disposed)
                return;

            // Win11 24H2/25H2 Explorer XAML taskbar can transiently re-show on notification badge/red-dot updates.
            foreach (var hwnd in FindTaskbarWindows())
            {
                if (NativeMethods.IsWindowVisible(hwnd))
                    HideTaskbarWindow(hwnd);
            }
        }
    }

    private static int QueryAppBarState()
    {
        try
        {
            var main = NativeMethods.FindWindow("Shell_TrayWnd", null);
            var abd = new NativeMethods.APPBARDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = main
            };
            return (int)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd);
        }
        catch
        {
            return 0;
        }
    }

    private static void SetAppBarAutoHide(int baseState, bool enableAutoHide)
    {
        try
        {
            var main = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (main == 0)
                return;

            var targetState = enableAutoHide
                ? (baseState | NativeMethods.ABS_AUTOHIDE)
                : (baseState & ~NativeMethods.ABS_AUTOHIDE);

            var abd = new NativeMethods.APPBARDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = main,
                lParam = targetState
            };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);
        }
        catch
        {
            // Best-effort AppBar state synchronization
        }
    }

    private static void RestoreAppBarState(int? originalState)
    {
        try
        {
            var main = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (main == 0)
                return;

            var targetState = originalState ?? 0;
            var abd = new NativeMethods.APPBARDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = main,
                lParam = targetState
            };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);
        }
        catch
        {
            // Best-effort restore
        }
    }

    private static void HideAllTaskbars()
    {
        foreach (var hwnd in FindTaskbarWindows())
            HideTaskbarWindow(hwnd);
    }

    private static void HideTaskbarWindow(nint hwnd)
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0, HideSwpFlags);
    }

    private static void ShowAllTaskbars()
    {
        foreach (var hwnd in FindTaskbarWindows())
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0, ShowSwpFlags);
        }
    }

    private static List<nint> FindTaskbarWindows()
    {
        var list = new List<nint>();
        var main = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (main != 0)
            list.Add(main);

        nint secondary = 0;
        while ((secondary = NativeMethods.FindWindowEx(0, secondary, "Shell_SecondaryTrayWnd", null)) != 0)
            list.Add(secondary);

        // Legacy / fallback Start button window if top-level
        var startBtn = NativeMethods.FindWindow("Button", "Start");
        if (startBtn != 0 && !list.Contains(startBtn))
            list.Add(startBtn);

        return list;
    }

    private void WriteSentinel(int originalAppBarState)
    {
        try
        {
            var dir = Path.GetDirectoryName(_sentinelFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_sentinelFilePath, $"{DateTime.UtcNow:O}|{originalAppBarState}");
        }
        catch
        {
            // Best-effort crash sentinel
        }
    }

    private int? ReadSentinelOriginalState()
    {
        try
        {
            if (!File.Exists(_sentinelFilePath))
                return null;

            var content = File.ReadAllText(_sentinelFilePath).Trim();
            var parts = content.Split('|');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var state))
                return state;
        }
        catch
        {
            // Ignore malformed sentinel; fallback to standard non-autohide state (0)
        }

        return 0;
    }

    private void TryDeleteSentinel()
    {
        try
        {
            if (File.Exists(_sentinelFilePath))
                File.Delete(_sentinelFilePath);
        }
        catch
        {
            // Ignore
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopEnforceTimerLocked();
            _enforceHiddenTimer?.Dispose();
            _enforceHiddenTimer = null;
        }

        RestoreIfHidden();
    }
}
