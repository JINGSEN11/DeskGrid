using DeskGrid.Core.Models;

namespace DeskGrid.Core.Abstractions;

public interface IWindowTracker : IDisposable
{
    event EventHandler? WindowsChanged;
    IReadOnlyList<RunningWindowInfo> GetRunningWindows();
    void Start();
    void Stop();
    void ActivateWindow(nint hwnd);
    void MinimizeWindow(nint hwnd);
    void CloseWindow(nint hwnd);
}

public interface INativeDesktopIconService
{
    bool AreIconsVisible();
    bool SetIconsVisible(bool visible);
    bool ToggleIcons();
}

public interface ITaskbarService : IDisposable
{
    bool IsHiddenByApp { get; }
    void SetTaskbarHidden(bool hidden);
    void RestoreTaskbar();
    void RestoreIfHidden();
    void RecoverFromCrashIfNeeded();
}

public interface IFullscreenMonitor : IDisposable
{
    event EventHandler<bool>? FullscreenStateChanged;
    bool IsFullscreenActive { get; }
    void Start();
    void Stop();
}

public interface IStartupService
{
    bool IsEnabled();
    void SetEnabled(bool enabled, string executablePath);
}

public interface IFileOperationsService
{
    void OpenPath(string path, string? arguments = null, bool asAdmin = false);
    void RevealInExplorer(string path);
    bool MoveToRecycleBin(string path);
    string? ResolveShortcutTarget(string lnkPath);
    string RenameFileSystemEntry(string fullPath, string newName);
    string TransferIntoFolder(string sourcePath, string destinationFolder, bool copy);
}
