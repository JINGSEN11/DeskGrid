using System.Diagnostics;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

/// <summary>
/// Provides Windows system power actions (Lock, Sleep, Restart, Shutdown).
/// </summary>
public static class SystemPowerService
{
    public static bool LockScreen()
    {
        try
        {
            return NativeMethods.LockWorkStation();
        }
        catch
        {
            return false;
        }
    }

    public static bool Sleep()
    {
        try
        {
            return NativeMethods.SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
        }
        catch
        {
            return false;
        }
    }

    public static void Restart()
    {
        ExecuteShutdownCommand("/r /t 0");
    }

    public static void Shutdown()
    {
        ExecuteShutdownCommand("/s /t 0");
    }

    private static void ExecuteShutdownCommand(string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
}
