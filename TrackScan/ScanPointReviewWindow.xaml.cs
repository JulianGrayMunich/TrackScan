using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Windows;
#endregion

namespace TrackScan;

public partial class ScanPointReviewWindow : Window
{
    #region Review Computed Points Without Persistence
    public ScanPointReviewWindow(ScanComputationResult result, bool exportCsv = true)
    {
        ArgumentNullException.ThrowIfNull(argument: result);
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(ietfLanguageTag: "en-GB");
        txtSummary.Text = $"Track: {result.Track.Name} — {result.Points.Count:N0} computed points";
        gridPoints.ItemsSource = result.Points;
        txtSaveExplanation.Text = "Save stores these points in DBTrackScan" + (exportCsv ? " and exports a checking CSV" : string.Empty) + ". Cancel discards this computation. Display: 3 decimals; saved values: 4 decimals.";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    #endregion
}
