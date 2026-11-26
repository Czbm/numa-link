using System.Diagnostics;
using System.Windows;
using NumaFiles.VirtualDrive;
using NumaLink.App.Services;

namespace NumaLink.App;

public partial class App : System.Windows.Application
{
    public static bool StartMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        TerminatePreviousInstances();
        WinFspBootstrapper.RegisterAssemblyResolver();
        base.OnStartup(e);
        StartMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        var settings = LinkSettingsStore.Load();
        LinkThemeManager.Apply(settings.ColorMode);
    }

    private static void TerminatePreviousInstances()
    {
        var currentPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("NumaLink"))
        {
            using (proc)
            {
                if (proc.Id == currentPid)
                    continue;
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                    }
                }
                catch
                {
                    // Best-effort cleanup of previous tray instances
                }
            }
        }
    }
}
