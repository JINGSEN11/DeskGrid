using DeskGrid.Core.Models;

namespace DeskGrid.Core.Services;

public sealed class DockManager
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly object _orderLock = new();
    private readonly List<string> _runningAppFifoOrder = [];
    private readonly Dictionary<string, List<nint>> _windowHandleFifoOrder = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? DockChanged;

    public DockConfig Settings => _config.Dock;

    public DockManager(AppConfig config, ConfigService configService)
    {
        _config = config;
        _configService = configService;
    }

    public static string NormalizeAppKey(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
            return string.Empty;

        var trimmed = pathOrName.Trim().Trim('"');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            return trimmed.ToLowerInvariant();
        }

        if (Directory.Exists(trimmed))
            return "dir:" + Path.GetFullPath(trimmed).TrimEnd('\\', '/').ToLowerInvariant();

        var fileName = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(fileName)
            ? trimmed.ToLowerInvariant()
            : fileName.ToLowerInvariant();
    }

    public DockSnapshot BuildSnapshot(IReadOnlyList<RunningWindowInfo> runningWindows)
    {
        var groupedRunning = runningWindows
            .GroupBy(w => string.IsNullOrWhiteSpace(w.AppKey) ? NormalizeAppKey(w.ExecutablePath) : w.AppKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        lock (_orderLock)
        {
            // Stabilize per-app window order in FIFO first-seen order so activating a window does not reorder multi-window popups
            var activeAppKeys = new HashSet<string>(groupedRunning.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var staleKey in _windowHandleFifoOrder.Keys.Where(k => !activeAppKeys.Contains(k)).ToList())
                _windowHandleFifoOrder.Remove(staleKey);

            foreach (var (key, rawWindows) in groupedRunning)
            {
                groupedRunning[key] = OrderWindowsFifoLocked(key, rawWindows);
            }

            var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pinnedSlots = new List<DockSlotItem>(_config.Dock.PinnedItems.Count);

            foreach (var pinned in _config.Dock.PinnedItems)
            {
                var key = string.IsNullOrWhiteSpace(pinned.AppKey)
                    ? NormalizeAppKey(pinned.TargetPath)
                    : pinned.AppKey;

                List<RunningWindowInfo> windows = [];
                if (!string.IsNullOrWhiteSpace(key) && groupedRunning.TryGetValue(key, out var found))
                {
                    windows = found;
                    matchedKeys.Add(key);
                }

                pinnedSlots.Add(new DockSlotItem
                {
                    Id = pinned.Id,
                    AppKey = key,
                    DisplayName = pinned.DisplayName,
                    TargetPath = pinned.TargetPath,
                    Arguments = pinned.Arguments,
                    Kind = pinned.Kind,
                    IsPinned = true,
                    IsActive = windows.Any(w => w.IsActive),
                    RunningWindows = windows
                });
            }

            // Collect currently active unpinned running app keys
            var currentRunningOnlyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, windows) in groupedRunning)
            {
                if (!matchedKeys.Contains(key) && windows.Count > 0)
                    currentRunningOnlyKeys.Add(key);
            }

            // Maintain strict FIFO order: remove closed or newly pinned apps, append newly opened apps to the tail
            _runningAppFifoOrder.RemoveAll(k => !currentRunningOnlyKeys.Contains(k));
            foreach (var (key, windows) in groupedRunning)
            {
                if (!matchedKeys.Contains(key) && windows.Count > 0 &&
                    !_runningAppFifoOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    _runningAppFifoOrder.Add(key);
                }
            }

            var runningOnlySlots = new List<DockSlotItem>(_runningAppFifoOrder.Count);
            foreach (var key in _runningAppFifoOrder)
            {
                if (!groupedRunning.TryGetValue(key, out var windows) || windows.Count == 0)
                    continue;

                var first = windows[0];
                var displayName = !string.IsNullOrWhiteSpace(first.ProcessName)
                    ? first.ProcessName
                    : first.Title;

                runningOnlySlots.Add(new DockSlotItem
                {
                    Id = "run:" + key,
                    AppKey = key,
                    DisplayName = displayName,
                    TargetPath = first.ExecutablePath,
                    Kind = BoxItemKind.Application,
                    IsPinned = false,
                    IsActive = windows.Any(w => w.IsActive),
                    RunningWindows = windows
                });
            }

            return new DockSnapshot(pinnedSlots, runningOnlySlots);
        }
    }

    private List<RunningWindowInfo> OrderWindowsFifoLocked(string appKey, List<RunningWindowInfo> currentWindows)
    {
        if (!_windowHandleFifoOrder.TryGetValue(appKey, out var order))
        {
            order = [];
            _windowHandleFifoOrder[appKey] = order;
        }

        var byHandle = new Dictionary<nint, RunningWindowInfo>(currentWindows.Count);
        foreach (var w in currentWindows)
            byHandle.TryAdd(w.Handle, w);

        order.RemoveAll(h => !byHandle.ContainsKey(h));
        foreach (var w in currentWindows)
        {
            if (!order.Contains(w.Handle))
                order.Add(w.Handle);
        }

        var ordered = new List<RunningWindowInfo>(order.Count);
        foreach (var h in order)
        {
            if (byHandle.TryGetValue(h, out var info))
                ordered.Add(info);
        }
        return ordered;
    }

    public static DockClickDecision DecideClickAction(DockSlotItem slot)
    {
        if (slot.RunningWindows.Count == 0)
            return new DockClickDecision(DockClickActionKind.Launch);

        if (slot.RunningWindows.Count > 1)
            return new DockClickDecision(DockClickActionKind.ShowWindowListPopup);

        var single = slot.RunningWindows[0];
        if (single.IsActive && !single.IsMinimized)
            return new DockClickDecision(DockClickActionKind.MinimizeWindow, single.Handle);

        return new DockClickDecision(DockClickActionKind.ActivateWindow, single.Handle);
    }

    public bool PinItem(string targetPath, string? displayName = null, string? resolvedExecutablePath = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return false;

        var normalizedPath = targetPath.Trim();
        var keySource = !string.IsNullOrWhiteSpace(resolvedExecutablePath) ? resolvedExecutablePath : normalizedPath;
        var appKey = NormalizeAppKey(keySource);

        if (_config.Dock.PinnedItems.Any(p =>
                string.Equals(p.TargetPath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(appKey) && string.Equals(p.AppKey, appKey, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        var kind = BoxManager.DetectItemKind(normalizedPath);
        var name = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : BoxManager.DeriveDisplayName(normalizedPath, kind);

        _config.Dock.PinnedItems.Add(new DockPinnedItem
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = name,
            TargetPath = normalizedPath,
            AppKey = appKey,
            Kind = kind
        });

        SaveAndNotify();
        return true;
    }

    public bool UnpinItem(string pinnedId)
    {
        var removed = _config.Dock.PinnedItems.RemoveAll(p => string.Equals(p.Id, pinnedId, StringComparison.Ordinal)) > 0;
        if (removed)
            SaveAndNotify();
        return removed;
    }

    public bool MovePinnedItem(int fromIndex, int toIndex)
    {
        var list = _config.Dock.PinnedItems;
        if (fromIndex < 0 || fromIndex >= list.Count || toIndex < 0 || toIndex >= list.Count || fromIndex == toIndex)
            return false;

        var item = list[fromIndex];
        list.RemoveAt(fromIndex);
        list.Insert(toIndex, item);
        SaveAndNotify();
        return true;
    }

    private void SaveAndNotify()
    {
        _configService.Save(_config);
        DockChanged?.Invoke(this, EventArgs.Empty);
    }
}
