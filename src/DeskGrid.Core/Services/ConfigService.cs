using System.Text.Json;
using System.Text.Json.Serialization;
using DeskGrid.Core.Models;

namespace DeskGrid.Core.Services;

public sealed class ConfigService
{
    public const int MaxBackups = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly object _syncLock = new();

    public string ConfigDirectory { get; }
    public string ConfigFilePath { get; }

    public ConfigService(string? customDirectory = null)
    {
        ConfigDirectory = ResolveConfigDirectory(customDirectory);
        ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");
    }

    public static string ResolveConfigDirectory(string? customDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(customDirectory))
            return customDirectory;

        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "portable.flag")))
            return Path.Combine(baseDir, "data");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "DeskGrid");
    }

    public AppConfig LoadOrCreate(Func<AppConfig> defaultFactory)
    {
        lock (_syncLock)
        {
            if (TryLoadFromFile(ConfigFilePath, out var config))
                return Sanitize(config);

            for (var i = 1; i <= MaxBackups; i++)
            {
                var backupPath = GetBackupPath(i);
                if (TryLoadFromFile(backupPath, out var recovered))
                {
                    var sanitized = Sanitize(recovered);
                    SaveCore(sanitized);
                    return sanitized;
                }
            }

            var fresh = Sanitize(defaultFactory());
            SaveCore(fresh);
            return fresh;
        }
    }

    public void Save(AppConfig config)
    {
        lock (_syncLock)
        {
            SaveCore(Sanitize(config));
        }
    }

    public void ExportTo(string destinationFilePath, AppConfig config)
    {
        var dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(Sanitize(config), JsonOptions);
        var tempPath = destinationFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, destinationFilePath, overwrite: true);
    }

    public AppConfig ImportFrom(string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("未找到指定的配置文件。", sourceFilePath);

        if (!TryLoadFromFile(sourceFilePath, out var imported))
            throw new InvalidDataException("配置文件格式无效或已损坏。");

        var sanitized = Sanitize(imported);
        Save(sanitized);
        return sanitized;
    }

    private void SaveCore(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tempPath = Path.Combine(ConfigDirectory, $"config.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(tempPath, json);

        if (File.Exists(ConfigFilePath))
        {
            RotateBackups();
            File.Replace(tempPath, ConfigFilePath, GetBackupPath(1), ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, ConfigFilePath, overwrite: true);
        }
    }

    private void RotateBackups()
    {
        for (var i = MaxBackups; i >= 2; i--)
        {
            var prev = GetBackupPath(i - 1);
            var curr = GetBackupPath(i);
            if (File.Exists(prev))
                File.Move(prev, curr, overwrite: true);
        }
    }

    private string GetBackupPath(int index) =>
        Path.Combine(ConfigDirectory, $"config.bak{index}.json");

    private static bool TryLoadFromFile(string path, out AppConfig config)
    {
        config = null!;
        try
        {
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            var parsed = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (parsed is null)
                return false;

            if (!HasRootProperty(json, "showDock") && parsed.Dock is not null)
            {
                parsed.ShowDock = parsed.Dock.Enabled;
            }
            else if (parsed.Dock is not null)
            {
                parsed.Dock.Enabled = parsed.ShowDock;
            }

            config = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRootProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Ignore malformed structure; Deserialize already succeeded
        }

        return false;
    }

    private static AppConfig Sanitize(AppConfig config)
    {
        config.Appearance ??= new AppearanceSettings();
        config.Behavior ??= new BehaviorSettings();
        config.Dock ??= new DockConfig();
        config.Dock.Enabled = config.ShowDock;
        config.Dock.PinnedItems ??= [];
        config.Boxes ??= [];

        // Smooth visual migration from Schema v1 (heavy 0.78 opacity) to v2 (Liquid Glass 0.35 + golden 340x260 layout)
        if (config.SchemaVersion < 2)
        {
            if (config.Appearance.DefaultBoxOpacity >= 0.70)
                config.Appearance.DefaultBoxOpacity = 0.35;

            foreach (var box in config.Boxes)
            {
                if (box.Opacity >= 0.70)
                    box.Opacity = 0.35;
                box.AutoFitHeight = false;
            }

            var desktopBox = config.Boxes.FirstOrDefault(b => b.Id == "box-desktop-portal");
            var quickBox = config.Boxes.FirstOrDefault(b => b.Id == "box-quick-virtual");
            if (desktopBox is not null && quickBox is not null)
            {
                desktopBox.Left = 36;
                desktopBox.Top = 36;
                desktopBox.Width = 340;
                desktopBox.Height = 260;
                quickBox.Left = 36;
                quickBox.Top = 316;
                quickBox.Width = 340;
                quickBox.Height = 260;
            }

            config.SchemaVersion = 2;
        }

        config.Appearance.DefaultBoxOpacity = Math.Clamp(config.Appearance.DefaultBoxOpacity, 0.15, 0.90);
        config.Behavior.GridSnapStep = Math.Clamp(config.Behavior.GridSnapStep, 4, 48);
        config.Dock.MultiMonitorMode = DockMultiMonitorModes.Normalize(config.Dock.MultiMonitorMode);
        config.Dock.DockScale = DockLayoutMetrics.ClampScale(config.Dock.DockScale);
        config.Dock.BaseIconSize = Math.Clamp(config.Dock.BaseIconSize, 32, 64);
        config.Dock.MagnificationScale = Math.Clamp(config.Dock.MagnificationScale, 1.0, 1.8);
        config.Dock.MagnificationRadius = Math.Clamp(config.Dock.MagnificationRadius, 60, 260);

        foreach (var box in config.Boxes)
        {
            box.Id = string.IsNullOrWhiteSpace(box.Id) ? Guid.NewGuid().ToString("N") : box.Id;
            box.Title = string.IsNullOrWhiteSpace(box.Title) ? "未命名盒子" : box.Title.Trim();
            box.Width = Math.Max(220, box.Width);
            box.Height = Math.Max(140, box.Height);
            box.Opacity = Math.Clamp(box.Opacity, 0.15, 0.90);
            box.VirtualItems ??= [];
            box.CustomItemOrder ??= [];
        }

        return config;
    }
}
