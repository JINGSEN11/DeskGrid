namespace DeskGrid.Core.Models;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public enum BackdropPreference
{
    Acrylic,
    Mica
}

public enum BoxMode
{
    Virtual,
    FolderPortal
}

public enum BoxIconSize
{
    Small,
    Medium,
    Large
}

public enum BoxSortMode
{
    Manual,
    NameAsc,
    TypeAsc,
    DateModifiedDesc
}

public enum BoxItemKind
{
    File,
    Folder,
    Application,
    Url
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 2;
    public bool ShowDock { get; set; } = true;
    public AppearanceSettings Appearance { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public DockConfig Dock { get; set; } = new();
    public List<BoxConfig> Boxes { get; set; } = [];
}

public sealed class AppearanceSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public BackdropPreference Backdrop { get; set; } = BackdropPreference.Acrylic;
    public double DefaultBoxOpacity { get; set; } = 0.35;
    public BoxIconSize DefaultIconSize { get; set; } = BoxIconSize.Medium;
}

public sealed class BehaviorSettings
{
    public bool LaunchAtStartup { get; set; } = false;
    public bool DoubleClickDesktopToToggleBoxes { get; set; } = true;
    public bool EnableGridSnap { get; set; } = true;
    public int GridSnapStep { get; set; } = 12;
    public bool BoxesVisible { get; set; } = true;
}

public sealed class DockConfig
{
    public bool Enabled { get; set; } = true;
    public bool EnableMagnification { get; set; } = true;
    public double MagnificationScale { get; set; } = 1.42;
    public double MagnificationRadius { get; set; } = 130.0;
    public double BaseIconSize { get; set; } = 44.0;
    public bool AutoHideOnFullscreen { get; set; } = true;
    public bool AutoHideSystemTaskbar { get; set; } = false;
    public bool ShowDockClock { get; set; } = true;
    public bool ClockUse24Hour { get; set; } = true;
    public bool ClockShowSeconds { get; set; } = false;
    public bool ClockShowDate { get; set; } = true;
    public List<DockPinnedItem> PinnedItems { get; set; } = [];
}

public sealed class DockPinnedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string? Arguments { get; set; }
    public string? AppKey { get; set; }
    public BoxItemKind Kind { get; set; } = BoxItemKind.Application;
}

public sealed class BoxConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新盒子";
    public BoxMode Mode { get; set; } = BoxMode.Virtual;
    public string? FolderPath { get; set; }
    public double Left { get; set; } = 36;
    public double Top { get; set; } = 36;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 260;
    public bool AutoFitHeight { get; set; } = false;
    public bool IsCollapsed { get; set; } = false;
    public bool IsLocked { get; set; } = false;
    public BoxIconSize IconSize { get; set; } = BoxIconSize.Medium;
    public double Opacity { get; set; } = 0.35;
    public BoxSortMode SortMode { get; set; } = BoxSortMode.NameAsc;
    public List<VirtualBoxItemConfig> VirtualItems { get; set; } = [];
    public List<string> CustomItemOrder { get; set; } = [];
}

public sealed class VirtualBoxItemConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public BoxItemKind Kind { get; set; } = BoxItemKind.File;
}

public sealed class BoxItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public BoxItemKind Kind { get; set; } = BoxItemKind.File;
    public bool IsMissing { get; set; }
    public DateTime DateModified { get; set; }
    public string Extension { get; set; } = string.Empty;
}
