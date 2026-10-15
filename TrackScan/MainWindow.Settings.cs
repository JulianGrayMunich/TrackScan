using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Settings And Debugging Visibility
using System.Windows;
using Microsoft.Win32;
namespace TrackScan;
public partial class MainWindow
{
    #region Temporary LAS Coordinate Inspection
    private async void btnInspectLas_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Title = "Select LAS file for coordinate inspection", Filter = "LAS scan (*.las)|*.las", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(owner: this) != true) return;
        btnInspectLas.IsEnabled = false;
        try
        {
            ScanLasPreview preview = await Task.Run(function: () => ScanEngine.ReadLasCoordinatePreview(path: dialog.FileName));
            if (IsLoaded) new ScanLasPreviewWindow(preview: preview) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            if (IsLoaded) MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "LAS coordinate inspection", button: MessageBoxButton.OK, icon: MessageBoxImage.Error);
        }
        finally { btnInspectLas.IsEnabled = true; }
    }
    #endregion

    #region Scan Coverage Verification
    private CancellationTokenSource? _coverageCancellation;

    private void btnCancelCoverage_Click(object sender, RoutedEventArgs e) => _coverageCancellation?.Cancel();

    private async void btnScanCoverage_Click(object sender, RoutedEventArgs e)
    {
        if (_coverageCancellation is not null) return;
        OpenFileDialog dialog = new() { Title = "Select LAS file for scan coverage", Filter = "LAS scan (*.las)|*.las", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(owner: this) != true) return;
        using CancellationTokenSource cancellation = new();
        _coverageCancellation = cancellation;
        btnScanCoverage.IsEnabled = false;
        btnCancelCoverage.Visibility = Visibility.Visible;
        // Prevent changing project/connection while taking the current railhead snapshot.
        List<(System.Windows.Controls.TabItem Tab, bool Enabled)> previousTabs = new();
        foreach (object item in tabApplication.Items)
            if (item is System.Windows.Controls.TabItem tab && tab != tabSettings) { previousTabs.Add(item: (tab, tab.IsEnabled)); tab.IsEnabled = false; }
        try
        {
            int projectId = _activeProjectId ?? throw new InvalidOperationException(message: "Select an active project on Project Selection first.");
            string connection = GetValidatedDatabaseConnectionString();
            ScanRepository repository = new(baseConnectionString: connection, geometryProjectId: projectId);
            txtCoverageStatus.Text = "Loading current saved railhead points...";
            ScanProjectState state = await repository.LoadAsync();
            var rails = await repository.LoadPolygonRailsAsync(expected: state);
            if (rails.Count == 0) throw new InvalidOperationException(message: "No current saved railhead points exist for the active project. Compute and save Railhead Points first.");
            Progress<string> progress = new(handler: message => txtCoverageStatus.Text = message);
            ScanCoverageResult result = await Task.Run(function: () => ScanEngine.CheckLasCoverage(path: dialog.FileName, rails: rails,
                progress: progress, cancellationToken: cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            // Reject stale preparation rather than display a mixture of project revisions.
            ScanProjectState latest = await repository.LoadAsync();
            if (latest.Revision != state.Revision || latest.DatabaseIdentity != state.DatabaseIdentity)
                throw new InvalidOperationException(message: "Railhead preparation changed during the check. Run the coverage check again.");
            txtCoverageStatus.Text = "Coverage check complete. Inside/Outside refers to the 0.5 m plan footprint, not usable railhead returns.";
            if (IsLoaded) new ScanCoverageWindow(result: result) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException) { txtCoverageStatus.Text = "Coverage check cancelled. No data saved."; }
        catch (Exception ex)
        {
            txtCoverageStatus.Text = ex.Message;
            if (IsLoaded) MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Scan coverage", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning);
        }
        finally
        {
            _coverageCancellation = null;
            btnScanCoverage.IsEnabled = true;
            btnCancelCoverage.Visibility = Visibility.Collapsed;
            foreach (var previous in previousTabs) previous.Tab.IsEnabled = previous.Enabled;
        }
    }
    #endregion


    private void chkDebugging_Changed(object sender, RoutedEventArgs e)
    {
        if (pnlDebugOptions is null) return;
        pnlDebugOptions.Visibility = chkDebugging.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdatePolygonComments();
    }
}
#endregion
