using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.IO;
using System.Windows.Controls;
using System.Windows;
using Microsoft.Win32;
#endregion
namespace TrackScan;

internal sealed record ScanCollinearityOption(decimal Value, string Label);

public partial class MainWindow
{
    #region Polygon Configuration And Review
    private bool PolygonCsvEnabled => chkPolygonExportCsv.IsChecked == true;
    private void cmbPolygonTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (btnCreatePolygons is not null) UpdateScanAvailability();
    }

    private void UpdatePolygonComments()
    {
        if (tabScanPolygons is null || !tabScanPolygons.IsSelected || txtScanGuidance is null) return;
        txtScanGuidance.Text = "Compute rail corridor and railhead polygons for the selected track. Scan points are filtered by corridor before testing its railhead polygons. Other tracks retain their saved polygons. Review before saving to DBTrackGeometry. Use Export polygon points to CSV for a checking file.";
        if (cmbPolygonTrack.Items.Count == 0)
            txtScanGuidance.Text += "\nCompute and save Railhead Points for a track before creating polygons.";
    }

    private void InitialisePolygonOptions()
    {
        cmbCorridorWidth.ItemsSource = new[] { 0.30m, 0.50m, 0.75m, 1.00m };
        cmbCollinearity.ItemsSource = new[] {
            new ScanCollinearityOption(Value: 0m, Label: "None"),
            new ScanCollinearityOption(Value: 0.01m, Label: "0.01"),
            new ScanCollinearityOption(Value: 0.05m, Label: "0.05"),
            new ScanCollinearityOption(Value: 0.10m, Label: "0.1") };
        cmbHeadWidth.ItemsSource = new[] { 0.02m, 0.03m, 0.04m, 0.05m, 0.06m, 0.075m, 0.10m, 0.20m, 0.25m, 0.50m, 0.75m };
        cmbHeadLength.ItemsSource = new[] { 0.40m, 0.50m, 0.60m };
        cmbHeightFilter.ItemsSource = new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        ApplyPolygonOptions(options: ScanPolygonOptions.Default);
    }

    private void ApplyPolygonOptions(ScanPolygonOptions options)
    {
        cmbCorridorWidth.SelectedItem = options.CorridorWidth; cmbCollinearity.SelectedValue = options.CollinearityLimit;
        cmbHeadWidth.SelectedItem = options.RailheadWidth; cmbHeadLength.SelectedItem = options.RailheadLength;
        cmbHeightFilter.SelectedItem = options.HeightFilterMillimetres;
    }

    private ScanPolygonOptions ReadPolygonOptions() => new(
        CorridorWidth: (decimal)(cmbCorridorWidth.SelectedItem ?? throw new InvalidOperationException(message: "Select corridor width.")),
        CollinearityLimit: (decimal)(cmbCollinearity.SelectedValue ?? throw new InvalidOperationException(message: "Select collinearity limit.")),
        RailheadWidth: (decimal)(cmbHeadWidth.SelectedItem ?? throw new InvalidOperationException(message: "Select polygon width.")),
        RailheadLength: (decimal)(cmbHeadLength.SelectedItem ?? throw new InvalidOperationException(message: "Select polygon length.")),
        HeightFilterMillimetres: (int)(cmbHeightFilter.SelectedItem ?? throw new InvalidOperationException(message: "Select height filter.")));

    private static string SafePolygonTrackName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(values: name.Select(selector: c => invalid.Contains(value: c) ? '_' : c)).TrimEnd(trimChars: new[] { ' ', '.' });
    }

    private async void btnCreatePolygons_Click(object sender, RoutedEventArgs e)
    {
        if (cmbPolygonTrack.SelectedItem is not ScanPolygonTrack selectedTrack) return;
        if (_scanEditorDirty)
        {
            MessageBox.Show(owner: this, messageBoxText: "Save or cancel the track edits, then recompute and save its railhead points before creating polygons.", caption: "Create Polygons", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning);
            return;
        }
        await RunScanOperationAsync(operation: async () =>
        {
            ScanRepository repository = CurrentScanRepository();
            ScanProjectState expected = _scanState ?? throw new InvalidOperationException(message: "Refresh Track Configuration first.");
            ScanPolygonOptions options = ReadPolygonOptions();
            txtScanStatus.Text = "Loading current saved railhead points...";
            IReadOnlyList<ScanPolygonRail> rails = await repository.LoadPolygonRailsAsync(expected: expected, trackId: selectedTrack.TrackId);
            ScanPolygonSet set = await Task.Run(function: () => ScanEngine.CreatePolygons(rails: rails, options: options, includeCorridors: true));
            await repository.ValidatePolygonsAsync(set: set);
            ScanPolygonReviewWindow review = new(set: set, exportCsv: PolygonCsvEnabled) { Owner = this };
            if (review.ShowDialog() != true) { txtScanStatus.Text = "Polygon review cancelled. Nothing saved."; return; }
            SaveFileDialog destination = new() { Title = "Save polygon checking CSV", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true, OverwritePrompt = true, FileName = $"RailPolygons_{SafePolygonTrackName(name: selectedTrack.TrackName)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
            if (PolygonCsvEnabled && destination.ShowDialog(owner: this) != true) { txtScanStatus.Text = "CSV selection cancelled. No polygons saved."; return; }
            long batch = await repository.SavePolygonsAsync(expected: expected, set: set);
            await RefreshProcessingRailsAsync(repository: repository, state: expected);
            try
            {
                if (PolygonCsvEnabled) await Task.Run(action: () => ScanPolygonCsv.Export(path: destination.FileName, set: set));
                txtScanStatus.Text = $"Polygon set {batch}: {set.Polygons.Count:N0} polygons saved in DBTrackGeometry. {(PolygonCsvEnabled ? "CSV: " + destination.FileName : string.Empty)}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                txtScanStatus.Text = $"Polygon set {batch} saved in DBTrackGeometry, but CSV export failed: {ex.Message}";
                MessageBox.Show(owner: this, messageBoxText: txtScanStatus.Text, caption: "Polygon CSV export", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning);
            }
        });
        if (_scanState is not null) { pnlScanWorkspace.SelectedItem = tabScanPolygons; tabScanPolygons.Focus(); }
    }
    #endregion
}
