using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Scan Coverage Review
using System.Windows;
using System.IO;
using Microsoft.Win32;
namespace TrackScan;
public partial class ScanCoverageWindow : Window
{
    private readonly ScanCoverageResult _result;
    public ScanCoverageWindow(ScanCoverageResult result)
    {
        ArgumentNullException.ThrowIfNull(argument: result);
        _result = result;
        InitializeComponent();
        txtSource.Text = result.SourcePath;
        gridCoverage.ItemsSource = result.Points;
        List<ScanFenceVertex> vertices = new();
        foreach (var ring in result.Rings) vertices.AddRange(collection: ring);
        gridFence.ItemsSource = vertices;
    }

    #region Export Outer Boundary
    private void btnExportBoundary_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Export scan outer boundary CSV", Filter = "CSV files (*.csv)|*.csv",
            DefaultExt = ".csv", AddExtension = true, OverwritePrompt = true,
            FileName = $"{Path.GetFileNameWithoutExtension(path: _result.SourcePath)}_ScanBoundary_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog(owner: this) != true) return;
        try
        {
            ScanEngine.ExportCoverageBoundaryCsv(path: dialog.FileName, result: _result);
            txtExportStatus.Text = "Outer boundary CSV saved: " + dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Boundary CSV export", button: MessageBoxButton.OK, icon: MessageBoxImage.Error);
        }
    }
    #endregion
}
#endregion
