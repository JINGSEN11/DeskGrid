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
    private static readonly Guid ShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private readonly ConcurrentDictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);

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

    public BitmapSource? GetIconForPath(string path, int pixelSize = 128)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var requestPx = Math.Clamp(pixelSize, MinHighResIconPx, MaxHighResIconPx);
        var cacheKey = $"{path}|{requestPx}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        BitmapSource? icon = TryGetShellItemBitmap(path, requestPx)
            ?? TryGetShGetFileInfoIcon(path);

        if (icon is not null)
        {
            if (icon.CanFreeze && !icon.IsFrozen)
                icon.Freeze();

            if (_cache.Count >= MaxCacheEntries)
                _cache.Clear();

            _cache[cacheKey] = icon;
        }

        return icon;
    }

    public BitmapSource? GetIconForWindow(nint hwnd, string? fallbackExePath = null, int pixelSize = 128)
    {
        var requestPx = Math.Clamp(pixelSize, MinHighResIconPx, MaxHighResIconPx);
        if (!string.IsNullOrWhiteSpace(fallbackExePath) && File.Exists(fallbackExePath))
        {
            var fromExe = GetIconForPath(fallbackExePath, requestPx);
            if (fromExe is not null)
                return fromExe;
        }

        if (hwnd == 0)
            return null;

        nint hIcon = NativeMethods.SendMessage(hwnd, NativeMethods.WM_GETICON, NativeMethods.ICON_BIG, 0);
        if (hIcon == 0)
            hIcon = NativeMethods.SendMessage(hwnd, NativeMethods.WM_GETICON, NativeMethods.ICON_SMALL2, 0);
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
