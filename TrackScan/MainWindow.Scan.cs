using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
#endregion

namespace TrackScan;

public partial class MainWindow
{
    #region Scan Preparation State
    private ScanProjectState? _scanState;
    private ScanSurveySnapshot? _scanPreview;
    private IReadOnlyList<ScanSurveyPreview> _scanStoredSurvey = Array.Empty<ScanSurveyPreview>();
    private int? _scanGeometryProject;
    private string _scanConnection = string.Empty;
    private bool _scanBusy;
    private bool _scanEditorDirty;
    private bool _scanFillingEditor;
    private ScanTrack? _scanEditingTrack;
    private ScanTrack? _scanLastSelection;

    private void InitialiseScanPreparation()
    {

        InitialisePolygonOptions();
        InitialiseScanProcessing();
        Closing += ScanWindow_Closing;
        Closed += ScanWindow_Closed;
        UpdateScanAvailability();
    }

    private void ScanWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_scanBusy)
        {
            e.Cancel = true;
            MessageBox.Show(owner: this, messageBoxText: "Wait for the scan preparation operation to finish before closing.", caption: "Scan preparation", button: MessageBoxButton.OK, icon: MessageBoxImage.Information);
        }
        else if (!ConfirmDiscardScanEdit()) e.Cancel = true;
    }

    private void ScanWindow_Closed(object? sender, EventArgs e)
    {
        _processingCancellation?.Cancel();

        ClearScanPreview();
    }

    private void ClearScanPreview()
    {
        _scanPreview?.Dispose();
        _scanPreview = null;
        if (btnScanImport is not null) btnScanImport.Visibility = Visibility.Collapsed;
        if (btnScanCancelPreview is not null) btnScanCancelPreview.Visibility = Visibility.Collapsed;
        if (dgScanPreview is null) return;
        dgScanPreview.ItemsSource = _scanStoredSurvey;
        txtScanFile.Text = _scanStoredSurvey.Count > 0 ? "Current imported Railhead survey" : "No CSV selected.";
        txtScanPreviewStatus.Text = _scanStoredSurvey.Count > 0 ? $"Current imported survey: {_scanStoredSurvey.Count:N0} points." : "No Railhead survey is stored. Select and import a CSV.";
        txtScanErrors.Text = string.Empty;
    }

    // Called by the existing project-workflow refresh; never writes to SQL.
    private void InvalidateScanProjectContext()
    {
        if (txtScanActiveProject is null) return;
        if (_scanGeometryProject != _activeProjectId ||
            !string.Equals(a: _scanConnection, b: _validatedDatabaseConnectionString, comparisonType: StringComparison.Ordinal))
        {
            _scanState = null;
            cmbPolygonTrack.ItemsSource = null;
            _processingTimeZoneId = string.Empty;
            cmbProcessingTracks.ItemsSource = null;
            
            
            
            
            _scanStoredSurvey = Array.Empty<ScanSurveyPreview>();
            _scanGeometryProject = null;
            _scanConnection = string.Empty;
            ClearScanPreview();
            _scanEditorDirty = false;
            dgScanTracks.ItemsSource = null;
            FillScanEditor(track: null);
            txtScanCurrentSurvey.Text = "Refresh to load this project's scan preparation.";
            txtScanStatus.Text = "Select Track Configuration, then Refresh to load the active project.";
        }
        txtScanActiveProject.Text = _activeProjectId.HasValue ? $"Active project: {_activeProjectName} (ID {_activeProjectId.Value})" : "No active project selected.";
        UpdateScanAvailability();
    }

    private void UpdateScanAvailability()
    {
        if (btnScanImport is null) return;
        bool ready = _scanState is not null && _scanGeometryProject == _activeProjectId;
        tabScanTracks.IsEnabled = !_processingOperation;
        if (tabScanTracks.IsSelected && !tabScanTracks.IsEnabled && !_scanBusy) tabScanSurvey.IsSelected = true;
        pnlScanWorkspace.IsEnabled = !_scanBusy || _processingOperation;
        tabScanSurvey.IsEnabled = !_processingOperation;
        tabScanPolygons.IsEnabled = !_processingOperation;
        if (_processingOperation) tabScanTracks.IsEnabled = false;
        
        
        
        btnScanImport.Visibility = _scanPreview is null ? Visibility.Collapsed : Visibility.Visible;
        btnScanCancelPreview.Visibility = btnScanImport.Visibility;
        btnScanImport.IsEnabled = ready && _scanPreview is { InvalidCount: 0, ValidCount: > 0 };
        btnScanNewTrack.IsEnabled = ready && _scanState!.Tracks.Count < 6;
        btnScanSaveTrack.IsEnabled = ready && (_scanEditingTrack is not null || _scanState!.Tracks.Count < 6);
        btnScanDeleteTrack.IsEnabled = ready && _scanEditingTrack is not null;
        btnScanCompute.IsEnabled = ready && _scanState!.HasSurvey && _scanEditingTrack is not null;
        
        
        
        
        cmbPolygonTrack.IsEnabled = ready && cmbPolygonTrack.Items.Count > 0;
        btnCreatePolygons.IsEnabled = ready && _scanState!.HasSurvey && cmbPolygonTrack.SelectedItem is ScanPolygonTrack;
        UpdatePolygonComments();
        UpdateProcessingAvailability(ready: ready);
        btnScanRefresh.IsEnabled = _activeProjectId.HasValue && !_scanBusy;
        barScanProgress.Visibility = _scanBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool ConfirmDiscardScanEdit()
    {
        return !_scanEditorDirty || MessageBox.Show(owner: this,
            messageBoxText: "Discard the unsaved track name or coordinate edits?", caption: "Unsaved scan track",
            button: MessageBoxButton.YesNo, icon: MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private ScanRepository CurrentScanRepository()
    {
        string validated = GetValidatedDatabaseConnectionString();
        if (_scanState is null || _scanGeometryProject != _activeProjectId ||
            !string.Equals(a: validated, b: _scanConnection, comparisonType: StringComparison.Ordinal))
            throw new InvalidOperationException(message: "The active project or connection has changed. Refresh Track Configuration before continuing.");
        return new ScanRepository(baseConnectionString: validated,
            geometryProjectId: _scanGeometryProject ?? throw new InvalidOperationException(message: "Select an active project first."));
    }

    private async Task RunScanOperationAsync(Func<Task> operation, bool returnToTracks = false)
    {
        if (_scanBusy || _busy) return;
        _scanBusy = true;
        // Prevent changes of active project or connection while an operation is in flight.
        tabApplication.IsEnabled = false;
        UpdateScanAvailability();
        try { await operation(); }
        catch (Exception ex)
        {
            txtScanStatus.Text = $"Operation failed: {ex.Message}";
            MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Scan preparation", button: MessageBoxButton.OK, icon: MessageBoxImage.Error);
        }
        finally
        {
            _scanBusy = false;
            tabApplication.IsEnabled = true;
            UpdateScanAvailability();
            if (returnToTracks)
            {
                RestoreScanTracksTab();
                // Restore again after WPF finishes owner activation and keyboard-focus routing.
                await Dispatcher.InvokeAsync(callback: RestoreScanTracksTab,
                    priority: System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
        }
    }
    private void RestoreScanTracksTab()
    {
        tabApplication.SelectedItem = tabScan;
        pnlScanWorkspace.SelectedItem = tabScanTracks;
        tabScanTracks.IsSelected = true;
        tabScanTracks.Focus();
    }
    #endregion

    #region Database Creation And Project Refresh
    private void ResetScanPreparationAfterRecreation()
    {
        _scanState = null;
        cmbPolygonTrack.ItemsSource = null;
        _processingTimeZoneId = string.Empty;
        cmbProcessingTracks.ItemsSource = null;
        
        
        
        
        _scanStoredSurvey = Array.Empty<ScanSurveyPreview>();
        _scanGeometryProject = null;
        _scanConnection = string.Empty;
        _scanEditorDirty = false;
        ClearScanPreview();
        dgScanTracks.ItemsSource = null;
        FillScanEditor(track: null);
        txtScanTrackCount.Text = "0 of 6 tracks";
        txtScanCurrentSurvey.Text = "Refresh to load this project's scan preparation.";
        txtScanStatus.Text = "DBTrackScan changed. Refresh Track Configuration before continuing.";
    }

    private async void tabScan_Selected(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(objA: e.OriginalSource, objB: tabScan) || !IsLoaded || _scanBusy) return;
        InvalidateScanProjectContext();
        if (_scanState is null && _activeProjectId.HasValue)
            await RunScanOperationAsync(operation: RefreshScanAsync);

    }

    private async void btnScanRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardScanEdit()) return;
        await RunScanOperationAsync(operation: RefreshScanAsync);
    }

    private async Task RefreshScanAsync()
    {
        bool restoreTracks = tabScanTracks.IsSelected;
        int projectId = _activeProjectId ?? throw new InvalidOperationException(message: "Select an active project on Project Selection first.");
        string connection = GetValidatedDatabaseConnectionString();
        _scanState = null;
        cmbPolygonTrack.ItemsSource = null;
        _processingTimeZoneId = string.Empty;
        cmbProcessingTracks.ItemsSource = null;
        
        
        
        
        _scanStoredSurvey = Array.Empty<ScanSurveyPreview>();
        ClearScanPreview();
        _scanEditorDirty = false;
        dgScanTracks.ItemsSource = null;
        FillScanEditor(track: null);
        txtScanStatus.Text = "Loading scan preparation...";
        ScanRepository repository = new(baseConnectionString: connection, geometryProjectId: projectId);
        ScanProjectState state = await repository.LoadAsync();
        List<ScanSurveyPreview> stored = new();
        if (state.HasSurvey)
        {
            IReadOnlyList<ScanSurveyPoint> points = await repository.LoadStartPointSurveyAsync(expected: state);
            foreach (ScanSurveyPoint point in points)
                stored.Add(item: new(Record: point.Record, PointName: point.PointName,
                    Easting: ScanCoordinates.Display(value: point.Easting), Northing: ScanCoordinates.Display(value: point.Northing),
                    Height: ScanCoordinates.Display(value: point.Height), Status: "Imported"));
        }
        _scanStoredSurvey = stored.AsReadOnly();
        ClearScanPreview();
        if (_activeProjectId != projectId || !string.Equals(a: connection, b: _validatedDatabaseConnectionString, comparisonType: StringComparison.Ordinal))
            throw new InvalidOperationException(message: "The active project changed while loading. Refresh again.");
        _scanGeometryProject = projectId;
        _scanConnection = connection;
        _scanState = state;
        
        
        
        
        ApplyPolygonOptions(options: await repository.LoadPolygonOptionsAsync());
        cmbPolygonTrack.ItemsSource = await repository.LoadPolygonTracksAsync(expected: state);
        cmbPolygonTrack.SelectedIndex = -1;
        await RefreshProcessingRailsAsync(repository: repository, state: state);
        dgScanTracks.ItemsSource = state.Tracks;
        txtScanActiveProject.Text = $"Active project: {state.ProjectName} (ID {projectId})";
        txtScanCurrentSurvey.Text = state.SurveyDescription;
        txtScanTrackCount.Text = $"{state.Tracks.Count} of 6 tracks";
        txtScanStatus.Clear();
        txtScanGuidance.Clear();
        if (_scanStoredSurvey.Count > 0)
        {
            dgScanPreview.SelectedIndex = -1;
            dgScanPreview.ScrollIntoView(item: _scanStoredSurvey[0]);
            txtScanStatus.Text = $"Database survey: {_scanStoredSurvey.Count:N0} points. First point: {_scanStoredSurvey[0].PointName}.";
        }
        if (restoreTracks && state.HasSurvey)
        {
            tabScanTracks.IsEnabled = true;
            tabScanTracks.IsSelected = true;
        }
    }
    #endregion

    #region Reference CSV Preview And Import
    private async void btnScanSelectCsv_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Title = "Select reference railhead CSV", Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(owner: this) != true) return;
        bool includesHeader = chkScanHeader.IsChecked == true;
        await RunScanOperationAsync(operation: async () =>
        {
            _ = CurrentScanRepository();
            ClearScanPreview();
            txtScanStatus.Text = "Validating the complete CSV...";
            _scanPreview = await Task.Run(function: () => ScanSurveySnapshot.Create(sourcePath: dialog.FileName, includesHeader: includesHeader));
            PresentScanPreview(sourcePath: dialog.FileName);
        });
    }

    private void PresentScanPreview(string sourcePath)
    {
        ScanSurveySnapshot snapshot = _scanPreview ?? throw new InvalidOperationException(message: "Select a CSV first.");
        dgScanPreview.ItemsSource = snapshot.Preview;
        dgScanPreview.SelectedIndex = -1;
        if (snapshot.Preview.Count > 0) dgScanPreview.ScrollIntoView(item: snapshot.Preview[0]);
        txtScanFile.Text = "CSV preview — NOT SAVED: " + sourcePath;
        txtScanPreviewStatus.Text = $"CSV preview: {snapshot.Preview.Count:N0} records; valid: {snapshot.ValidCount:N0}; invalid: {snapshot.InvalidCount:N0}. Database unchanged.";
        txtScanErrors.Text = string.Join(separator: Environment.NewLine, values: snapshot.Errors);
        txtScanStatus.Text = snapshot.InvalidCount == 0
            ? ScanImportSummary(snapshot: snapshot) + " Review the grid, then click Save to commit this survey, or Cancel to return to the stored survey."
            : "Save disabled. Review the record errors, correct the CSV and select it again. Database unchanged.";
        UpdateScanAvailability();
    }

    private void btnScanCancelPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_scanBusy) return;
        ClearScanPreview();
        UpdateScanAvailability();
        txtScanStatus.Text = "CSV review cancelled. The database survey is unchanged; the grid shows the stored survey.";
    }
    private static string ScanImportSummary(ScanSurveySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(argument: snapshot);
        string header = snapshot.IncludesHeader ? "Header row: yes — first row skipped." : "Header row: no — first row included.";
        string first = snapshot.Preview.Count > 0 ? snapshot.Preview[0].PointName : "(none)";
        return $"{header} Points to import: {snapshot.ValidCount:N0}. First point: {first}.";
    }

    private void chkScanHeader_Changed(object sender, RoutedEventArgs e)
    {
        if (dgScanPreview is null || _scanBusy) return;
        ClearScanPreview();
        txtScanStatus.Text = "Header setting changed. Select the CSV again to preview it with this setting.";
        UpdateScanAvailability();
    }

    private async void btnScanImport_Click(object sender, RoutedEventArgs e)
    {
        if (_scanPreview is null || _scanState is null || !ConfirmDiscardScanEdit()) return;
        if (_scanPreview.IncludesHeader != (chkScanHeader.IsChecked == true))
        {
            ClearScanPreview();
            txtScanStatus.Text = "The header setting changed. Select the CSV again before saving.";
            UpdateScanAvailability();
            return;
        }
        if (MessageBox.Show(owner: this,
            messageBoxText: ScanImportSummary(snapshot: _scanPreview) + "\n\n" + $"Save {_scanPreview.ValidCount:N0} reference points for '{_scanState.ProjectName}'?\n\nThis replaces ALL Railhead survey history for this active project and deletes ALL its computed-point runs. Track definitions are retained. Track points must be recomputed.",
            caption: "Save Railhead survey", button: MessageBoxButton.YesNo, icon: MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunScanOperationAsync(operation: async () =>
        {
            ScanRepository repository = CurrentScanRepository();
            ScanProjectState expected = _scanState ?? throw new InvalidOperationException(message: "Refresh Track Configuration first.");
            ScanSurveySnapshot snapshot = _scanPreview ?? throw new InvalidOperationException(message: "Preview a CSV first.");
            Progress<long> progress = new(handler: count => txtScanStatus.Text = $"Imported {count:N0} of {snapshot.ValidCount:N0} points; awaiting completion...");
            await Task.Run(function: () => repository.ImportAsync(expected: expected, snapshot: snapshot, progress: progress));
            // Clear preview immediately after commit to prevent accidental resubmission if refresh fails.
            ClearScanPreview();
            try { await RefreshScanAsync(); txtScanStatus.Text = $"Railhead survey replaced successfully. {_scanStoredSurvey.Count:N0} points stored; first point: {_scanStoredSurvey[0].PointName}. Previous surveys and all computed-point runs for this project were deleted. Recompute track points."; }
            catch (Exception ex) { txtScanStatus.Text = $"Survey committed successfully, but refresh failed: {ex.Message}. Use Refresh."; }
        });
    }
    #endregion

    #region Track Editor
    private void FillScanEditor(ScanTrack? track)
    {
        _scanFillingEditor = true;
        try
        {
            _scanEditingTrack = track;
            _scanLastSelection = track;
            txtScanTrackName.Text = track?.Name ?? string.Empty;
            rbScanGauge1435.IsChecked = track?.GaugeMillimetres == ScanEngine.StandardGaugeMillimetres;
            rbScanGauge1600.IsChecked = track is null || track.GaugeMillimetres == ScanEngine.BroadGaugeMillimetres;
            TextBox[] boxes = { txtScanLeftStartE, txtScanLeftStartN, txtScanRightStartE, txtScanRightStartN,
                txtScanLeftEndE, txtScanLeftEndN, txtScanRightEndE, txtScanRightEndN };
            decimal[] values = track is null ? Array.Empty<decimal>() : new[] { track.LeftStartE, track.LeftStartN, track.RightStartE, track.RightStartN,
                track.LeftEndE, track.LeftEndN, track.RightEndE, track.RightEndN };
            for (int index = 0; index < boxes.Length; index++)
            {
                boxes[index].Tag = track is null ? null : values[index];
                boxes[index].Text = track is null ? string.Empty : ScanCoordinates.Display(value: values[index]);
            }
            txtScanEditorTitle.Text = track is null ? "New track" : $"Edit track {track.Slot}";
            _scanEditorDirty = false;
        }
        finally { _scanFillingEditor = false; }
        UpdateScanAvailability();
    }

    private void ScanTrack_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_scanFillingEditor && sender is TextBox box) box.Tag = null;
        if (!_scanFillingEditor && IsLoaded) _scanEditorDirty = true;
    }

    private void ScanGauge_Checked(object sender, RoutedEventArgs e)
    {
        if (!_scanFillingEditor && IsLoaded) _scanEditorDirty = true;
    }

    private static decimal ReadScanCoordinate(TextBox box, string label)
    {
        // Tag retains the full value behind the three-decimal display. A real text
        // edit clears Tag so new input is parsed at its supplied precision.
        return box.Tag is decimal value ? value : ScanCoordinates.Parse(text: box.Text, label: label);
    }

    private void ScanCoordinate_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_scanFillingEditor || sender is not TextBox box) return;
        try
        {
            decimal value = ReadScanCoordinate(box: box, label: "Coordinate");
            _scanFillingEditor = true;
            box.Tag = value;
            box.Text = ScanCoordinates.Display(value: value);
        }
        catch (FormatException ex) { txtScanStatus.Text = ex.Message; }
        finally { _scanFillingEditor = false; }
    }

    private void dgScanTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_scanFillingEditor) return;
        ScanTrack? selected = dgScanTracks.SelectedItem as ScanTrack;
        if (!ConfirmDiscardScanEdit())
        {
            _scanFillingEditor = true;
            dgScanTracks.SelectedItem = _scanLastSelection;
            _scanFillingEditor = false;
            return;
        }
        FillScanEditor(track: selected);
    }

    private void btnScanNewTrack_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardScanEdit()) return;
        _scanFillingEditor = true;
        dgScanTracks.SelectedItem = null;
        _scanFillingEditor = false;
        FillScanEditor(track: null);
    }

    private void btnScanCancelTrack_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscardScanEdit()) FillScanEditor(track: dgScanTracks.SelectedItem as ScanTrack);
    }

    private ScanTrack ReadScanTrack()
    {
        return new ScanTrack(TrackId: _scanEditingTrack?.TrackId ?? 0, Slot: _scanEditingTrack?.Slot ?? 0, Name: txtScanTrackName.Text.Trim(),
            LeftStartE: ReadScanCoordinate(box: txtScanLeftStartE, label: "Secondary start Easting"),
            LeftStartN: ReadScanCoordinate(box: txtScanLeftStartN, label: "Secondary start Northing"),
            RightStartE: ReadScanCoordinate(box: txtScanRightStartE, label: "Primary start Easting"),
            RightStartN: ReadScanCoordinate(box: txtScanRightStartN, label: "Primary start Northing"),
            LeftEndE: ReadScanCoordinate(box: txtScanLeftEndE, label: "Secondary end Easting"),
            LeftEndN: ReadScanCoordinate(box: txtScanLeftEndN, label: "Secondary end Northing"),
            RightEndE: ReadScanCoordinate(box: txtScanRightEndE, label: "Primary end Easting"),
            RightEndN: ReadScanCoordinate(box: txtScanRightEndN, label: "Primary end Northing"),
            GaugeMillimetres: rbScanGauge1600.IsChecked == true ? ScanEngine.BroadGaugeMillimetres : ScanEngine.StandardGaugeMillimetres,
            PointSpacing: ScanEngine.FixedRailheadSpacingMetres);
    }

    private async void btnScanDeleteTrack_Click(object sender, RoutedEventArgs e)
    {
        ScanTrack? track = _scanEditingTrack;
        if (track is null || _scanState is null) return;
        if (MessageBox.Show(owner: this, messageBoxText: $"Delete track '{track.Name}' and its four endpoint coordinates from this project's active track list?\n\nThe saved definition remains in the audit history.",
            caption: "Delete scan track", button: MessageBoxButton.YesNo, icon: MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunScanOperationAsync(operation: async () =>
        {
            ScanRepository repository = CurrentScanRepository();
            await repository.SaveTrackAsync(expected: _scanState ?? throw new InvalidOperationException(message: "Refresh Track Configuration first."), track: track, delete: true);
            _scanEditorDirty = false;
            try { await RefreshScanAsync(); txtScanStatus.Text = $"Track '{track.Name}' deleted from the active list."; }
            catch (Exception ex) { txtScanStatus.Text = $"Track deleted successfully, but refresh failed: {ex.Message}. Use Refresh."; }
        });
    }
    #endregion

    #region Scanning Page Comments
    private async void ScanningPage_Selected(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(objA: sender, objB: e.OriginalSource) || txtScanGuidance is null) return;
        bool tracks = ReferenceEquals(objA: sender, objB: tabScanTracks);
        txtScanGuidance.Clear();
        UpdatePolygonComments();
        UpdateProcessingAvailability(ready: _scanState is not null && _scanGeometryProject == _activeProjectId);
        pnlScanSurveyComments.Visibility = ReferenceEquals(objA: sender, objB: tabScanSurvey) ? Visibility.Visible : Visibility.Collapsed;
        txtScanEndpointCsvInstructions.Visibility = tracks ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(objA: sender, objB: tabScanProcessing) && IsLoaded && !_scanBusy && !_restoringProcessingTab &&
            _scanState is not null && _scanGeometryProject == _activeProjectId)
            await RunProcessingOperationAsync(operation: RefreshProcessingPageAsync);
    }
    #endregion
}
