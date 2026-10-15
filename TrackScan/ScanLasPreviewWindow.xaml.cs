using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region LAS Coordinate Inspection Window
using System.Windows;
namespace TrackScan;

public partial class ScanLasPreviewWindow : Window
{
    public ScanLasPreviewWindow(ScanLasPreview preview)
    {
        ArgumentNullException.ThrowIfNull(argument: preview);
        InitializeComponent();
        txtSource.Text = preview.SourcePath;
        txtDetails.Text = FormattableString.Invariant(formattable: $"LAS 1.{preview.VersionMinor}, point format {preview.PointFormat}. Showing {preview.Points.Count} of {preview.PointCount:N0} point records in file order.\nScales E/N/H: {preview.ScaleE:G17}, {preview.ScaleN:G17}, {preview.ScaleH:G17}\nOffsets E/N/H: {preview.OffsetE:G17}, {preview.OffsetN:G17}, {preview.OffsetH:G17}\nCoordinates = stored integer × scale + offset. Native file axes and units; no projection or height adjustment applied. Record is the file sequence, not a survey point name.");
        gridLasPoints.ItemsSource = preview.Points;
    }
}
#endregion
