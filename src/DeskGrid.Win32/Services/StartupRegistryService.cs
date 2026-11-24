using DeskGrid.Core.Abstractions;
using Microsoft.Win32;

namespace DeskGrid.Win32.Services;

public sealed class StartupRegistryService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "DeskGrid";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var val = key?.GetValue(AppValueName) as string;
        return !string.IsNullOrWhiteSpace(val);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
            key.SetValue(AppValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        else
            key.DeleteValue(AppValueName, throwOnMissingValue: false);
    }
}
