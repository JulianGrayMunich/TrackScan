using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Railhead Comparison Flyout
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
namespace TrackScan;

public sealed class ScanRailheadComparisonWindow : Window
{
    private readonly IReadOnlyList<ScanRailheadComparisonRow> _rows;
    private readonly TextBlock _status = new() { Margin = new Thickness(uniformLength: 0), TextWrapping = TextWrapping.Wrap };

    public ScanRailheadComparisonWindow(string trackName, IReadOnlyList<ScanRailheadComparisonRow> rows)
    {
        _rows = rows ?? throw new ArgumentNullException(paramName: nameof(rows));
        Title = "Railhead Points vs ToR Points";
        Width = 1180; Height = 570; MinWidth = 780; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(ietfLanguageTag: "en-GB");
        Grid layout = new() { Margin = new Thickness(uniformLength: 16) };
        layout.RowDefinitions.Add(value: new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(value: new() { Height = new GridLength(value: 1, type: GridUnitType.Star) });
        layout.RowDefinitions.Add(value: new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(value: new() { Height = GridLength.Auto });
        TextBlock summary = new() { Text = $"Track: {trackName} — {rows.Count:N0} railhead points. All distances and heights are in metres.", Margin = new Thickness(left: 0, top: 0, right: 0, bottom: 12) };
        layout.Children.Add(element: summary);
        DataGrid grid = new() { Name = "ComparisonGrid", AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true,
            HeadersVisibility = DataGridHeadersVisibility.Column, ItemsSource = rows };
        string[] headers = { "Railhead Point name", "Railhead E", "Railhead N", "Railhead Ht", "ToR_name", "ToR E", "ToR N", "ToR Ht", "ds", "dh" };
        string[] properties = { "RailheadName", "RailheadE", "RailheadN", "RailheadHeight", "ToRName", "ToRE", "ToRN", "ToRHeight", "Ds", "Dh" };
        for (int i = 0; i < headers.Length; i++)
        {
            bool numeric = i != 0 && i != 4;
            Binding binding = new(path: properties[i]);
            if (numeric) binding.StringFormat = "F3";
            Style style = new(targetType: typeof(TextBlock));
            style.Setters.Add(item: new Setter(property: TextBlock.TextAlignmentProperty, value: numeric ? TextAlignment.Right : TextAlignment.Left));
            style.Setters.Add(item: new Setter(property: FrameworkElement.MarginProperty, value: new Thickness(left: 4, top: 0, right: 4, bottom: 0)));
            grid.Columns.Add(item: new DataGridTextColumn { Header = new TextBlock { Text = headers[i] }, Binding = binding, ElementStyle = style, Width = new DataGridLength(pixels: numeric ? 98 : 155) });
        }
        Grid.SetRow(element: grid, value: 1); layout.Children.Add(element: grid);
        _status.Text = "Nearest ToR point is selected by horizontal distance, without a search-radius limit. Equal distances use the first survey record. ds = horizontal distance; dh = Railhead Ht − ToR Ht. Display: 3 decimals; CSV: 4 decimals. An interpolated railhead height may differ from the nearest ToR height. This comparison does not save or change railhead points.";
        _status.Margin = new Thickness(left: 0, top: 12, right: 0, bottom: 12);
        Grid.SetRow(element: _status, value: 2); layout.Children.Add(element: _status);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button save = new() { Content = "Save as CSV", Width = 120, Height = 28, Margin = new Thickness(left: 0, top: 0, right: 10, bottom: 0) };
        save.Click += SaveCsv_Click;
        Button close = new() { Content = "Close", Width = 95, Height = 28, IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(element: save); buttons.Children.Add(element: close);
        Grid.SetRow(element: buttons, value: 3); layout.Children.Add(element: buttons); Content = layout;
    }

    private void SaveCsv_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new() { Title = "Save railhead / ToR comparison", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv",
            AddExtension = true, OverwritePrompt = true, FileName = $"Railhead_vs_ToR_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
        if (dialog.ShowDialog(owner: this) != true) return;
        try { ScanRailheadComparison.Export(path: dialog.FileName, rows: _rows); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Comparison CSV export failed", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning); }
    }
}
#endregion
