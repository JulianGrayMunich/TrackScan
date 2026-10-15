#region Review Saved Calibration Offsets
using System.Windows;
using global::TrackScan.Core;
namespace TrackScan;
public partial class MainWindow
{
    private async void ReviewStoredOffsets_Click(object sender, RoutedEventArgs e)
    {
        await RunProcessingOperationAsync(operation: async () =>
        {
            ScanRepository repository = CurrentScanRepository();
            ScanProjectState state = _scanState ?? throw new InvalidOperationException(message: "Refresh Track Configuration first.");
            StoredOffsetReview review = await repository.LoadStoredOffsetReviewAsync(expected: state);
            bool saved = new StoredOffsetReviewWindow(projectName: state.ProjectName, review: review,
                save: editor => repository.SaveStoredOffsetReviewAsync(expected: state, editor: editor)) { Owner = this }.ShowDialog() == true;
            txtScanStatus.Text = saved ? "Revised mean offsets saved for future processing. Historical readings unchanged." : $"Reviewed saved offsets for {review.Points.Count} current railhead points in '{state.ProjectName}'. No values changed.";
        });
    }
}
#endregion
