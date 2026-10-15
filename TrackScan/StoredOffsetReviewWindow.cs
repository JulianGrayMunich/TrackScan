#region Read Only Stored Offset Review
using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
namespace TrackScan;

public sealed record StoredOffsetPoint(long PolygonId, string PointId, string TrackName, double? AdoptedMean, Guid? AdoptedCalibrationId) { public long OffsetVersion { get; init; } public decimal ReferenceHeight { get; init; } }
public sealed record StoredOffsetObservation(long PolygonId, Guid CalibrationId, int FileIndex, string FilePath,
    DateTime ObservationUtc, DateTime AdoptedUtc, double? Offset, bool Included, string Status) { public bool? SelectedForMean { get; init; } public long ScanPointCount { get; init; } public long RejectedPointCount { get; init; } public string SourceSha256 { get; init; } = string.Empty; }
public sealed record StoredOffsetReview(IReadOnlyList<StoredOffsetPoint> Points, IReadOnlyList<StoredOffsetObservation> Observations);

public sealed class StoredOffsetReviewWindow : Window
{
    public StoredOffsetEditor Editor { get; }
    public StoredOffsetReviewWindow(string projectName, StoredOffsetReview review, Func<StoredOffsetEditor, Task>? save = null)
    {
        Editor = new(source: review);
        Title = "Review offsets — " + projectName; Width = 1200; Height = 650; MinWidth = 750; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(ietfLanguageTag: "en-GB");
        DockPanel layout = new() { Margin = new Thickness(uniformLength: 12) }; Content = layout;
        TextBlock instructions = new() { Text = "Tick the offsets to use for each point. Recompute mean previews the result; Save adopts it for future processing only. Historical readings remain unchanged. With no ticked offsets, Save clears the adopted baseline for that point. Values are metres, to 4 decimals.", TextWrapping = TextWrapping.Wrap };
        StackPanel commentText = new(); commentText.Children.Add(element: instructions);
        TextBlock status = new() { Text = "Select offsets, then Recompute mean.", Margin = new Thickness(uniformLength: 8), TextWrapping = TextWrapping.Wrap };
        commentText.Children.Add(element: status);
        GroupBox comments = new() { Header = "Comments", Margin = new Thickness(left: 0, top: 8, right: 0, bottom: 0),
            Content = new ScrollViewer { Content = commentText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 120, Padding = new Thickness(uniformLength: 6) } };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(uniformLength: 8) };
        Button recompute = new() { Content = "Recompute mean", Width = 150, Height = 30 };
        Button cancel = new() { Content = "Cancel", IsCancel = true, Width = 100, Height = 30, Margin = new Thickness(left: 10, top: 0, right: 0, bottom: 0) };
        Button saveButton = new() { Content = "Save", Width = 100, Height = 30, IsEnabled = false, Margin = new Thickness(left: 10, top: 0, right: 0, bottom: 0) };
        buttons.Children.Add(element: recompute); buttons.Children.Add(element: cancel); buttons.Children.Add(element: saveButton);
        DockPanel.SetDock(element: buttons, dock: Dock.Bottom); layout.Children.Add(element: buttons);
        DockPanel.SetDock(element: comments, dock: Dock.Bottom); layout.Children.Add(element: comments);
        DataGrid grid = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, ItemsSource = Editor.Rows };
        Style wrappedCell = new(targetType: typeof(TextBlock));
        wrappedCell.Setters.Add(item: new Setter(property: TextBlock.TextWrappingProperty, value: TextWrapping.Wrap));
        foreach (var column in new[] { ("Railhead point", "PointId"), ("Adopted mean (m)", "ExistingMean"), ("Recomputed mean (m)", "Mean") })
            grid.Columns.Add(item: new DataGridTextColumn { Header = new TextBlock { Text = column.Item1, TextWrapping = TextWrapping.Wrap, Width = 115 }, ElementStyle = wrappedCell, Binding = new Binding(path: column.Item2) { StringFormat = "F4", TargetNullValue = "—" }, Width = 127.5 });
        for (int index = 0; index < Editor.Scans.Count; index++)
        {
            StoredOffsetObservation scan = Editor.Scans[index];
            FrameworkElementFactory panel = new(type: typeof(DockPanel));
            FrameworkElementFactory box = new(type: typeof(CheckBox));
            box.SetBinding(dp: CheckBox.IsCheckedProperty, binding: new Binding(path: $"Cells[{index}].Selected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            box.SetBinding(dp: CheckBox.IsEnabledProperty, binding: new Binding(path: $"Cells[{index}].IsValid"));
            box.SetValue(dp: FrameworkElement.MarginProperty, value: new Thickness(left: 5, top: 0, right: 8, bottom: 0));
            box.SetValue(dp: DockPanel.DockProperty, value: Dock.Left);
            box.SetValue(dp: FrameworkElement.VerticalAlignmentProperty, value: VerticalAlignment.Center); panel.AppendChild(child: box);
            FrameworkElementFactory value = new(type: typeof(TextBlock));
            value.SetBinding(dp: TextBlock.TextProperty, binding: new Binding(path: $"Cells[{index}].Offset") { StringFormat = "F4", TargetNullValue = "—" });
            value.SetValue(dp: TextBlock.TextWrappingProperty, value: TextWrapping.Wrap);
            panel.AppendChild(child: value);
            grid.Columns.Add(item: new DataGridTemplateColumn { Header = new TextBlock { Text = $"Scan {index + 1}\n{scan.ObservationUtc:yyyy-MM-dd HH:mm:ss} UTC", ToolTip = scan.FilePath, TextWrapping = TextWrapping.Wrap, Width = 138 }, CellTemplate = new DataTemplate { VisualTree = panel }, Width = 150 });
        }
        TextBlock selectedPointComment = new() { TextWrapping = TextWrapping.Wrap };
        selectedPointComment.SetBinding(dp: TextBlock.TextProperty, binding: new Binding(path: "SelectedItem.Status") { Source = grid, TargetNullValue = string.Empty });
        commentText.Children.Add(element: selectedPointComment);
        grid.FrozenColumnCount = 3; layout.Children.Add(element: grid);
        bool saving = false;
        Editor.Changed += () => { saveButton.IsEnabled = false; status.Text = "Selection changed. Click Recompute mean before Save."; };
        recompute.Click += (_, _) => { Editor.Recompute(); saveButton.IsEnabled = save is not null && Editor.HasChanges; status.Text = Editor.HasChanges ? "Preview computed. Save applies these decisions to future processing only." : "No inclusion decisions changed."; };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        saveButton.Click += async (_, _) =>
        {
            if (save is null || !Editor.IsRecomputed) return;
            saving = true; grid.IsEnabled = false; buttons.IsEnabled = false;
            try { await save(Editor); saving = false; DialogResult = true; }
            catch (Exception ex) { status.Text = "Save failed: " + ex.Message; MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Review offsets", button: MessageBoxButton.OK, icon: MessageBoxImage.Error); }
            finally { saving = false; grid.IsEnabled = true; buttons.IsEnabled = true; }
        };
    }

    public static DataTable BuildTable(StoredOffsetReview review)
    {
        ArgumentNullException.ThrowIfNull(argument: review);
        DataTable table = new() { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add(columnName: "Point", type: typeof(string)).Caption = "Railhead point";
        table.Columns.Add(columnName: "Track", type: typeof(string)).Caption = "Track";
        Dictionary<(Guid Calibration, int File), string> columns = new();
        foreach (StoredOffsetObservation observation in review.Observations)
        {
            var key = (observation.CalibrationId, observation.FileIndex);
            if (columns.ContainsKey(key: key)) continue;
            string name = "Scan" + (columns.Count + 1).ToString(provider: CultureInfo.InvariantCulture);
            columns.Add(key: key, value: name);
            DataColumn column = table.Columns.Add(columnName: name, type: typeof(string));
            column.Caption = $"Scan {columns.Count}\n{observation.ObservationUtc:yyyy-MM-dd HH:mm:ss} UTC";
            column.ExtendedProperties["Tooltip"] = $"{observation.FilePath}\nCalibration saved: {observation.AdoptedUtc:yyyy-MM-dd HH:mm:ss} UTC\n{observation.CalibrationId}";
        }
        table.Columns.Add(columnName: "AdoptedMean", type: typeof(string)).Caption = "Adopted mean offset (m)";
        table.Columns["AdoptedMean"]!.SetOrdinal(ordinal: 2);
        Dictionary<long, (DataRow Row, StoredOffsetPoint Point)> rows = new();
        foreach (StoredOffsetPoint point in review.Points)
        {
            DataRow row = table.NewRow(); row["Point"] = point.PointId; row["Track"] = point.TrackName;
            foreach (string column in columns.Values) row[column] = "—";
            row["AdoptedMean"] = point.AdoptedMean?.ToString(format: "F4", provider: CultureInfo.InvariantCulture) ?? "—";
            table.Rows.Add(row: row); rows.Add(key: point.PolygonId, value: (row, point));
        }
        foreach (StoredOffsetObservation observation in review.Observations)
        {
            if (!rows.TryGetValue(key: observation.PolygonId, value: out var target)) continue;
            bool current = target.Point.AdoptedCalibrationId == observation.CalibrationId;
            string status = !observation.Included ? "Excluded" : current ? "Included" : "Historical";
            if (!current && !observation.Included) status = "Historical, excluded";
            string value = observation.Offset?.ToString(format: "F4", provider: CultureInfo.InvariantCulture) ?? "—";
            target.Row[columns[(observation.CalibrationId, observation.FileIndex)]] = value;
        }
        return table;
    }
}
#endregion
