#region Temporary Scan Filtering Decision Message
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace TrackScan;

public sealed class ScanDecisionWindow : Window
{
    public ScanDecisionWindow(string details, string pointId = "Track_AB_S025")
    {
        ArgumentNullException.ThrowIfNull(argument: details);
        Title = pointId + " — filtering decisions";
        Width = 950; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        DockPanel panel = new() { Margin = new Thickness(uniformLength: 12) };
        TextBlock guidance = new() { Text = "Use Next on the chart to advance both windows. Closing either window cancels calibration.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left: 0, top: 8, right: 0, bottom: 0) };
        DockPanel.SetDock(element: guidance, dock: Dock.Bottom); panel.Children.Add(element: guidance);
        panel.Children.Add(element: new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily(familyName: "Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }
}
#endregion
