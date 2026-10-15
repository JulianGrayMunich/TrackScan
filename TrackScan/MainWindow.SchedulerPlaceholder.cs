#region Temporary Scheduled Processing Placeholder
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace TrackScan;
public partial class MainWindow
{
    private DateTime? _lastPlaceholderRunUtc;
    private void PresentSchedulerPlaceholder(ScanSchedulerStatus? progress)
    {
        if (progress is null || progress.State != "Placeholder" || !progress.LastRunUtc.HasValue ||
            progress.LastRunUtc == _lastPlaceholderRunUtc || DateTime.UtcNow - progress.HeartbeatUtc > TimeSpan.FromSeconds(value: 10)) return;
        _lastPlaceholderRunUtc = progress.LastRunUtc;
        ScanSchedulerPlaceholderWindow window = new() { Owner = this };
        window.Show();
    }
}
public sealed class ScanSchedulerPlaceholderWindow : Window
{
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(value: 5) };
    public ScanSchedulerPlaceholderWindow()
    {
        Title = "Scan Scheduler - temporary test"; Width = 420; Height = 150;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TextBlock { Text = "LAS processing placeholder\nNo LAS files or readings are changed.\nThis window closes automatically after five seconds.",
            Margin = new Thickness(uniformLength: 20), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _closeTimer.Tick += (_, _) => Close();
        Loaded += (_, _) => _closeTimer.Start();
        Closed += (_, _) => _closeTimer.Stop();
    }
}
#endregion
