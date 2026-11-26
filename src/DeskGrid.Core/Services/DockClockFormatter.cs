using System.Globalization;

namespace DeskGrid.Core.Services;

/// <summary>
/// Pure formatting helpers for the minimalist Dock clock and its hover tooltip.
/// </summary>
public static class DockClockFormatter
{
    private static readonly CultureInfo ZhCnCulture = CultureInfo.GetCultureInfo("zh-CN");

    public static string FormatTime(DateTime now, bool use24Hour, bool showSeconds)
    {
        if (use24Hour)
            return now.ToString(showSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);

        return now.ToString(showSeconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.InvariantCulture);
    }

    public static string FormatDate(DateTime now)
    {
        return now.ToString("M月d日 ddd", ZhCnCulture);
    }

    public static string FormatTooltip(DateTime now, bool use24Hour)
    {
        var datePart = now.ToString("yyyy年M月d日 dddd", ZhCnCulture);
        var timePart = FormatTime(now, use24Hour, showSeconds: true);
        return $"{datePart} {timePart}";
    }
}
