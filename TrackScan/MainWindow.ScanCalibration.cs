using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Calibration File Queue And Review Workflow
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
namespace TrackScan;
public partial class MainWindow
{
    private sealed class CalibrationFile : INotifyPropertyChanged
    {
        public string Path { get; }
        public string FileName => System.IO.Path.GetFileName(path: Path);
        private string _status = "Queued", _timestamp = "Resolved when processed";
        public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(sender: this, e: new(propertyName: nameof(Status))); } }
        public string Timestamp { get => _timestamp; set { _timestamp = value; PropertyChanged?.Invoke(sender: this, e: new(propertyName: nameof(Timestamp))); } }
        public CalibrationFile(string path) { Path = path; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly ObservableCollection<CalibrationFile> _calibrationFiles = new();
    private void AddCalibrationFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path: path);
            if (_calibrationFiles.Any(predicate: file => string.Equals(a: file.Path, b: full, comparisonType: StringComparison.OrdinalIgnoreCase))) continue;
            _calibrationFiles.Add(item: new(path: full));
        }
        gridCalibrationFiles.ItemsSource = _calibrationFiles;
        PresentCalibrationFileTimes();
        UpdateCalibrationFileAvailability();
    }
    private void PresentCalibrationFileTimes()
    {
        foreach (CalibrationFile file in _calibrationFiles)
        {
            try
            {
                ScanTimestamp stamp = ScanEngine.ResolveScanTimestamp(path: file.Path, timeZoneId: _processingTimeZoneId, utcNow: DateTime.UtcNow);
                file.Timestamp = stamp.Source == ScanEngine.FallbackTimestampSource ? "Now (UTC) at processing" : stamp.Utc.ToString(format: EpochDisplayFormat, provider: System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { file.Timestamp = ex.Message; }
        }
    }
    private void CalibrationRemove_Click(object sender, RoutedEventArgs e)
    { foreach (CalibrationFile file in gridCalibrationFiles.SelectedItems.Cast<CalibrationFile>().ToArray()) _calibrationFiles.Remove(item: file); UpdateScanAvailability(); }
    private void CalibrationClear_Click(object sender, RoutedEventArgs e) { _calibrationFiles.Clear(); UpdateScanAvailability(); }
    private void UpdateCalibrationFileAvailability()
    {
        if (pnlCalibrationFiles is null) return;
        pnlCalibrationFiles.Visibility = Visibility.Visible;
        btnSelectProcessingLas.Content = "Select LAS files";
        Visibility fileActionsVisibility = _calibrationFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        btnCalibrationRemove.Visibility = fileActionsVisibility; btnCalibrationClear.Visibility = fileActionsVisibility;
        btnCalibrationAdd.IsEnabled = !_scanBusy; btnCalibrationRemove.IsEnabled = !_scanBusy; btnCalibrationClear.IsEnabled = !_scanBusy;
        cmbProcessingRejectionCount.IsEnabled = !_scanBusy;
        btnReviewStoredOffsets.IsEnabled = !_scanBusy && _scanState is not null && _scanGeometryProject == _activeProjectId;
    }
    // Chart numbers are local to each named file and exclude empty railhead polygons.
    private static List<int> GetDataChartIndices(ScanProcessingResult result)
    {
        List<int> indices = new();
        for (int index = 0; index < result.DebugHistograms.Count / 2; index++)
            if (result.DebugHistograms[index * 2].Count > 0) indices.Add(item: index);
        return indices;
    }
    private async Task ProcessCalibrationFilesAsync(ScanRepository repository, ScanProjectState expected,
        IReadOnlyList<ScanProcessingRail> rails, CancellationTokenSource cancellation, bool debugHistograms, int rejectionCount)
    {
        CalibrationFile[] files = _calibrationFiles.ToArray();
        if (files.Length == 0) throw new InvalidOperationException(message: "Select calibration LAS files first.");
        List<ScanProcessingResult> results = new();
        ScanOffsetRunningMean runningOffsets = new();
        HashSet<string> hashes = new(comparer: StringComparer.OrdinalIgnoreCase);
        foreach (CalibrationFile file in files) file.Status = "Queued";
        try
        {
            for (int i = 0; i < files.Length; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                CalibrationFile file = files[i]; file.Status = "Processing";
                ScanTimestamp stamp = ScanEngine.ResolveScanTimestamp(path: file.Path, timeZoneId: _processingTimeZoneId, utcNow: DateTime.UtcNow);
                file.Timestamp = stamp.Utc.ToString(format: EpochDisplayFormat, provider: System.Globalization.CultureInfo.InvariantCulture);
                txtProcessingEpoch.Text = file.Timestamp;
                txtProcessingTimestampSource.Text = stamp.Source == ScanEngine.FallbackTimestampSource ? "No filename timestamp: Now (UTC)" : _processingTimeZoneId;
                int number = i + 1;
                Progress<string> progress = new(handler: text => txtScanStatus.Text = $"File {number}/{files.Length}: {file.FileName}. {text}");
                ScanProcessingResult result = await Task.Run(function: () => ScanEngine.ProcessLas(path: file.Path, utcTimestamp: stamp.Utc,
                    rails: rails, calculateOffsets: true, progress: progress, cancellationToken: cancellation.Token,
                    collectDebugHistograms: debugHistograms, rejectionCount: rejectionCount));
                result = result with { TimeZoneId = _processingTimeZoneId, TimestampSource = stamp.Source };
                if (!hashes.Add(item: result.Sha256)) throw new InvalidOperationException(message: $"'{file.FileName}' duplicates another selected scan's contents. Remove the duplicate before calibration. No offsets saved.");
                IReadOnlyList<ScanOffsetMeanSnapshot> previousMeans = runningOffsets.PreviousMeans(readings: result.Offsets);
                List<int> chartIndices = GetDataChartIndices(result: result);
                for (int chartNumber = 0; chartNumber < chartIndices.Count; chartNumber++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    int chartIndex = chartIndices[chartNumber];
                    // The paired histograms use the same target order as Offsets.
                    // RawMean is the exact final-subset mean used to compute the offset.
                    ScanHeightHistogramWindow window = new(
                        histogram: result.DebugHistograms[chartIndex * 2],
                        retained: result.DebugHistograms[chartIndex * 2 + 1],
                        adoptedMean: result.Offsets[chartIndex].RawMean,
                        referenceHeight: result.Offsets[chartIndex].ReferenceHeight,
                        scanOffset: result.Offsets[chartIndex].ScanOffset,
                        fileName: file.FileName,
                        chartNumber: chartNumber + 1,
                        chartCount: chartIndices.Count,
                        previousOffsetMean: previousMeans[chartIndex].Mean, previousOffsetCount: previousMeans[chartIndex].Count) { Owner = this };
                    if (!await ScanChartReviewPair.ShowAsync(owner: this, chart: window,
                        details: new ScanDecisionWindow(details: result.ChartDiagnostics[chartIndex], pointId: result.Offsets[chartIndex].PointId),
                        cancellationToken: cancellation.Token)) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                }
                results.Add(item: result with { DebugHistograms = Array.Empty<ScanHeightHistogram>(), ChartDiagnostics = Array.Empty<string>(), TemporaryDiagnostics = Array.Empty<string>() });
                file.Status = result.Warnings.Count == 0 ? "Ready for review" : $"Ready; {result.Warnings.Count} missing results";
            }
            cancellation.Token.ThrowIfCancellationRequested();
            ScanCalibrationReview review = new(results: results.AsReadOnly());
            txtScanStatus.Text = "Review calibration observations. No monitoring offsets have changed.";
            if (new ScanCalibrationReviewWindow(review: review) { Owner = this }.ShowDialog() != true)
            { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            cancellation.Token.ThrowIfCancellationRequested();
            btnCancelProcessing.IsEnabled = false;
            txtScanStatus.Text = "Saving calibration observations and adopting approved mean offsets...";
            await repository.AdoptCalibrationAsync(expected: expected, review: review);
            foreach (CalibrationFile file in files) file.Status = "Calibration review saved";
            int adopted = review.Rows.Count(predicate: row => row.Mean.HasValue);
            txtScanStatus.Text = $"Adopted mean offsets for {adopted} railhead points from {files.Length} scans. {review.Rows.Count - adopted} points had no included observations and retained their existing offsets, if any. Calibration observations and decisions saved to DBTrackGeometry.";
        }
        catch
        {
            foreach (CalibrationFile file in files) if (file.Status != "Calibration review saved") file.Status = "Not adopted";
            throw;
        }
    }
}
#endregion
