using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Stored Offset Review
using System.Windows;
using System.IO;
using Microsoft.Win32;
namespace TrackScan;
public partial class ScanOffsetReviewWindow : Window
{
    private readonly IReadOnlyList<ScanOffsetReading> _offsets;
    public ScanOffsetReviewWindow(ScanProcessingResult result)
    {
        ArgumentNullException.ThrowIfNull(argument: result);
        _offsets = result.Offsets;
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(ietfLanguageTag: "en-GB");
        gridOffsets.ItemsSource = result.Offsets;
        int saved = result.Offsets.Count(predicate: offset => offset.ScanPointCount > 0);
        txtSummary.Text = $"{saved:N0} offsets saved; {result.Offsets.Count - saved:N0} polygons had no accepted surface. " +
            "Uncorrected Scan Height is the final filtered scan mean, before adding ScanOffset. ScanOffset = reference ToR height − uncorrected scan height. " +
             $"Max/Min describe the retained set: within the selected Height Filter below the surviving maximum, inclusive. The 1.000 m reference check and rejection of 1 mm bands containing fewer than {result.RejectionCount} points are applied first. Final 1 mm bands with fewer than {result.RejectionCount} points are then rejected before computing these statistics. " +
            "Existing offsets are retained where no new value can be computed. Missing values are shown as — and exported as blank CSV fields.";
    }
    private void SaveCsv_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new() { Title = "Save railhead scan offsets", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv",
            AddExtension = true, OverwritePrompt = true, FileName = $"RailheadScanOffsets_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
        if (dialog.ShowDialog(owner: this) != true) return;
        try { ScanOffsetCsv.Export(path: dialog.FileName, offsets: _offsets); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Offset CSV export failed", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning); }
    }
}
#endregion
