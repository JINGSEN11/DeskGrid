using System.Runtime.CompilerServices;
using DeskGrid.Core.Services;
using DeskGrid.Win32.Services;

namespace DeskGrid.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Intercept lightweight watchdog mode BEFORE touching any WPF types or UI resources
        // so the background guard keeps minimal memory footprint and never hits SingleInstanceMutex.
        if (TaskbarWatchdogProtocol.TryParseArgs(args, out var watchdogOptions))
            return TaskbarWatchdog.Run(watchdogOptions);

        return RunWpfApp();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWpfApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
