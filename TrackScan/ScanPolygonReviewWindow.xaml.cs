using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Windows;
#endregion
namespace TrackScan;
public partial class ScanPolygonReviewWindow : Window
{
    #region Polygon Review
    public ScanPolygonReviewWindow(ScanPolygonSet set, bool exportCsv = true)
    {
        ArgumentNullException.ThrowIfNull(argument: set); InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(ietfLanguageTag: "en-GB");
        List<ScanPolygonVertex> vertices = new(); int corridors = 0;
        foreach (ScanPolygon polygon in set.Polygons) { vertices.AddRange(collection: polygon.Vertices); if (polygon.Kind == "Corridor") corridors++; }
        gridVertices.ItemsSource = vertices;
        btnSave.Content = exportCsv ? "Save and export CSV" : "Save";
        txtSummary.Text = set.RailheadOnly
            ? $"Railhead verification mode: {set.Polygons.Count:N0} railhead polygons; {vertices.Count:N0} vertices. No rail corridors created.\nRailhead: {set.Options.RailheadLength} × {set.Options.RailheadWidth} m; height filter: {set.Options.HeightFilterMillimetres} mm."
            : $"{corridors} rail corridors; {set.Polygons.Count - corridors:N0} railhead polygons; {vertices.Count:N0} vertices.\n" +
            $"Corridor width: {set.Options.CorridorWidth} m; collinearity: {(set.Options.CollinearityLimit == 0m ? "None" : set.Options.CollinearityLimit + " m")}; railhead: {set.Options.RailheadLength} × {set.Options.RailheadWidth} m; height filter: {set.Options.HeightFilterMillimetres} mm.";
    }
    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    #endregion
}
