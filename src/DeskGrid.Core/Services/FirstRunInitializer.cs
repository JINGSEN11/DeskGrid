using DeskGrid.Core.Models;

namespace DeskGrid.Core.Services;

public static class FirstRunInitializer
{
    public static AppConfig CreateDefaultConfig()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        var config = new AppConfig
        {
            Appearance = new AppearanceSettings
            {
                Theme = ThemePreference.System,
                Backdrop = BackdropPreference.Acrylic,
                DefaultBoxOpacity = 0.35,
                DefaultIconSize = BoxIconSize.Medium
            },
            Behavior = new BehaviorSettings
            {
                LaunchAtStartup = false,
                DoubleClickDesktopToToggleBoxes = true,
                EnableGridSnap = true,
                GridSnapStep = 12,
                BoxesVisible = true
            },
            Dock = new DockConfig
            {
                Enabled = true,
                EnableMagnification = true,
                MagnificationScale = 1.42,
                MagnificationRadius = 130,
                BaseIconSize = 44,
                AutoHideOnFullscreen = true,
                AutoHideSystemTaskbar = false,
                PinnedItems = BuildDefaultPinnedItems()
            },
            Boxes =
            [
                new BoxConfig
                {
                    Id = "box-desktop-portal",
                    Title = "桌面文件",
                    Mode = BoxMode.FolderPortal,
                    FolderPath = desktopPath,
                    Left = 36,
                    Top = 36,
                    Width = 340,
                    Height = 260,
                    AutoFitHeight = false,
                    IsCollapsed = false,
                    IsLocked = false,
                    IconSize = BoxIconSize.Medium,
                    Opacity = 0.35,
                    SortMode = BoxSortMode.NameAsc
                },
                new BoxConfig
                {
                    Id = "box-quick-virtual",
                    Title = "常用快捷",
                    Mode = BoxMode.Virtual,
                    Left = 36,
                    Top = 316,
                    Width = 340,
                    Height = 260,
                    AutoFitHeight = false,
                    IsCollapsed = false,
                    IsLocked = false,
                    IconSize = BoxIconSize.Medium,
                    Opacity = 0.35,
                    SortMode = BoxSortMode.Manual,
                    VirtualItems = []
                }
            ]
        };

        return config;
    }

    private static List<DockPinnedItem> BuildDefaultPinnedItems()
    {
        var items = new List<DockPinnedItem>();

        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (File.Exists(explorer))
        {
            items.Add(new DockPinnedItem
            {
                DisplayName = "文件资源管理器",
                TargetPath = explorer,
                AppKey = DockManager.NormalizeAppKey(explorer),
                Kind = BoxItemKind.Application
            });
        }

        var edgePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        var edge = edgePaths.FirstOrDefault(File.Exists);
        if (edge is not null)
        {
            items.Add(new DockPinnedItem
            {
                DisplayName = "Microsoft Edge",
                TargetPath = edge,
                AppKey = DockManager.NormalizeAppKey(edge),
                Kind = BoxItemKind.Application
            });
        }

        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (File.Exists(notepad))
        {
            items.Add(new DockPinnedItem
            {
                DisplayName = "记事本",
                TargetPath = notepad,
                AppKey = DockManager.NormalizeAppKey(notepad),
                Kind = BoxItemKind.Application
            });
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            items.Add(new DockPinnedItem
            {
                DisplayName = "下载",
                TargetPath = downloads,
                AppKey = DockManager.NormalizeAppKey(downloads),
                Kind = BoxItemKind.Folder
            });
        }

        return items;
    }
}
