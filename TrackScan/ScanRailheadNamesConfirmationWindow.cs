#region Railhead Name Update Confirmation
using System.Windows;
using System.Windows.Controls;
namespace TrackScan;

public sealed class ScanRailheadNamesConfirmationWindow : Window
{
    public ScanRailheadNamesConfirmationWindow(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: projectName);
        Title = "Update Railhead Names";
        Width = 520; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        StackPanel panel = new() { Margin = new Thickness(uniformLength: 20) };
        panel.Children.Add(element: new TextBlock { Text = "Warning", FontWeight = FontWeights.Bold, FontSize = 16, Margin = new Thickness(left: 0, top: 0, right: 0, bottom: 12) });
        panel.Children.Add(element: new TextBlock { Text = $"Project: {projectName}", FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left: 0, top: 0, right: 0, bottom: 12) });
        panel.Children.Add(element: new TextBlock { Text = "Proceed will check this project's railhead names in DBTrackGeometry and add any missing names. Existing names will be retained.\n\nVerify that this is the correct project before continuing,", TextWrapping = TextWrapping.Wrap });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(left: 0, top: 18, right: 0, bottom: 0) };
        Button proceed = new() { Content = "Proceed", Width = 100, Height = 30 };
        Button cancel = new() { Content = "Cancel", Width = 100, Height = 30, IsCancel = true, IsDefault = true, Margin = new Thickness(left: 10, top: 0, right: 0, bottom: 0) };
        proceed.Click += (_, _) => DialogResult = true;
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(element: proceed); buttons.Children.Add(element: cancel);
        panel.Children.Add(element: buttons); Content = panel;
        Loaded += (_, _) => cancel.Focus();
    }
}
#endregion
