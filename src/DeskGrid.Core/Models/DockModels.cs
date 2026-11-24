namespace DeskGrid.Core.Models;

public sealed class RunningWindowInfo
{
    public nint Handle { get; init; }
    public int ProcessId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string AppKey { get; init; } = string.Empty;
    public bool IsMinimized { get; init; }
    public bool IsActive { get; init; }
}

public sealed class DockSlotItem
{
    public string Id { get; init; } = string.Empty;
    public string AppKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string? Arguments { get; init; }
    public BoxItemKind Kind { get; init; } = BoxItemKind.Application;
    public bool IsPinned { get; init; }
    public bool IsActive { get; init; }
    public IReadOnlyList<RunningWindowInfo> RunningWindows { get; init; } = [];
    public bool IsRunning => RunningWindows.Count > 0;
    public int RunningCount => RunningWindows.Count;
    public nint PrimaryWindowHandle => RunningWindows.Count > 0 ? RunningWindows[0].Handle : 0;
}

public sealed record DockSnapshot(
    IReadOnlyList<DockSlotItem> PinnedSlots,
    IReadOnlyList<DockSlotItem> RunningOnlySlots);

public enum DockClickActionKind
{
    Launch,
    ActivateWindow,
    MinimizeWindow,
    ShowWindowListPopup
}

public sealed record DockClickDecision(
    DockClickActionKind Action,
    nint WindowHandle = 0);
