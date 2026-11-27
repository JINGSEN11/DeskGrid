using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using DeskGrid.Core.Abstractions;
using DeskGrid.Core.Models;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public sealed class WindowTrackerService : IWindowTracker
{
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly List<nint> _hooks = [];
    private readonly ConcurrentDictionary<uint, string> _processPathCache = new();
    private readonly ConcurrentDictionary<nint, byte> _excludedHandles = new();
    private readonly object _lock = new();
    private NativeMethods.WinEventDelegate? _winEventDelegate;
    private Timer? _debounceTimer;
    private IReadOnlyList<RunningWindowInfo> _snapshot = [];
    private int _isRefreshing;
    private int _pendingRefreshRequested;
    private bool _disposed;

    public event EventHandler? WindowsChanged;

    public IReadOnlyList<RunningWindowInfo> GetRunningWindows()
    {
        lock (_lock)
            return _snapshot;
    }

    public void RegisterExcludedWindow(nint hwnd)
    {
        if (hwnd != 0)
            _excludedHandles[hwnd] = 1;
    }

    public void UnregisterExcludedWindow(nint hwnd)
    {
        if (hwnd != 0)
            _excludedHandles.TryRemove(hwnd, out _);
    }

    public void Start()
    {
        if (_hooks.Count > 0) return;

        _debounceTimer = new Timer(_ => RefreshAndNotify(), null, Timeout.Infinite, Timeout.Infinite);
        _winEventDelegate = OnWinEvent;

        RegisterHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        RegisterHook(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
        RegisterHook(NativeMethods.EVENT_OBJECT_CREATE, NativeMethods.EVENT_OBJECT_HIDE);
        RegisterHook(NativeMethods.EVENT_OBJECT_NAMECHANGE, NativeMethods.EVENT_OBJECT_NAMECHANGE);

        // Kick off initial enumeration on a background thread instead of blocking UI startup
        _debounceTimer.Change(0, Timeout.Infinite);
    }

    public void Stop()
    {
        foreach (var h in _hooks)
        {
            if (h != 0)
                NativeMethods.UnhookWinEvent(h);
        }
        _hooks.Clear();
        _winEventDelegate = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    public void ActivateWindow(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return;

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        NativeMethods.SetForegroundWindow(hwnd);
        ScheduleRefresh();
    }

    public void MinimizeWindow(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
        ScheduleRefresh();
    }

    public void CloseWindow(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return;

        NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, 0, 0);
        ScheduleRefresh();
    }

    private void RegisterHook(uint minEvent, uint maxEvent)
    {
        if (_winEventDelegate is null) return;
        var h = NativeMethods.SetWinEventHook(
            minEvent,
            maxEvent,
            0,
            _winEventDelegate,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (h != 0)
            _hooks.Add(h);
    }

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || hwnd == 0 || idObject != 0)
            return;

        // Immediately discard any event targeting our own process or explicitly registered Dock/Box windows
        if (_excludedHandles.ContainsKey(hwnd))
            return;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _ownProcessId)
            return;

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (_disposed) return;
        try
        {
            _debounceTimer?.Change(90, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Ignore races during shutdown
        }
    }

    private void RefreshAndNotify()
    {
        if (_disposed) return;

        if (Interlocked.Exchange(ref _isRefreshing, 1) != 0)
        {
            Interlocked.Exchange(ref _pendingRefreshRequested, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _pendingRefreshRequested, 0);
                if (_disposed)
                    return;

                var foreground = NativeMethods.GetForegroundWindow();
                var windows = new List<RunningWindowInfo>();
                var activePids = new HashSet<uint>();
                var excludedSnapshot = _excludedHandles.IsEmpty
                    ? null
                    : new HashSet<nint>(_excludedHandles.Keys);

                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    if (!IsTrackableAppWindow(hwnd, excludedSnapshot, out var pid, out var title))
                        return true;

                    activePids.Add(pid);
                    var exePath = ResolveProcessExecutablePath(pid);
                    var procName = !string.IsNullOrEmpty(exePath)
                        ? Path.GetFileNameWithoutExtension(exePath)
                        : title;

                    var appKey = DockManager.NormalizeAppKey(
                        string.IsNullOrEmpty(exePath) ? title : exePath);

                    // Separate UWP apps hosted inside ApplicationFrameHost by their window title
                    if (appKey.Equals("applicationframehost", StringComparison.OrdinalIgnoreCase))
                    {
                        appKey = "uwp:" + title.ToLowerInvariant();
                        procName = title;
                    }

                    windows.Add(new RunningWindowInfo
                    {
                        Handle = hwnd,
                        ProcessId = (int)pid,
                        Title = title,
                        ProcessName = procName,
                        ExecutablePath = exePath,
                        AppKey = appKey,
                        IsMinimized = NativeMethods.IsIconic(hwnd),
                        IsActive = hwnd == foreground
                    });

                    return true;
                }, 0);

                // Trim dead PIDs from cache
                if (_processPathCache.Count > 128)
                {
                    foreach (var key in _processPathCache.Keys)
                    {
                        if (!activePids.Contains(key))
                            _processPathCache.TryRemove(key, out _);
                    }
                }

                bool changed;
                lock (_lock)
                {
                    changed = !SnapshotsEqual(_snapshot, windows);
                    if (changed)
                        _snapshot = windows;
                }

                if (changed && !_disposed)
                    WindowsChanged?.Invoke(this, EventArgs.Empty);
            }
            while (!_disposed && Interlocked.Exchange(ref _pendingRefreshRequested, 0) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    private static bool SnapshotsEqual(IReadOnlyList<RunningWindowInfo> prev, IReadOnlyList<RunningWindowInfo> next)
    {
        if (prev.Count != next.Count)
            return false;

        for (var i = 0; i < prev.Count; i++)
        {
            var a = prev[i];
            var b = next[i];
            if (a.Handle != b.Handle ||
                a.ProcessId != b.ProcessId ||
                a.IsActive != b.IsActive ||
                a.IsMinimized != b.IsMinimized ||
                !string.Equals(a.AppKey, b.AppKey, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.Title, b.Title, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsTrackableAppWindow(
        nint hwnd,
        IReadOnlySet<nint>? excludedHandles,
        out uint pid,
        out string title)
    {
        pid = 0;
        title = string.Empty;

        if (hwnd == 0 || (excludedHandles is not null && excludedHandles.Contains(hwnd)))
            return false;

        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        uint rootPid = 0;
        if (root != 0 && root != hwnd)
        {
            if (excludedHandles is not null && excludedHandles.Contains(root))
                return false;
            NativeMethods.GetWindowThreadProcessId(root, out rootPid);
        }

        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        uint ownerPid = 0;
        if (owner != 0)
        {
            if (excludedHandles is not null && excludedHandles.Contains(owner))
                return false;
            NativeMethods.GetWindowThreadProcessId(owner, out ownerPid);
        }

        // Strict exclusion: 100% filters out current process (primary DockWindow, all secondary DockWindows, boxes, popups)
        if (WindowExclusionFilter.ShouldExcludeWindow(hwnd, pid, _ownProcessId, excludedHandles, rootPid, ownerPid))
            return false;

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;

        if (owner != 0 && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            return false;

        // Ignore cloaked windows (other virtual desktops / suspended UWP frames)
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        title = NativeMethods.GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var cls = NativeMethods.GetWindowClassName(hwnd);
        if (cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private string ResolveProcessExecutablePath(uint pid)
    {
        if (_processPathCache.TryGetValue(pid, out var cached))
            return cached;

        var path = string.Empty;
        var hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess != 0)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (NativeMethods.QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    path = sb.ToString(0, (int)size);
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }
        }
        else
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                path = proc.ProcessName;
            }
            catch
            {
                // Access denied or exited
            }
        }

        _processPathCache[pid] = path;
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
