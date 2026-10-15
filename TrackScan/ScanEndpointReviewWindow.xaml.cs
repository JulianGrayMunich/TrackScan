using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Windows;
#endregion

namespace TrackScan;

public partial class ScanEndpointReviewWindow : Window
{
    #region Coordinate Comparison And Explicit Acceptance
    public sealed record CoordinateRow(string Point, string Coordinate, string Original, string New, string Difference);
    public IReadOnlyList<CoordinateRow> Rows { get; }

    public ScanEndpointReviewWindow(ScanTrack original, ScanTrack proposed)
    {
        ArgumentNullException.ThrowIfNull(argument: original);
        ArgumentNullException.ThrowIfNull(argument: proposed);
        InitializeComponent();
        txtTrack.Text = "Track: " + proposed.Name;
        List<CoordinateRow> rows = new();
        AddPair(rows: rows, point: "Primary start", originalE: original.RightStartE, originalN: original.RightStartN, newE: proposed.RightStartE, newN: proposed.RightStartN);
        AddPair(rows: rows, point: "Secondary start", originalE: original.LeftStartE, originalN: original.LeftStartN, newE: proposed.LeftStartE, newN: proposed.LeftStartN);
        AddPair(rows: rows, point: "Primary end", originalE: original.RightEndE, originalN: original.RightEndN, newE: proposed.RightEndE, newN: proposed.RightEndN);
        AddPair(rows: rows, point: "Secondary end", originalE: original.LeftEndE, originalN: original.LeftEndN, newE: proposed.LeftEndE, newN: proposed.LeftEndN);
        Rows = rows.AsReadOnly();
        gridCoordinates.ItemsSource = Rows;
    }

    private static void AddPair(List<CoordinateRow> rows, string point, decimal originalE, decimal originalN, decimal newE, decimal newN)
    {
        rows.Add(item: new(Point: point, Coordinate: "Easting", Original: ScanCoordinates.Display(value: originalE),
            New: ScanCoordinates.Display(value: newE), Difference: ScanCoordinates.Display(value: newE - originalE)));
        rows.Add(item: new(Point: point, Coordinate: "Northing", Original: ScanCoordinates.Display(value: originalN),
            New: ScanCoordinates.Display(value: newN), Difference: ScanCoordinates.Display(value: newN - originalN)));
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    #endregion
}
