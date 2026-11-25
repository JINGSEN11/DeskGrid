using DeskGrid.Core.Models;

namespace DeskGrid.Core.Services;

public static class FirstRunInitializer
{
    public static AppConfig CreateDefaultConfig() => CreateDefaultConfig(null, null);

    public static AppConfig CreateDefaultConfig(double? workAreaWidth, double? workAreaHeight)
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath))
            desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        // Adaptive box sizing for 1080p / 2K / 4K (and HiDPI scaling): never exceeds screen bounds or overlaps Dock
        const double margin = 36.0;
        const double verticalGap = 20.0;
        const double dockReserve = 104.0;

        var safeWorkWidth = workAreaWidth is > 400 ? workAreaWidth.Value : 1920.0;
        var safeWorkHeight = workAreaHeight is > 300 ? workAreaHeight.Value : 1040.0;

        var boxWidth = Math.Clamp(340.0, 260.0, Math.Max(260.0, Math.Floor(safeWorkWidth - margin * 2)));
        var usableHeight = Math.Max(360.0, safeWorkHeight - dockReserve);
        var maxEachBoxHeight = Math.Floor((usableHeight - margin - verticalGap - 12.0) / 2.0);
        var boxHeight = Math.Clamp(260.0, 180.0, Math.Max(180.0, maxEachBoxHeight));

        var config = new AppConfig
        {
            ShowDock = true,
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
                    Left = margin,
                    Top = margin,
                    Width = boxWidth,
                    Height = boxHeight,
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
                    Left = margin,
                    Top = margin + boxHeight + verticalGap,
                    Width = boxWidth,
                    Height = boxHeight,
                    AutoFitHeight = false,
                    IsCollapsed = false,
                    IsLocked = false,
                    IconSize = BoxIconSize.Medium,
                    Opacity = 0.35,
                    SortMode = BoxSortMode.Manual,
                    VirtualItems = ScanDesktopShortcuts(desktopPath, maxCount: 8)
                }
            ]
        };

        return config;
    }

    private static List<VirtualBoxItemConfig> ScanDesktopShortcuts(string userDesktopPath, int maxCount)
    {
        var results = new List<VirtualBoxItemConfig>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidateDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(userDesktopPath) && Directory.Exists(userDesktopPath))
            candidateDirs.Add(userDesktopPath);

        try
        {
            var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrWhiteSpace(commonDesktop) &&
                Directory.Exists(commonDesktop) &&
                !string.Equals(commonDesktop, userDesktopPath, StringComparison.OrdinalIgnoreCase))
            {
                candidateDirs.Add(commonDesktop);
            }
        }
        catch
        {
            // Ignore inaccessible public desktop directory
        }

        foreach (var dir in candidateDirs)
        {
            if (results.Count >= maxCount)
                break;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= maxCount)
                    break;

                try
                {
                    var ext = Path.GetExtension(file);
                    var isLnk = string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase);
                    var isUrl = string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase);
                    if (!isLnk && !isUrl)
                        continue;

                    var attrs = File.GetAttributes(file);
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;

                    var displayName = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    if (displayName.Contains("DeskGrid", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!seenNames.Add(displayName))
                        continue;

                    results.Add(new VirtualBoxItemConfig
                    {
                        DisplayName = displayName,
                        TargetPath = file,
                        Kind = isUrl ? BoxItemKind.Url : BoxItemKind.Application
                    });
                }
                catch
                {
                    // Skip unreadable entries safely
                }
            }
        }

        return results;
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

        var browser = DetectInstalledBrowser();
        if (browser is not null)
        {
            items.Add(new DockPinnedItem
            {
                DisplayName = browser.Value.DisplayName,
                TargetPath = browser.Value.Path,
                AppKey = DockManager.NormalizeAppKey(browser.Value.Path),
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

    private static (string DisplayName, string Path)? DetectInstalledBrowser()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new (string Name, string Path)[]
        {
            ("Microsoft Edge", Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe")),
            ("Microsoft Edge", Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe")),
            ("Google Chrome", Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe")),
            ("Google Chrome", Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe")),
            ("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")),
            ("Mozilla Firefox", Path.Combine(pf, "Mozilla Firefox", "firefox.exe")),
            ("Mozilla Firefox", Path.Combine(pf86, "Mozilla Firefox", "firefox.exe"))
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path) && File.Exists(candidate.Path))
                return candidate;
        }

        return null;
    }
}
