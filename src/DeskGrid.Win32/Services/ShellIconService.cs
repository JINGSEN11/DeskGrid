using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskGrid.Core.Abstractions;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

public sealed class ShellIconService : IFileOperationsService
{
    private const int MaxCacheEntries = 512;
    private const int MinHighResIconPx = 96;
    private const int MaxHighResIconPx = 256;
    private const uint WindowIconTimeoutMs = 150;
    private static readonly Guid ShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private sealed record LruEntry(string Key, BitmapSource Value);

    private readonly object _lruLock = new();
    private readonly Dictionary<string, LinkedListNode<LruEntry>> _lruMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<LruEntry> _lruList = new();
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inFlightLoads = new(StringComparer.OrdinalIgnoreCase);

    // Dedicated background STA worker queue so IShellItemImageFactory / SHGetFileInfo COM calls
    // NEVER execute on or block the WPF UI thread.
    private readonly BlockingCollection<Action> _staQueue = new();
    private readonly Thread _staWorker;

    public ShellIconService()
    {
        _staWorker = new Thread(RunStaWorkerLoop)
        {
            Name = "DeskGrid.ShellIconStaWorker",
            IsBackground = true
        };
        if (OperatingSystem.IsWindows())
            _staWorker.SetApartmentState(ApartmentState.STA);
        _staWorker.Start();
    }

    private void RunStaWorkerLoop()
    {
        foreach (var work in _staQueue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch
            {
                // Isolate individual shell extraction exceptions
            }
        }
    }

    private Task<T> RunOnStaWorkerAsync<T>(Func<T> func, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _staQueue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Computes a crisp high-resolution physical pixel size for shell icon extraction,
    /// oversampling by DPI and snapping to native Windows icon buckets (96/128/192/256)
    /// so WPF always downsamples cleanly instead of upscaling low-res 32px/48px bitmaps.
    /// </summary>
    public static int ComputePhysicalIconPixels(double displayDipSize, double dpiScale = 1.0)
    {
        var safeDpi = Math.Max(1.0, dpiScale);
        var desired = (int)Math.Ceiling(Math.Max(32.0, displayDipSize) * safeDpi * 1.5);
        if (desired <= 96) return 96;
        if (desired <= 128) return 128;
        if (desired <= 192) return 192;
        return MaxHighResIconPx;
    }

    private bool TryGetFromLru(string cacheKey, out BitmapSource? icon)
    {
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(cacheKey, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                icon = node.Value.Value;
                return true;
            }
        }

        icon = null;
        return false;
    }

    private void PutInLru(string cacheKey, BitmapSource icon)
    {
        if (icon.CanFreeze && !icon.IsFrozen)
            icon.Freeze();

        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(cacheKey, out var existing))
            {
                existing.Value = new LruEntry(cacheKey, icon);
                _lruList.Remove(existing);
                _lruList.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<LruEntry>(new LruEntry(cacheKey, icon));
            _lruList.AddFirst(node);
            _lruMap[cacheKey] = node;

            while (_lruMap.Count > MaxCacheEntries && _lruList.Last is { } oldest)
            {
                _lruList.RemoveLast();
                _lruMap.Remove(oldest.Value.Key);
            }
        }
    }

    private static string BuildPathCacheKey(string path, int requestPx) => $"path:{path}|{requestPx}";
    private static string BuildWindowCacheKey(nint hwnd, int requestPx) => $"hwnd:{hwnd}|{requestPx}";

    /// <summary>
    /// Fast non-blocking O(1) LRU cache check for UI threads.
    /// </summary>
    public bool TryGetCachedIconForPath(string path, int pixelSize, out BitmapSource? icon)
    {
        icon = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var requestPx = Math.Clamp(pixelSize, MinHighResIconPx, MaxHighResIconPx);
        return TryGetFromLru(BuildPathCacheKey(path, requestPx), out icon);
    }

    /// <summary>
    /// Fast non-blocking O(1) LRU cache check for running window icons on UI threads.
    /// </summary>
    public bool TryGetCachedIconForWindow(nint hwnd, string? fallbackExePath, int pixelSize, out BitmapSource? icon)
    {
        icon = null;
        var requestPx = Math.Clamp(pixelSize, MinHighResIconPx, MaxHighResIconPx);
        if (!string.IsNullOrWhiteSpace(fallbackExePath) &&
            TryGetFromLru(BuildPathCacheKey(fallbackExePath, requestPx), out icon))
        {
            return true;
        }

        if (hwnd != 0 && TryGetFromLru(BuildWindowCacheKey(hwnd, requestPx), out icon))
            return true;

        return false;
    }

    /// <summary>
    /// Asynchronously loads a shell icon on the dedicated background STA worker thread
    /// with LRU caching and in-flight deduplication (never blocks UI thread).
    /// </summary>
    public Task<BitmapSource?> GetIconForPathAsync(string path, int pixelSize = 128, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult<BitmapSource?>(null);

        var requestPx = Math.Clamp(pixelSize, MinHighResIconPx, MaxHighResIconPx);
        var cacheKey = BuildPathCacheKey(path, requestPx);
        if (TryGetFromLru(cacheKey, out var cached))
            return Task.FromResult<BitmapSource?>(cached);

        var loadTask = _inFlightLoads.GetOrAdd(cacheKey, _ => LoadPathIconCoreAsync(cacheKey, path, requestPx));
        return cancellationToken.CanBeCanceled
            ? loadTask.WaitAsync(cancellationToken)
            : loadTask;
    }

    /// <summary>
    /// Asynchronously loads a running window icon on the dedicated background STA worker thread
    /// with non-blocking SendMessageTimeout(SMTO_ABORTIFHUNG) and LRU caching.
    /// </summary>
    public Task<BitmapSource?> GetIconForWindowAsync(
        nint hwnd,
        string? fallbackExePath = null,
        int pixelSize = 128,
        CancellationToken cancellationToken = default)
    {
        var requestPx = Math.Clamp(pixelSize, MinHighResIconPx, MaxHighResIconPx);
        if (TryGetCachedIconForWindow(hwnd, fallbackExePath, requestPx, out var cached))
            return Task.FromResult<BitmapSource?>(cached);

        var dedupeKey = !string.IsNullOrWhiteSpace(fallbackExePath)
            ? $"win:{fallbackExePath}|{hwnd}|{requestPx}"
            : BuildWindowCacheKey(hwnd, requestPx);

        var loadTask = _inFlightLoads.GetOrAdd(dedupeKey, _ => LoadWindowIconCoreAsync(dedupeKey, hwnd, fallbackExePath, requestPx));
        return cancellationToken.CanBeCanceled
            ? loadTask.WaitAsync(cancellationToken)
            : loadTask;
    }

    private async Task<BitmapSource?> LoadPathIconCoreAsync(string cacheKey, string path, int requestPx)
    {
        try
        {
            var icon = await RunOnStaWorkerAsync(() => ExtractIconForPathCore(path, requestPx), CancellationToken.None)
                .ConfigureAwait(false);
            if (icon is not null)
                PutInLru(cacheKey, icon);
            return icon;
        }
        finally
        {
            _inFlightLoads.TryRemove(cacheKey, out _);
        }
    }

    private async Task<BitmapSource?> LoadWindowIconCoreAsync(string dedupeKey, nint hwnd, string? fallbackExePath, int requestPx)
    {
        try
        {
            var icon = await RunOnStaWorkerAsync(
                () =>
                {
                    if (!string.IsNullOrWhiteSpace(fallbackExePath) && File.Exists(fallbackExePath))
                    {
                        var pathKey = BuildPathCacheKey(fallbackExePath, requestPx);
                        if (TryGetFromLru(pathKey, out var cachedExe))
                            return cachedExe;

                        var fromExe = ExtractIconForPathCore(fallbackExePath, requestPx);
                        if (fromExe is not null)
                        {
                            PutInLru(pathKey, fromExe);
                            return fromExe;
                        }
                    }

                    if (hwnd == 0)
                        return null;

                    var winKey = BuildWindowCacheKey(hwnd, requestPx);
                    if (TryGetFromLru(winKey, out var cachedWin))
                        return cachedWin;

                    var fromWin = ExtractIconFromWindowHandle(hwnd);
                    if (fromWin is not null)
                        PutInLru(winKey, fromWin);
                    return fromWin;
                },
                CancellationToken.None).ConfigureAwait(false);

            return icon;
        }
        finally
        {
            _inFlightLoads.TryRemove(dedupeKey, out _);
        }
    }

    public BitmapSource? GetIconForPath(string path, int pixelSize = 128)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var requestPx = Math.Clamp(pixelSize, MinHighResIconPx, MaxHighResIconPx);
        var cacheKey = BuildPathCacheKey(path, requestPx);
        if (TryGetFromLru(cacheKey, out var cached))
            return cached;

        var icon = ExtractIconForPathCore(path, requestPx);
        if (icon is not null)
            PutInLru(cacheKey, icon);

        return icon;
    }

    public BitmapSource? GetIconForWindow(nint hwnd, string? fallbackExePath = null, int pixelSize = 128)
    {
        var requestPx = Math.Clamp(pixelSize, MinHighResIconPx, MaxHighResIconPx);
        if (TryGetCachedIconForWindow(hwnd, fallbackExePath, requestPx, out var cached))
            return cached;

        if (!string.IsNullOrWhiteSpace(fallbackExePath) && File.Exists(fallbackExePath))
        {
            var fromExe = GetIconForPath(fallbackExePath, requestPx);
            if (fromExe is not null)
                return fromExe;
        }

        if (hwnd == 0)
            return null;

        var fromWin = ExtractIconFromWindowHandle(hwnd);
        if (fromWin is not null)
            PutInLru(BuildWindowCacheKey(hwnd, requestPx), fromWin);
        return fromWin;
    }

    private static BitmapSource? ExtractIconForPathCore(string path, int requestPx)
    {
        var icon = TryGetShellItemBitmap(path, requestPx)
            ?? TryGetShGetFileInfoIcon(path);

        if (icon is not null && icon.CanFreeze && !icon.IsFrozen)
            icon.Freeze();

        return icon;
    }

    private static BitmapSource? ExtractIconFromWindowHandle(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return null;

        nint hIcon = QueryWindowIconNonBlocking(hwnd, NativeMethods.ICON_BIG);
        if (hIcon == 0)
            hIcon = QueryWindowIconNonBlocking(hwnd, NativeMethods.ICON_SMALL2);
        if (hIcon == 0)
            hIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCL_HICON);
        if (hIcon == 0)
            hIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCL_HICONSM);

        if (hIcon == 0)
            return null;

        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static nint QueryWindowIconNonBlocking(nint hwnd, nint iconType)
    {
        var ok = NativeMethods.SendMessageTimeout(
            hwnd,
            NativeMethods.WM_GETICON,
            iconType,
            0,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
            WindowIconTimeoutMs,
            out var result);
        return ok != 0 ? (nint)result : 0;
    }

    private static BitmapSource? TryGetShellItemBitmap(string path, int pixelSize)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return null;

        nint hBitmap = 0;
        try
        {
            var hr = NativeMethods.SHCreateItemFromParsingName(path, 0, ShellItemImageFactoryGuid, out var factory);
            if (hr != 0 || factory is null)
                return null;

            try
            {
                var size = new NativeMethods.SIZE { cx = pixelSize, cy = pixelSize };
                var flags = NativeMethods.SIIGBF.SIIGBF_RESIZETOFIT | NativeMethods.SIIGBF.SIIGBF_BIGGERSIZEOK;
                hr = factory.GetImage(size, flags, out hBitmap);
                if (hr != 0 || hBitmap == 0)
                    return null;

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    nint.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                if (source.CanFreeze) source.Freeze();
                return source;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != 0)
                NativeMethods.DeleteObject(hBitmap);
        }
    }

    private static BitmapSource? TryGetShGetFileInfoIcon(string path)
    {
        var shfi = new NativeMethods.SHFILEINFO();
        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
        uint attrs = 0;

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            attrs = NativeMethods.FILE_ATTRIBUTE_NORMAL;
        }

        NativeMethods.SHGetFileInfo(path, attrs, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
        if (shfi.hIcon == 0)
            return null;

        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(shfi.hIcon);
        }
    }

    public void OpenPath(string path, string? arguments = null, bool asAdmin = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var psi = new ProcessStartInfo
        {
            FileName = path,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true
        };

        if (asAdmin)
            psi.Verb = "runas";

        if (File.Exists(path))
            psi.WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty;

        Process.Start(psi);
    }

    public void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path) || Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
    }

    public bool MoveToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;

        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = path + '\0' + '\0',
            fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT | NativeMethods.FOF_NOERRORUI
        };

        var result = NativeMethods.SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    public string? ResolveShortcutTarget(string lnkPath)
    {
        if (!File.Exists(lnkPath) || !lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var link = (NativeMethods.IShellLinkW)new NativeMethods.ShellLink();
            try
            {
                var file = (NativeMethods.IPersistFile)link;
                file.Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, 0, 0);
                var resolved = sb.ToString();
                return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch
        {
            return null;
        }
    }

    public string RenameFileSystemEntry(string fullPath, string newName)
    {
        var sanitized = newName.Trim();
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("文件名包含非法字符。", nameof(newName));

        var parentDir = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无法确定父目录。");

        var destination = Path.Combine(parentDir, sanitized);
        if (string.Equals(fullPath, destination, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        if (Directory.Exists(fullPath))
            Directory.Move(fullPath, destination);
        else if (File.Exists(fullPath))
            File.Move(fullPath, destination);
        else
            throw new FileNotFoundException("找不到目标文件或目录。", fullPath);

        return destination;
    }

    public string TransferIntoFolder(string sourcePath, string destinationFolder, bool copy)
    {
        if (!Directory.Exists(destinationFolder))
            throw new DirectoryNotFoundException($"目标目录不存在: {destinationFolder}");

        var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        var destination = GetUniqueDestinationPath(destinationFolder, name);

        if (string.Equals(Path.GetFullPath(trimmed), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return destination;

        var sameVolume = string.Equals(Path.GetPathRoot(trimmed), Path.GetPathRoot(destinationFolder), StringComparison.OrdinalIgnoreCase);
        var shouldCopy = copy || !sameVolume;

        if (Directory.Exists(trimmed))
        {
            if (shouldCopy)
                CopyDirectoryRecursive(trimmed, destination);
            else
                Directory.Move(trimmed, destination);
        }
        else if (File.Exists(trimmed))
        {
            if (shouldCopy)
                File.Copy(trimmed, destination);
            else
                File.Move(trimmed, destination);
        }

        return destination;
    }

    private static string GetUniqueDestinationPath(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(folder, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{baseName}_{Guid.NewGuid():N}{ext}");
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}
