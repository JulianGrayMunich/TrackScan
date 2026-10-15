using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.IO;
using System.Windows;
using Microsoft.Win32;
#endregion

namespace TrackScan;

public partial class MainWindow
{
    #region Compute And Export Selected Track Reference Points
    private async void btnScanCompute_Click(object sender, RoutedEventArgs e)
    {
        if (_scanEditingTrack is null || _scanState is null) return;
        if (_scanEditorDirty)
        {
            MessageBox.Show(owner: this, messageBoxText: "Save the track name, gauge and coordinates before computing track points.",
                caption: "Compute Railhead Points", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning);
            return;
        }
        ScanTrack selected = _scanEditingTrack;
        await RunScanOperationAsync(operation: async () =>
        {
            ScanRepository repository = CurrentScanRepository();
            ScanProjectState expected = _scanState ?? throw new InvalidOperationException(message: "Refresh Track Configuration first.");
            txtScanStatus.Text = $"Loading the Railhead survey for '{selected.Name}'...";
            ScanComputationInput input = await repository.LoadComputationAsync(expected: expected, trackId: selected.TrackId);
            txtScanStatus.Text = $"Computing both rails for '{selected.Name}' and checking all heights...";
            ScanComputationOutcome outcome;
            try { outcome = await Task.Run(function: () => ScanEngine.TryComputeTrackPoints(track: input.Track, survey: input.Survey)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                txtScanStatus.Text = ex.Message;
                MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Track computation halted", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning);
                return;
            }
            if (!outcome.Succeeded)
            {
                string warning = outcome.Warning ?? throw new InvalidOperationException(message: "The unsuccessful computation returned no warning.");
                txtScanStatus.Text = warning;
                MessageBox.Show(owner: this, messageBoxText: warning, caption: "Track computation halted", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning);
                return;
            }
            ScanComputationResult result = outcome.Result ?? throw new InvalidOperationException(message: "The successful computation returned no points.");
            if (chkRailheadToRComparison.IsChecked == true)
            {
                var comparisons = await Task.Run(function: () => ScanRailheadComparison.Create(points: result.Points, survey: input.Survey));
                new ScanRailheadComparisonWindow(trackName: result.Track.Name, rows: comparisons) { Owner = this }.ShowDialog();
            }
            ScanPointReviewWindow review = new(result: result, exportCsv: DebugCsvEnabled) { Owner = this };
            if (review.ShowDialog() != true)
            {
                txtScanStatus.Text = "Review cancelled. No computed points have been saved to the database.";
                return;
            }
            SaveFileDialog dialog = new()
            {
                Title = "Save computed track points CSV", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv",
                AddExtension = true, OverwritePrompt = true, FileName = $"TrackPoints_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (DebugCsvEnabled && dialog.ShowDialog(owner: this) != true)
            {
                txtScanStatus.Text = "Computation completed, but saving was cancelled. No new points have been saved to the database.";
                return;
            }
            txtScanStatus.Text = "Saving the completed reference points...";
            long run = await repository.SaveComputationAsync(expected: expected, input: input, result: result);
            string completion = $"Track '{selected.Name}': {result.Points.Count:N0} points saved (run {run}).";
            try
            {
                if (DebugCsvEnabled)
                {
                    await Task.Run(action: () => ScanPointCsv.Export(path: dialog.FileName, result: result));
                    completion += $" CSV: {dialog.FileName}";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                completion += $" CSV export failed: {ex.Message}. The database points are saved. Compute again to retry the export.";
                MessageBox.Show(owner: this, messageBoxText: completion, caption: "CSV export incomplete", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning);
            }
            try
            {
                await RefreshScanAsync();
                if (_scanState is not null)
                    foreach (ScanTrack track in _scanState.Tracks)
                        if (track.TrackId == selected.TrackId) { dgScanTracks.SelectedItem = track; break; }
            }
            catch (Exception ex) { completion += $" Refresh failed: {ex.Message}. Use Refresh before continuing."; }
            txtScanStatus.Text = completion;
        });
    }
    #endregion
}
