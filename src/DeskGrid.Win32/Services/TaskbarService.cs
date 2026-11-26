using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeskGrid.Core.Abstractions;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

/// <summary>
/// Hides and restores Windows 10/11 (including Win11 24H2/25H2 Build 26200) native taskbars safely.
/// Uses AppBar ABS_AUTOHIDE + ShowWindow/SetWindowPos SWP_HIDEWINDOW + lightweight visibility guard,
/// durable sentinel file (taskbar_hidden.flag) for crash/power-loss recovery, and an out-of-process
/// parent-handle watchdog (DeskGrid.exe --watchdog &lt;pid&gt;) to restore taskbars even after TerminateProcess.
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
        NativeMethods.SWP_FRAMECHANGED |
        NativeMethods.SWP_SHOWWINDOW;

    private readonly string _configDirectory;
    private readonly string _sentinelFilePath;
    private readonly string _legacySentinelFilePath;
    private readonly object _lock = new();
    private Timer? _enforceHiddenTimer;
    private Process? _watchdogProcess;
    private EventWaitHandle? _watchdogCancelEvent;
    private int? _originalAppBarState;
    private bool _isHiddenByApp;
    private bool _disposed;

    public bool IsHiddenByApp => _isHiddenByApp;

    public TaskbarService(string configDirectory)
    {
        _configDirectory = configDirectory;
        _sentinelFilePath = Path.Combine(configDirectory, TaskbarWatchdogProtocol.PrimarySentinelFileName);
        _legacySentinelFilePath = Path.Combine(configDirectory, TaskbarWatchdogProtocol.LegacySentinelFileName);
    }

    public static bool HasSentinelFile(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
            return false;

        var primary = Path.Combine(configDirectory, TaskbarWatchdogProtocol.PrimarySentinelFileName);
        var legacy = Path.Combine(configDirectory, TaskbarWatchdogProtocol.LegacySentinelFileName);
        return File.Exists(primary) || File.Exists(legacy);
    }

    /// <summary>
    /// Static restoration entry point used by the out-of-process watchdog after TerminateProcess / crash.
    /// Restores AppBar state, shows primary and all secondary monitor taskbars, broadcasts WM_SETTINGCHANGE,
    /// and removes persisted sentinel files.
    /// </summary>
    public static void RestoreTaskbar(string configDirectory)
    {
        var primary = Path.Combine(configDirectory, TaskbarWatchdogProtocol.PrimarySentinelFileName);
        var legacy = Path.Combine(configDirectory, TaskbarWatchdogProtocol.LegacySentinelFileName);
        var savedState = ReadSentinelOriginalState(primary, legacy);

        RestoreAppBarState(savedState);
        ShowAllTaskbars();
        BroadcastWorkAreaRefresh();

        // Win11 Explorer may finish re-evaluating AppBar auto-hide state asynchronously; verify visibility once more.
        var main = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (main != 0 && !NativeMethods.IsWindowVisible(main))
        {
            Thread.Sleep(120);
            RestoreAppBarState(savedState);
            ShowAllTaskbars();
            BroadcastWorkAreaRefresh();
        }

        TryDeleteFile(primary);
        TryDeleteFile(legacy);
    }

    public void RecoverFromCrashIfNeeded()
    {
        lock (_lock)
        {
            if (HasAnySentinelLocked())
            {
                var savedState = ReadSentinelOriginalState(_sentinelFilePath, _legacySentinelFilePath);
                StopEnforceTimerLocked();
                StopWatchdogLocked();
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
                EnsureWatchdogRunningLocked();
            }
            else
            {
                RestoreTaskbarLocked(forceAppBarRestore: _isHiddenByApp || HasAnySentinelLocked());
            }
        }
    }

    public void RestoreTaskbar()
    {
        lock (_lock)
        {
            RestoreTaskbarLocked(forceAppBarRestore: true);
        }
    }

    public void RestoreIfHidden()
    {
        lock (_lock)
        {
            if (_isHiddenByApp || HasAnySentinelLocked())
            {
                RestoreTaskbarLocked(forceAppBarRestore: true);
            }
            else
            {
                StopWatchdogLocked();
            }
        }
    }

    private void RestoreTaskbarLocked(bool forceAppBarRestore)
    {
        StopEnforceTimerLocked();
        StopWatchdogLocked();

        var restoreState = _originalAppBarState ?? ReadSentinelOriginalState(_sentinelFilePath, _legacySentinelFilePath);
        if (forceAppBarRestore)
            RestoreAppBarState(restoreState);

        ShowAllTaskbars();
        BroadcastWorkAreaRefresh();
        TryDeleteAllSentinelsLocked();
        _originalAppBarState = null;
        _isHiddenByApp = false;
    }

    private void EnsureWatchdogRunningLocked()
    {
        try
        {
            if (_watchdogProcess is not null)
            {
                if (!_watchdogProcess.HasExited)
                    return;

                _watchdogProcess.Dispose();
                _watchdogProcess = null;
            }
        }
        catch
        {
            _watchdogProcess?.Dispose();
            _watchdogProcess = null;
        }

        _watchdogCancelEvent?.Dispose();
        _watchdogCancelEvent = null;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            // Avoid launching watchdog if hosted inside an external runner such as dotnet.exe
            var exeName = Path.GetFileNameWithoutExtension(exePath);
            if (string.Equals(exeName, "dotnet", StringComparison.OrdinalIgnoreCase))
                return;

            var cancelEventName = $"Local\\DeskGrid_TaskbarWatchdog_Cancel_{Environment.ProcessId}_{Guid.NewGuid():N}";
            var cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset, cancelEventName);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            foreach (var arg in TaskbarWatchdogProtocol.BuildArgs(Environment.ProcessId, _configDirectory, cancelEventName))
                psi.ArgumentList.Add(arg);

            var proc = Process.Start(psi);
            if (proc is null)
            {
                cancelEvent.Dispose();
                return;
            }

            _watchdogCancelEvent = cancelEvent;
            _watchdogProcess = proc;
        }
        catch
        {
            // Best-effort watchdog startup; durable taskbar_hidden.flag still provides fallback recovery
            _watchdogCancelEvent?.Dispose();
            _watchdogCancelEvent = null;
            _watchdogProcess?.Dispose();
            _watchdogProcess = null;
        }
    }

    private void StopWatchdogLocked()
    {
        try
        {
            _watchdogCancelEvent?.Set();
        }
        catch
        {
            // Ignore
        }

        if (_watchdogProcess is not null)
        {
            try
            {
                if (!_watchdogProcess.HasExited)
                {
                    if (!_watchdogProcess.WaitForExit(250))
                        _watchdogProcess.Kill(entireProcessTree: false);
                }
            }
            catch
            {
                // Ignore if already exited
            }
            finally
            {
                _watchdogProcess.Dispose();
                _watchdogProcess = null;
            }
        }

        _watchdogCancelEvent?.Dispose();
        _watchdogCancelEvent = null;
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

            var payload = TaskbarWatchdogProtocol.FormatSentinel(DateTime.UtcNow, originalAppBarState, Environment.ProcessId);
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

                var content = File.ReadAllText(path);
                if (TaskbarWatchdogProtocol.TryParseSentinelOriginalState(content, out var state))
                    return state;
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
