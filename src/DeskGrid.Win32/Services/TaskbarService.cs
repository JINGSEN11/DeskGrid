using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

/// <summary>
/// Hides and restores Windows 10/11 (including Win11 24H2/25H2 Build 26200) native taskbars safely.
/// Uses AppBar ABS_AUTOHIDE + ShowWindow/SetWindowPos SWP_HIDEWINDOW + lightweight visibility guard,
/// and persists original AppBar state in a crash sentinel (taskbar_hidden.flag) so normal safe-exit
/// or restart after an unexpected termination restores the original taskbar state cleanly.
/// </summary>
public sealed class TaskbarService : ITaskbarService
{
    private const string PrimarySentinelFileName = "taskbar_hidden.flag";
    private const string LegacySentinelFileName = "taskbar.hidden.flag";

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
        NativeMethods.SWP_FRAMECHANGED |
        NativeMethods.SWP_SHOWWINDOW;

    private readonly string _sentinelFilePath;
    private readonly string _legacySentinelFilePath;
    private readonly object _lock = new();
    private Timer? _enforceHiddenTimer;
    private int? _originalAppBarState;
    private bool _isHiddenByApp;
    private bool _disposed;

    public bool IsHiddenByApp => _isHiddenByApp;

    public TaskbarService(string configDirectory)
    {
        _sentinelFilePath = Path.Combine(configDirectory, PrimarySentinelFileName);
        _legacySentinelFilePath = Path.Combine(configDirectory, LegacySentinelFileName);
    }

    public void RecoverFromCrashIfNeeded()
    {
        lock (_lock)
        {
            if (HasAnySentinelLocked())
            {
                var savedState = ReadSentinelOriginalState(_sentinelFilePath, _legacySentinelFilePath);
                StopEnforceTimerLocked();
                RestoreAppBarState(savedState);
                ShowAllTaskbars();
                BroadcastWorkAreaRefresh();
                TryDeleteAllSentinelsLocked();
                _originalAppBarState = null;
                _isHiddenByApp = false;
            }
        }
    }

    public void SetTaskbarHidden(bool hidden)
    {
        if (hidden)
        {
            HideTaskbar();
            return;
        }

        lock (_lock)
        {
            if (_disposed)
                return;

            // No-op when DeskGrid has not hidden the taskbar (avoids expensive HWND_BROADCAST on unrelated setting changes)
            if (!_isHiddenByApp && !HasAnySentinelLocked())
                return;

            RestoreTaskbarLocked(forceAppBarRestore: true, asyncBroadcast: true);
        }
    }

    public void HideTaskbar()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

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
    }

    public void RestoreTaskbar()
    {
        lock (_lock)
        {
            RestoreTaskbarLocked(forceAppBarRestore: true, asyncBroadcast: true);
        }
    }

    public void RestoreIfHidden()
    {
        lock (_lock)
        {
            if (_isHiddenByApp || HasAnySentinelLocked())
            {
                RestoreTaskbarLocked(forceAppBarRestore: true, asyncBroadcast: false);
            }
        }
    }

    private void RestoreTaskbarLocked(bool forceAppBarRestore, bool asyncBroadcast)
    {
        StopEnforceTimerLocked();

        var restoreState = _originalAppBarState ?? ReadSentinelOriginalState(_sentinelFilePath, _legacySentinelFilePath);
        if (forceAppBarRestore)
            RestoreAppBarState(restoreState);

        ShowAllTaskbars();
        if (asyncBroadcast)
            _ = Task.Run(BroadcastWorkAreaRefresh);
        else
            BroadcastWorkAreaRefresh();

        TryDeleteAllSentinelsLocked();
        _originalAppBarState = null;
        _isHiddenByApp = false;
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

    private static void BroadcastWorkAreaRefresh()
    {
        try
        {
            // Asynchronous non-blocking notification tells Explorer's taskbar/shell immediately to refresh TraySettings
            NativeMethods.SendNotifyMessage(
                NativeMethods.HWND_BROADCAST,
                NativeMethods.WM_SETTINGCHANGE,
                0,
                "TraySettings");

            // Non-blocking broadcast (SMTO_ABORTIFHUNG) notifies desktop shell and top-level apps
            // to refresh their working area without ever hanging on unresponsive windows.
            NativeMethods.SendMessageTimeout(
                NativeMethods.HWND_BROADCAST,
                NativeMethods.WM_SETTINGCHANGE,
                (nint)NativeMethods.SPI_SETWORKAREA,
                "TraySettings",
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                250,
                out _);

            NativeMethods.SendMessageTimeout(
                NativeMethods.HWND_BROADCAST,
                NativeMethods.WM_SETTINGCHANGE,
                (nint)NativeMethods.SPI_SETWORKAREA,
                null,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                250,
                out _);
        }
        catch
        {
            // Best-effort broadcast
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

    private bool HasAnySentinelLocked() =>
        File.Exists(_sentinelFilePath) || File.Exists(_legacySentinelFilePath);

    private void WriteSentinel(int originalAppBarState)
    {
        try
        {
            var dir = Path.GetDirectoryName(_sentinelFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var payload = $"{DateTime.UtcNow:O}|{originalAppBarState.ToString(CultureInfo.InvariantCulture)}";
            var bytes = Encoding.UTF8.GetBytes(payload);
            using var fs = new FileStream(
                _sentinelFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            // Best-effort crash sentinel
        }
    }

    private static int? ReadSentinelOriginalState(string primaryPath, string legacyPath)
    {
        foreach (var path in new[] { primaryPath, legacyPath })
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var content = File.ReadAllText(path).Trim();
                var parts = content.Split('|');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var state))
                {
                    return state;
                }
            }
            catch
            {
                // Ignore malformed sentinel; fallback to standard non-autohide state (0)
            }
        }

        return 0;
    }

    private void TryDeleteAllSentinelsLocked()
    {
        TryDeleteFile(_sentinelFilePath);
        TryDeleteFile(_legacySentinelFilePath);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
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
