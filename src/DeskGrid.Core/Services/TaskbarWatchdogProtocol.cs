using System.Globalization;

namespace DeskGrid.Core.Services;

public sealed record TaskbarWatchdogOptions(
    int MainProcessId,
    string ConfigDirectory,
    string? CancelEventName);

public static class TaskbarWatchdogProtocol
{
    public const string WatchdogSwitch = "--watchdog";
    public const string ConfigDirSwitch = "--config-dir";
    public const string CancelEventSwitch = "--cancel-event";

    public const string PrimarySentinelFileName = "taskbar_hidden.flag";
    public const string LegacySentinelFileName = "taskbar.hidden.flag";

    public static bool TryParseArgs(string[]? args, out TaskbarWatchdogOptions options)
    {
        options = null!;
        if (args is null || args.Length == 0)
            return false;

        int? mainPid = null;
        string? configDir = null;
        string? cancelEventName = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, WatchdogSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                    return false;
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid) || parsedPid <= 0)
                    return false;
                mainPid = parsedPid;
            }
            else if (string.Equals(arg, ConfigDirSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    configDir = args[++i];
            }
            else if (string.Equals(arg, CancelEventSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    cancelEventName = args[++i];
            }
        }

        if (mainPid is null)
            return false;

        var resolvedConfigDir = string.IsNullOrWhiteSpace(configDir)
            ? ConfigService.ResolveConfigDirectory()
            : configDir;

        options = new TaskbarWatchdogOptions(mainPid.Value, resolvedConfigDir, cancelEventName);
        return true;
    }

    public static IReadOnlyList<string> BuildArgs(int mainProcessId, string configDirectory, string cancelEventName)
    {
        var list = new List<string>(6)
        {
            WatchdogSwitch,
            mainProcessId.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            list.Add(ConfigDirSwitch);
            list.Add(configDirectory);
        }

        if (!string.IsNullOrWhiteSpace(cancelEventName))
        {
            list.Add(CancelEventSwitch);
            list.Add(cancelEventName);
        }

        return list;
    }

    public static string FormatSentinel(DateTime utcNow, int originalAppBarState, int mainProcessId) =>
        $"{utcNow.ToUniversalTime():O}|{originalAppBarState.ToString(CultureInfo.InvariantCulture)}|{mainProcessId.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParseSentinelOriginalState(string? content, out int originalAppBarState)
    {
        originalAppBarState = 0;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var parts = content.Trim().Split('|');
        if (parts.Length >= 2 &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            originalAppBarState = parsed;
            return true;
        }

        return false;
    }
}
