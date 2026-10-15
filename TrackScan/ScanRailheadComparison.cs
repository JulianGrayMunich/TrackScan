using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Survey Comparison And CSV Export
using System.Globalization;
using System.IO;
using System.Text;
namespace TrackScan;

public sealed record ScanRailheadComparisonRow(string RailheadName, decimal RailheadE, decimal RailheadN,
    decimal RailheadHeight, string ToRName, decimal ToRE, decimal ToRN, decimal ToRHeight, double Ds, decimal Dh);

public static class ScanRailheadComparison
{
    #region Independent Nearest Survey Check
    public static IReadOnlyList<ScanRailheadComparisonRow> Create(IReadOnlyList<ScanComputedPoint> points,
        IReadOnlyList<ScanSurveyPoint> survey)
    {
        ArgumentNullException.ThrowIfNull(argument: points);
        ArgumentNullException.ThrowIfNull(argument: survey);
        if (survey.Count == 0) throw new InvalidOperationException(message: "No imported ToR survey points are available for comparison.");
        List<ScanSurveyPoint> ordered = new(collection: survey);
        ordered.Sort(comparison: (a, b) => { int order = a.Easting.CompareTo(value: b.Easting); return order != 0 ? order : a.Record.CompareTo(value: b.Record); });
        List<ScanRailheadComparisonRow> rows = new(capacity: points.Count);
        foreach (ScanComputedPoint point in points)
        {
            int low = 0, high = ordered.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (ordered[middle].Easting < point.Easting) low = middle + 1; else high = middle;
            }
            ScanSurveyPoint? nearest = null;
            double best = double.PositiveInfinity;
            // Search both sides of the Easting insertion point. Easting alone supplies
            // a lower distance bound; no radius or rail classification restricts this diagnostic.
            foreach (int direction in new[] { -1, 1 })
                for (int i = direction < 0 ? low - 1 : low; i >= 0 && i < ordered.Count; i += direction)
                {
                    ScanSurveyPoint candidate = ordered[i];
                    double de = (double)(candidate.Easting - point.Easting);
                    if (de * de > best) break;
                    double dn = (double)(candidate.Northing - point.Northing);
                    double squared = de * de + dn * dn;
                    if (squared < best || (squared == best && (nearest is null || candidate.Record < nearest.Record)))
                    { best = squared; nearest = candidate; }
                }
            ScanSurveyPoint match = nearest ?? throw new InvalidOperationException(message: "No nearest ToR point was found.");
            rows.Add(item: new(RailheadName: point.PointName, RailheadE: point.Easting, RailheadN: point.Northing,
                RailheadHeight: point.Height, ToRName: match.PointName, ToRE: match.Easting, ToRN: match.Northing,
                ToRHeight: match.Height, Ds: Math.Sqrt(d: best), Dh: point.Height - match.Height));
        }
        return rows.AsReadOnly();
    }
    #endregion

    #region Atomic Optional CSV Export
    public static void Export(string path, IReadOnlyList<ScanRailheadComparisonRow> rows)
    {
        ArgumentNullException.ThrowIfNull(argument: rows);
        string destination = Path.GetFullPath(path: path);
        string directory = Path.GetDirectoryName(path: destination) ?? throw new IOException(message: "Select a CSV destination.");
        string temporary = Path.Combine(path1: directory, path2: $".railhead-comparison-{Guid.NewGuid():N}.tmp");
        try
        {
            using (StreamWriter writer = new(path: temporary, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine(value: "Railhead Point name,Railhead E,Railhead N,Railhead Ht,ToR_name,ToR E,ToR N,ToR Ht,ds,dh");
                foreach (ScanRailheadComparisonRow row in rows)
                    writer.WriteLine(value: string.Join(separator: ",", value: new[] {
                        Quote(text: row.RailheadName), Number(value: row.RailheadE), Number(value: row.RailheadN), Number(value: row.RailheadHeight),
                        Quote(text: row.ToRName), Number(value: row.ToRE), Number(value: row.ToRN), Number(value: row.ToRHeight),
                        row.Ds.ToString(format: "F4", provider: CultureInfo.InvariantCulture), Number(value: row.Dh) }));
            }
            File.Move(sourceFileName: temporary, destFileName: destination, overwrite: true);
        }
        finally { if (File.Exists(path: temporary)) File.Delete(path: temporary); }
    }
    private static string Quote(string text) => "\"" + text.Replace(oldValue: "\"", newValue: "\"\"") + "\"";
    private static string Number(decimal value) => value.ToString(format: "F4", provider: CultureInfo.InvariantCulture);
    #endregion
}
#endregion
