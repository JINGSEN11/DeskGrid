using System.IO;
using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public sealed class TaskbarService : ITaskbarService
{
    private readonly string _sentinelFilePath;
    private readonly object _lock = new();
    private bool _isHiddenByApp;

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
                ShowAllTaskbars();
                TryDeleteSentinel();
                _isHiddenByApp = false;
            }
        }
    }

    public void SetTaskbarHidden(bool hidden)
    {
        lock (_lock)
        {
            if (hidden)
            {
                WriteSentinel();
                HideAllTaskbars();
                _isHiddenByApp = true;
            }
            else
            {
                ShowAllTaskbars();
                TryDeleteSentinel();
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
                ShowAllTaskbars();
                TryDeleteSentinel();
                _isHiddenByApp = false;
            }
        }
    }

    private static void HideAllTaskbars()
    {
        foreach (var hwnd in FindTaskbarWindows())
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    }

    private static void ShowAllTaskbars()
    {
        foreach (var hwnd in FindTaskbarWindows())
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
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

        return list;
    }

    private void WriteSentinel()
    {
        try
        {
            var dir = Path.GetDirectoryName(_sentinelFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_sentinelFilePath, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Best-effort crash sentinel
        }
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
        RestoreIfHidden();
    }
}
