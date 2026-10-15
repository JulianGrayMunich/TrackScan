using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Calibration Review Flyout
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
namespace TrackScan;
public sealed class ScanCalibrationReviewWindow : Window
{
    public ScanCalibrationReviewWindow(ScanCalibrationReview review)
    {
        ArgumentNullException.ThrowIfNull(argument: review);
        Title = "Review calibration offsets — not yet adopted"; Width = 1350; Height = 700; MinWidth = 900; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(ietfLanguageTag: "en-GB");
        DockPanel layout = new() { Margin = new Thickness(uniformLength: 12) }; Content = layout;
        TextBlock instructions = new() { Text = "Untick an individual offset to exclude it. Missing readings are — and cannot be included. Each included scan has equal weight. Mean and sample standard deviation update immediately. All measurements are metres, displayed to 4 decimals. Existing monitoring offsets change only after adoption.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left: 0, top: 0, right: 0, bottom: 10) };
        DockPanel.SetDock(element: instructions, dock: Dock.Top); layout.Children.Add(element: instructions);
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(left: 0, top: 0, right: 0, bottom: 10) };
        ComboBox files = new() { Width = 500, SelectedIndex = 0 };
        for (int i = 0; i < review.Results.Count; i++) files.Items.Add(new ComboBoxItem { Content = $"Scan {i + 1}: {System.IO.Path.GetFileName(path: review.Results[i].SourcePath)}", ToolTip = review.Results[i].SourcePath });
        files.SelectedIndex = 0; tools.Children.Add(element: files);
        Button exclude = new() { Content = "Exclude this scan for all points", Margin = new Thickness(left: 10, top: 0, right: 0, bottom: 0), Padding = new Thickness(uniformLength: 5) };
        exclude.Click += (_, _) => { if (files.SelectedIndex >= 0) review.IncludeFile(index: files.SelectedIndex, include: false); };
        Button include = new() { Content = "Include this scan", Margin = new Thickness(left: 10, top: 0, right: 0, bottom: 0), Padding = new Thickness(uniformLength: 5) };
        include.Click += (_, _) => { if (files.SelectedIndex >= 0) review.IncludeFile(index: files.SelectedIndex, include: true); };
        tools.Children.Add(element: exclude); tools.Children.Add(element: include); DockPanel.SetDock(element: tools, dock: Dock.Top); layout.Children.Add(element: tools);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(left: 0, top: 10, right: 0, bottom: 0) };
        Button csv = new() { Content = "Save CSV", Width = 110, Height = 30 };
        csv.Click += (_, _) =>
        {
            SaveFileDialog dialog = new() { Title = "Save calibration review", Filter = "CSV (*.csv)|*.csv", FileName = $"CalibrationOffsets_{DateTime.Now:yyyyMMdd_HHmmss}.csv", AddExtension = true, DefaultExt = ".csv", OverwritePrompt = true };
            if (dialog.ShowDialog(owner: this) != true) return;
            try { review.Export(path: dialog.FileName); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
            { MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "CSV export", button: MessageBoxButton.OK, icon: MessageBoxImage.Error); }
        };
        Button adopt = new() { Content = "Adopt mean offsets", Width = 160, Height = 30, Margin = new Thickness(left: 10, top: 0, right: 0, bottom: 0) };
        adopt.Click += (_, _) =>
        {
            int changed = review.Rows.Count(predicate: row => row.Mean.HasValue);
            if (changed == 0) { MessageBox.Show(owner: this, messageBoxText: "No included valid observations. No offsets can be adopted.", caption: "Calibration", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning); return; }
            if (MessageBox.Show(owner: this, messageBoxText: $"Adopt the displayed mean offsets for {changed} railhead points? {review.Rows.Count - changed} points have no included observations and will retain their existing offsets, if any. The individual observations and inclusion decisions will also be saved.", caption: "Adopt mean offsets", button: MessageBoxButton.YesNo, icon: MessageBoxImage.Question) == MessageBoxResult.Yes) DialogResult = true;
        };
        Button cancel = new() { Content = "Cancel", Width = 100, Height = 30, IsCancel = true, Margin = new Thickness(left: 10, top: 0, right: 0, bottom: 0) };
        buttons.Children.Add(element: csv); buttons.Children.Add(element: adopt); buttons.Children.Add(element: cancel); DockPanel.SetDock(element: buttons, dock: Dock.Bottom); layout.Children.Add(element: buttons);
        DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, ItemsSource = review.Rows, EnableRowVirtualization = true, FrozenColumnCount = 1 };
        grid.Columns.Add(item: new DataGridTextColumn { Header = "Railhead point", Binding = new Binding(path: "PointId"), IsReadOnly = true, Width = 160 });
        for (int i = 0; i < review.Results.Count; i++)
        {
            FrameworkElementFactory panel = new(type: typeof(StackPanel)); panel.SetValue(dp: StackPanel.OrientationProperty, value: Orientation.Horizontal);
            FrameworkElementFactory check = new(type: typeof(CheckBox));
            check.SetBinding(dp: CheckBox.IsCheckedProperty, binding: new Binding(path: $"Cells[{i}].Included") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetBinding(dp: CheckBox.IsEnabledProperty, binding: new Binding(path: $"Cells[{i}].IsValid"));
            check.SetBinding(dp: CheckBox.ToolTipProperty, binding: new Binding(path: $"Cells[{i}].Status"));
            check.SetValue(dp: FrameworkElement.VerticalAlignmentProperty, value: VerticalAlignment.Center); check.SetValue(dp: FrameworkElement.MarginProperty, value: new Thickness(left: 5, top: 0, right: 8, bottom: 0)); panel.AppendChild(child: check);
            FrameworkElementFactory text = new(type: typeof(TextBlock)); text.SetBinding(dp: TextBlock.TextProperty, binding: new Binding(path: $"Cells[{i}].Offset") { StringFormat = "F4", TargetNullValue = "—" });
            Style style = new(targetType: typeof(TextBlock)); DataTrigger grey = new() { Binding = new Binding(path: $"Cells[{i}].Included"), Value = false }; grey.Setters.Add(item: new Setter(property: TextBlock.ForegroundProperty, value: Brushes.Gray)); style.Triggers.Add(item: grey); text.SetValue(dp: FrameworkElement.StyleProperty, value: style); panel.AppendChild(child: text);
            TextBlock header = new() { Text = $"Scan {i + 1}\n{review.Results[i].Readings[0].UtcTimestamp:yyyy-MM-dd HH:mm:ss} UTC", ToolTip = review.Results[i].SourcePath };
            grid.Columns.Add(item: new DataGridTemplateColumn { Header = header, CellTemplate = new DataTemplate { VisualTree = panel }, Width = 195 });
        }
        foreach (var column in new[] { ("Mean offset (m)", "Mean"), ("Std deviation (m)", "StandardDeviation"), ("Existing offset (m)", "ExistingOffset"), ("Difference (m)", "Difference") })
            grid.Columns.Add(item: new DataGridTextColumn { Header = column.Item1, Binding = new Binding(path: column.Item2) { StringFormat = "F4", TargetNullValue = "—" }, IsReadOnly = true, Width = 130 });
        grid.Columns.Add(item: new DataGridTextColumn { Header = "Status", Binding = new Binding(path: "Status"), IsReadOnly = true, Width = 280 });
        layout.Children.Add(element: grid);
    }
}
#endregion
