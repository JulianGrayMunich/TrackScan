#region Application And Unattended Service Entry
using System.Windows;
namespace TrackScan;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e: e);
        if (e.Args.Length > 0) ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Length == 1 && e.Args[0] == "--run-scan-schedule")
        { Shutdown(exitCode: await Task.Run(function: () => ScanScheduledRunner.RunAsync())); return; }
        if (e.Args.Length == 3 && e.Args[0] == "--configure-scheduler")
        { Shutdown(exitCode: ScanSchedulerSetup.Configure(stagedPath: e.Args[1], action: e.Args[2])); return; }
        if (e.Args.Length > 0) { Shutdown(exitCode: 1); return; }
        MainWindow window = new(); MainWindow = window; window.Show();
    }
}
#endregion
