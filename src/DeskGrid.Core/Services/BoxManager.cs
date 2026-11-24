using System.Collections.Concurrent;
using DeskGrid.Core.Models;

namespace DeskGrid.Core.Services;

public sealed class BoxManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly ConcurrentDictionary<string, FolderWatcherEntry> _watchers = new();
    private bool _disposed;

    public event EventHandler<string>? BoxItemsChanged;
    public event EventHandler<BoxConfig>? BoxAdded;
    public event EventHandler<string>? BoxRemoved;
    public event EventHandler? ConfigChanged;

    public IReadOnlyList<BoxConfig> Boxes => _config.Boxes;

    public BoxManager(AppConfig config, ConfigService configService)
    {
        _config = config;
        _configService = configService;

        foreach (var box in _config.Boxes)
            EnsureWatcher(box);
    }

    public BoxConfig? GetBox(string boxId) =>
        _config.Boxes.FirstOrDefault(b => string.Equals(b.Id, boxId, StringComparison.Ordinal));

    public BoxConfig CreateBox(string title, BoxMode mode, string? folderPath = null, double? left = null, double? top = null)
    {
        var offset = (_config.Boxes.Count % 6) * 28;
        var box = new BoxConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? (mode == BoxMode.FolderPortal ? "目录盒子" : "新盒子") : title.Trim(),
            Mode = mode,
            FolderPath = mode == BoxMode.FolderPortal ? folderPath : null,
            Left = left ?? (36 + offset),
            Top = top ?? (36 + offset),
            Width = 340,
            Height = 260,
            AutoFitHeight = false,
            IconSize = _config.Appearance.DefaultIconSize,
            Opacity = _config.Appearance.DefaultBoxOpacity,
            SortMode = mode == BoxMode.FolderPortal ? BoxSortMode.NameAsc : BoxSortMode.Manual
        };

        _config.Boxes.Add(box);
        EnsureWatcher(box);
        SaveAndNotify();
        BoxAdded?.Invoke(this, box);
        return box;
    }

    public bool RemoveBox(string boxId)
    {
        var box = GetBox(boxId);
        if (box is null) return false;

        DisposeWatcher(boxId);
        _config.Boxes.Remove(box);
        SaveAndNotify();
        BoxRemoved?.Invoke(this, boxId);
        return true;
    }

    public void UpdateBox(BoxConfig box)
    {
        EnsureWatcher(box);
        SaveAndNotify();
    }

    public IReadOnlyList<BoxItem> GetItems(string boxId)
    {
        var box = GetBox(boxId);
        if (box is null) return [];

        List<BoxItem> items = box.Mode == BoxMode.FolderPortal
            ? ReadFolderPortalItems(box)
            : ReadVirtualItems(box);

        return SortItems(
            items,
            box.SortMode,
            box.Mode == BoxMode.FolderPortal ? box.CustomItemOrder : null);
    }

    public bool ReorderItem(string boxId, int oldIndex, int newIndex)
    {
        var box = GetBox(boxId);
        if (box is null)
            return false;

        var currentItems = GetItems(boxId);
        if (oldIndex < 0 || oldIndex >= currentItems.Count ||
            newIndex < 0 || newIndex >= currentItems.Count ||
            oldIndex == newIndex)
        {
            return false;
        }

        if (box.Mode == BoxMode.Virtual)
        {
            var byId = new Dictionary<string, VirtualBoxItemConfig>(box.VirtualItems.Count, StringComparer.Ordinal);
            foreach (var v in box.VirtualItems)
                byId.TryAdd(v.Id, v);

            var ordered = new List<VirtualBoxItemConfig>(box.VirtualItems.Count);
            var addedIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in currentItems)
            {
                if (byId.TryGetValue(item.Id, out var cfg) && addedIds.Add(cfg.Id))
                    ordered.Add(cfg);
            }

            foreach (var v in box.VirtualItems)
            {
                if (addedIds.Add(v.Id))
                    ordered.Add(v);
            }

            if (oldIndex >= ordered.Count || newIndex >= ordered.Count)
                return false;

            var moved = ordered[oldIndex];
            ordered.RemoveAt(oldIndex);
            ordered.Insert(newIndex, moved);

            box.VirtualItems = ordered;
            box.SortMode = BoxSortMode.Manual;
            SaveAndNotify();
            BoxItemsChanged?.Invoke(this, boxId);
            return true;
        }

        // FolderPortal mode: record filename order in CustomItemOrder without modifying real files on disk
        var orderedNames = new List<string>(currentItems.Count);
        foreach (var item in currentItems)
        {
            var key = GetFileNameKey(item.TargetPath);
            if (!string.IsNullOrWhiteSpace(key))
                orderedNames.Add(key);
        }

        if (oldIndex >= orderedNames.Count || newIndex >= orderedNames.Count)
            return false;

        var movedName = orderedNames[oldIndex];
        orderedNames.RemoveAt(oldIndex);
        orderedNames.Insert(newIndex, movedName);

        box.CustomItemOrder = orderedNames;
        box.SortMode = BoxSortMode.Manual;
        SaveAndNotify();
        BoxItemsChanged?.Invoke(this, boxId);
        return true;
    }

    public void UpdateFolderCustomOrderOnRename(string boxId, string oldTargetPath, string newFileName)
    {
        var box = GetBox(boxId);
        if (box is null || box.Mode != BoxMode.FolderPortal || box.CustomItemOrder.Count == 0)
            return;

        var oldName = GetFileNameKey(oldTargetPath);
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newFileName))
            return;

        var idx = box.CustomItemOrder.FindIndex(n => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            box.CustomItemOrder[idx] = newFileName.Trim();
            SaveAndNotify();
        }
    }

    public static string GetFileNameKey(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return string.Empty;

        var trimmed = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
    }

    public bool AddVirtualItem(string boxId, string targetPath, string? displayName = null)
    {
        var box = GetBox(boxId);
        if (box is null || box.Mode != BoxMode.Virtual || string.IsNullOrWhiteSpace(targetPath))
            return false;

        var normalized = targetPath.Trim();
        if (box.VirtualItems.Any(i => string.Equals(i.TargetPath, normalized, StringComparison.OrdinalIgnoreCase)))
            return false;

        var kind = DetectItemKind(normalized);
        var name = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : DeriveDisplayName(normalized, kind);

        box.VirtualItems.Add(new VirtualBoxItemConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = name,
            TargetPath = normalized,
            Kind = kind
        });

        SaveAndNotify();
        BoxItemsChanged?.Invoke(this, boxId);
        return true;
    }

    public bool RemoveVirtualItem(string boxId, string itemId)
    {
        var box = GetBox(boxId);
        if (box is null || box.Mode != BoxMode.Virtual)
            return false;

        var removed = box.VirtualItems.RemoveAll(i => string.Equals(i.Id, itemId, StringComparison.Ordinal)) > 0;
        if (removed)
        {
            SaveAndNotify();
            BoxItemsChanged?.Invoke(this, boxId);
        }

        return removed;
    }

    public bool RenameVirtualItem(string boxId, string itemId, string newName)
    {
        var box = GetBox(boxId);
        if (box is null || box.Mode != BoxMode.Virtual || string.IsNullOrWhiteSpace(newName))
            return false;

        var item = box.VirtualItems.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
        if (item is null) return false;

        item.DisplayName = newName.Trim();
        SaveAndNotify();
        BoxItemsChanged?.Invoke(this, boxId);
        return true;
    }

    public int RemoveMissingVirtualItems(string boxId)
    {
        var box = GetBox(boxId);
        if (box is null || box.Mode != BoxMode.Virtual)
            return 0;

        var count = box.VirtualItems.RemoveAll(i => !TargetExists(i.TargetPath, i.Kind));
        if (count > 0)
        {
            SaveAndNotify();
            BoxItemsChanged?.Invoke(this, boxId);
        }

        return count;
    }

    public static BoxItemKind DetectItemKind(string targetPath)
    {
        if (Uri.TryCreate(targetPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            return BoxItemKind.Url;
        }

        if (Directory.Exists(targetPath))
            return BoxItemKind.Folder;

        var ext = Path.GetExtension(targetPath);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return BoxItemKind.Application;
        }

        return BoxItemKind.File;
    }

    public static string DeriveDisplayName(string targetPath, BoxItemKind kind)
    {
        if (kind == BoxItemKind.Url)
            return targetPath;

        var trimmed = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name))
            return trimmed;

        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(name);
        }

        return name;
    }

    private static List<BoxItem> ReadVirtualItems(BoxConfig box)
    {
        var result = new List<BoxItem>(box.VirtualItems.Count);
        foreach (var item in box.VirtualItems)
        {
            var exists = TargetExists(item.TargetPath, item.Kind);
            DateTime modified = DateTime.MinValue;
            if (exists && item.Kind != BoxItemKind.Url)
            {
                try
                {
                    modified = Directory.Exists(item.TargetPath)
                        ? Directory.GetLastWriteTime(item.TargetPath)
                        : File.GetLastWriteTime(item.TargetPath);
                }
                catch
                {
                    // Ignore inaccessible timestamps
                }
            }

            result.Add(new BoxItem
            {
                Id = item.Id,
                DisplayName = item.DisplayName,
                TargetPath = item.TargetPath,
                Kind = item.Kind,
                IsMissing = !exists,
                DateModified = modified,
                Extension = item.Kind == BoxItemKind.Folder ? string.Empty : Path.GetExtension(item.TargetPath)
            });
        }

        return result;
    }

    private static List<BoxItem> ReadFolderPortalItems(BoxConfig box)
    {
        var result = new List<BoxItem>();
        if (string.IsNullOrWhiteSpace(box.FolderPath) || !Directory.Exists(box.FolderPath))
            return result;

        try
        {
            var dirInfo = new DirectoryInfo(box.FolderPath);
            foreach (var entry in dirInfo.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;
                if (entry.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                var isDir = entry is DirectoryInfo;
                var kind = isDir ? BoxItemKind.Folder : DetectItemKind(entry.FullName);

                result.Add(new BoxItem
                {
                    Id = entry.FullName.ToLowerInvariant(),
                    DisplayName = DeriveDisplayName(entry.FullName, kind),
                    TargetPath = entry.FullName,
                    Kind = kind,
                    IsMissing = false,
                    DateModified = entry.LastWriteTime,
                    Extension = isDir ? string.Empty : entry.Extension
                });
            }
        }
        catch
        {
            // Ignore transient access errors
        }

        return result;
    }

    public static IReadOnlyList<BoxItem> SortItems(
        IReadOnlyList<BoxItem> items,
        BoxSortMode sortMode,
        IReadOnlyList<string>? customItemOrder = null)
    {
        if (sortMode == BoxSortMode.Manual)
        {
            if (customItemOrder is { Count: > 0 })
            {
                var orderIndex = new Dictionary<string, int>(customItemOrder.Count, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < customItemOrder.Count; i++)
                {
                    var key = customItemOrder[i];
                    if (!string.IsNullOrWhiteSpace(key))
                        orderIndex.TryAdd(key, i);
                }

                return items
                    .OrderBy(i =>
                    {
                        var key = GetFileNameKey(i.TargetPath);
                        return orderIndex.TryGetValue(key, out var idx) ? idx : int.MaxValue;
                    })
                    .ThenBy(i => i.Kind == BoxItemKind.Folder ? 0 : 1)
                    .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            return items;
        }

        return sortMode switch
        {
            BoxSortMode.NameAsc => items
                .OrderBy(i => i.Kind == BoxItemKind.Folder ? 0 : 1)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            BoxSortMode.TypeAsc => items
                .OrderBy(i => i.Kind == BoxItemKind.Folder ? 0 : 1)
                .ThenBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            BoxSortMode.DateModifiedDesc => items
                .OrderByDescending(i => i.DateModified)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => items
        };
    }

    private static bool TargetExists(string targetPath, BoxItemKind kind)
    {
        if (kind == BoxItemKind.Url)
            return Uri.TryCreate(targetPath, UriKind.Absolute, out _);

        return Directory.Exists(targetPath) || File.Exists(targetPath);
    }

    private void EnsureWatcher(BoxConfig box)
    {
        DisposeWatcher(box.Id);

        if (box.Mode != BoxMode.FolderPortal || string.IsNullOrWhiteSpace(box.FolderPath) || !Directory.Exists(box.FolderPath))
            return;

        try
        {
            var watcher = new FileSystemWatcher(box.FolderPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };

            var entry = new FolderWatcherEntry(watcher, () => BoxItemsChanged?.Invoke(this, box.Id));
            watcher.Created += entry.OnFsEvent;
            watcher.Deleted += entry.OnFsEvent;
            watcher.Renamed += entry.OnFsEvent;
            watcher.Changed += entry.OnFsEvent;
            watcher.EnableRaisingEvents = true;

            _watchers[box.Id] = entry;
        }
        catch
        {
            // Folder may be inaccessible or on a network path that does not support watchers
        }
    }

    private void DisposeWatcher(string boxId)
    {
        if (_watchers.TryRemove(boxId, out var existing))
            existing.Dispose();
    }

    private void SaveAndNotify()
    {
        _configService.Save(_config);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _watchers.Values)
            entry.Dispose();
        _watchers.Clear();
    }

    private sealed class FolderWatcherEntry : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Action _callback;
        private readonly Timer _debounceTimer;
        private bool _disposed;

        public FolderWatcherEntry(FileSystemWatcher watcher, Action callback)
        {
            _watcher = watcher;
            _callback = callback;
            _debounceTimer = new Timer(_ =>
            {
                if (!_disposed) _callback();
            }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            if (_disposed) return;
            _debounceTimer.Change(150, Timeout.Infinite);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _debounceTimer.Dispose();
        }
    }
}
