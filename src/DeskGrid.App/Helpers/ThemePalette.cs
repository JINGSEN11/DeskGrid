using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskGrid.Core.Models;
using Microsoft.Win32;

namespace DeskGrid.App.Helpers;

public static class ThemePalette
{
    public static readonly FontFamily UiFontFamily = new("Segoe UI Variable Text, Microsoft YaHei UI, SimSun");

    public static ContextMenu CreateThemedContextMenu()
    {
        var menu = new ContextMenu
        {
            FontFamily = UiFontFamily,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        if (Application.Current?.TryFindResource("DgContextMenuStyle") is Style menuStyle)
            menu.Style = menuStyle;

        if (Application.Current?.TryFindResource("DgMenuItemStyle") is Style itemStyle)
            menu.Resources[typeof(MenuItem)] = itemStyle;

        if (Application.Current?.TryFindResource("DgMenuSeparatorStyle") is Style sepStyle)
            menu.Resources[MenuItem.SeparatorStyleKey] = sepStyle;

        TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);
        return menu;
    }
    public static bool IsDarkMode(ThemePreference preference)
    {
        if (preference == ThemePreference.Dark) return true;
        if (preference == ThemePreference.Light) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            if (val is int i)
                return i == 0;
        }
        catch
        {
            // Fallback to dark Liquid Glass aesthetic
        }

        return true;
    }

    public static ThemeMode ToWpfThemeMode(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeMode.Light,
        ThemePreference.Dark => ThemeMode.Dark,
        _ => ThemeMode.System
    };

    private static ThemePreference? _lastAppliedPreference;
    private static bool? _lastAppliedDark;

    public static void ApplyToApplication(Application app, ThemePreference preference)
    {
        var dark = IsDarkMode(preference);
        var targetThemeMode = ToWpfThemeMode(preference);
        if (_lastAppliedPreference == preference &&
            _lastAppliedDark == dark &&
            app.ThemeMode == targetThemeMode &&
            app.Resources.Contains("DgDockSurfaceBrush"))
        {
            return;
        }

        _lastAppliedPreference = preference;
        _lastAppliedDark = dark;

        if (app.ThemeMode != targetThemeMode)
            app.ThemeMode = targetThemeMode;

        var res = app.Resources;
        if (dark)
        {
            // Liquid Glass Dark: rgba(18, 20, 26, 0.35)
            res["DgDockSurfaceBrush"] = CreateFrozenBrush(Color.FromArgb(0x59, 0x12, 0x14, 0x1A));
            res["DgGlassSheenBrush"] = CreateLiquidSheenBrush(
                Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
            // 1px Apple-style diagonal luminous glass border: rgba(255,255,255,0.35) -> rgba(255,255,255,0.08)
            res["DgGlassBorderBrush"] = CreateDiagonalGlassBorderBrush(
                Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            res["DgBorderBrush"] = res["DgGlassBorderBrush"];
            res["DgDividerBrush"] = CreateFrozenBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
            // Hover pill: rgba(255, 255, 255, 0.14)
            res["DgHoverBrush"] = CreateFrozenBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            res["DgHoverBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            res["DgSelectedBrush"] = CreateFrozenBrush(Color.FromArgb(0x40, 0x60, 0xCD, 0xFF));
            res["DgTextPrimaryBrush"] = CreateFrozenBrush(Color.FromRgb(0xFA, 0xFC, 0xFF));
            res["DgTextSecondaryBrush"] = CreateFrozenBrush(Color.FromArgb(0xCC, 0xE6, 0xEC, 0xF5));
            res["DgAccentBrush"] = CreateFrozenBrush(Color.FromRgb(0x6C, 0xD4, 0xFF));
            res["DgAccentMutedBrush"] = CreateFrozenBrush(Color.FromArgb(0x34, 0x6C, 0xD4, 0xFF));
            res["DgPopupBrush"] = CreateFrozenBrush(Color.FromArgb(0xCC, 0x16, 0x18, 0x20));
            res["DgDangerBrush"] = CreateFrozenBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));

            // SettingsWindow dedicated high-contrast palette (Dark Mode)
            res["DgSettingsNavForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
            res["DgSettingsNavHoverForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            res["DgSettingsNavHoverBackgroundBrush"] = CreateFrozenBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            res["DgSettingsNavSelectedForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            res["DgSettingsNavSelectedBackgroundBrush"] = CreateFrozenBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            res["DgSettingsTextPrimaryBrush"] = CreateFrozenBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
            res["DgSettingsTextSecondaryBrush"] = CreateFrozenBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            res["DgSettingsCardBackgroundBrush"] = CreateFrozenBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
            res["DgSettingsCardBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            res["DgSettingsDividerBrush"] = CreateFrozenBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            res["DgSettingsBadgeBackgroundBrush"] = CreateFrozenBrush(Color.FromArgb(0x38, 0x0A, 0x84, 0xFF));
            res["DgSettingsBadgeForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0x6C, 0xD4, 0xFF));
        }
        else
        {
            // Liquid Glass Light: rgba(255, 255, 255, 0.38)
            res["DgDockSurfaceBrush"] = CreateFrozenBrush(Color.FromArgb(0x61, 0xFF, 0xFF, 0xFF));
            res["DgGlassSheenBrush"] = CreateLiquidSheenBrush(
                Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
            res["DgGlassBorderBrush"] = CreateDiagonalGlassBorderBrush(
                Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            res["DgBorderBrush"] = res["DgGlassBorderBrush"];
            res["DgDividerBrush"] = CreateFrozenBrush(Color.FromArgb(0x34, 0x00, 0x00, 0x00));
            // Hover pill: subtle luminous white highlight
            res["DgHoverBrush"] = CreateFrozenBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
            res["DgHoverBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            res["DgSelectedBrush"] = CreateFrozenBrush(Color.FromArgb(0x36, 0x00, 0x78, 0xD4));
            res["DgTextPrimaryBrush"] = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            res["DgTextSecondaryBrush"] = CreateFrozenBrush(Color.FromArgb(0xDC, 0xF5, 0xF7, 0xFA));
            res["DgAccentBrush"] = CreateFrozenBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            res["DgAccentMutedBrush"] = CreateFrozenBrush(Color.FromArgb(0x2A, 0x0A, 0x84, 0xFF));
            res["DgPopupBrush"] = CreateFrozenBrush(Color.FromArgb(0xCC, 0xF4, 0xF6, 0xFA));
            res["DgDangerBrush"] = CreateFrozenBrush(Color.FromRgb(0xD9, 0x2D, 0x20));

            // SettingsWindow dedicated high-contrast palette (Light Mode)
            res["DgSettingsNavForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0x37, 0x41, 0x51));
            res["DgSettingsNavHoverForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27));
            res["DgSettingsNavHoverBackgroundBrush"] = CreateFrozenBrush(Color.FromArgb(0x0D, 0x00, 0x00, 0x00));
            res["DgSettingsNavSelectedForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27));
            res["DgSettingsNavSelectedBackgroundBrush"] = CreateFrozenBrush(Color.FromArgb(0x17, 0x00, 0x00, 0x00));
            res["DgSettingsTextPrimaryBrush"] = CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27));
            res["DgSettingsTextSecondaryBrush"] = CreateFrozenBrush(Color.FromRgb(0x4B, 0x55, 0x63));
            res["DgSettingsCardBackgroundBrush"] = CreateFrozenBrush(Color.FromArgb(0x08, 0x00, 0x00, 0x00));
            res["DgSettingsCardBorderBrush"] = CreateFrozenBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            res["DgSettingsDividerBrush"] = CreateFrozenBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            res["DgSettingsBadgeBackgroundBrush"] = CreateFrozenBrush(Color.FromArgb(0x1F, 0x0A, 0x84, 0xFF));
            res["DgSettingsBadgeForegroundBrush"] = CreateFrozenBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        }
    }

    public static SolidColorBrush CreateTintedSurfaceBrush(bool isDark, double opacity)
    {
        // Map user opacity cleanly: Dark default 0.35 -> rgba(18,20,26,0.35); Light default -> slightly brighter 0.38
        var effectiveOpacity = isDark
            ? Math.Clamp(opacity, 0.15, 0.88)
            : Math.Clamp(opacity + 0.03, 0.18, 0.90);
        var alpha = (byte)Math.Round(effectiveOpacity * 255);
        var color = isDark
            ? Color.FromArgb(alpha, 0x12, 0x14, 0x1A)
            : Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);
        return CreateFrozenBrush(color);
    }

    private static LinearGradientBrush CreateDiagonalGlassBorderBrush(Color topLeft, Color mid, Color bottomRight)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(topLeft, 0.0));
        brush.GradientStops.Add(new GradientStop(mid, 0.48));
        brush.GradientStops.Add(new GradientStop(bottomRight, 1.0));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateLiquidSheenBrush(Color top, Color mid, Color bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.15, 0.0),
            EndPoint = new Point(0.85, 1.0)
        };
        brush.GradientStops.Add(new GradientStop(top, 0.0));
        brush.GradientStops.Add(new GradientStop(mid, 0.38));
        brush.GradientStops.Add(new GradientStop(bottom, 1.0));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
