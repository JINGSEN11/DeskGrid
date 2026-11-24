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
    private readonly object _lock = new();
    private NativeMethods.WinEventDelegate? _winEventDelegate;
    private Timer? _debounceTimer;
    private IReadOnlyList<RunningWindowInfo> _snapshot = [];
    private bool _disposed;

    public event EventHandler? WindowsChanged;

    public IReadOnlyList<RunningWindowInfo> GetRunningWindows()
    {
        lock (_lock)
            return _snapshot;
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

        RefreshAndNotify();
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

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (_disposed) return;
        _debounceTimer?.Change(80, Timeout.Infinite);
    }

    private void RefreshAndNotify()
    {
        if (_disposed) return;

        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new List<RunningWindowInfo>();
        var activePids = new HashSet<uint>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsTrackableAppWindow(hwnd, out var pid, out var title))
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

        lock (_lock)
            _snapshot = windows;

        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsTrackableAppWindow(nint hwnd, out uint pid, out string title)
    {
        pid = 0;
        title = string.Empty;

        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0 || pid == _ownProcessId)
            return false;

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;

        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
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
