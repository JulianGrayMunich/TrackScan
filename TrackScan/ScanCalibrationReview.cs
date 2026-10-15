using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Calibration Review Models And CSV
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
namespace TrackScan;

public sealed class ScanCalibrationCell : INotifyPropertyChanged
{
    public ScanOffsetReading Observation { get; }
    public double? Offset => IsValid ? Observation.ScanOffset : null;
    public bool IsValid => Observation.ScanPointCount > 0 && Observation.ScanOffset.HasValue && Observation.RawMean.HasValue;
    private bool _included;
    public bool Included
    {
        get => _included;
        set { bool next = value && IsValid; if (_included == next) return; _included = next; PropertyChanged?.Invoke(sender: this, e: new(nameof(Included))); }
    }
    public string Status => IsValid ? "Untick to exclude this observation from the mean." : Observation.Warning ?? "No valid scan height.";
    public ScanCalibrationCell(ScanOffsetReading observation) { Observation = observation; _included = IsValid; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class ScanCalibrationRow : INotifyPropertyChanged
{
    public long PolygonId { get; }
    public string PointId { get; }
    public decimal ReferenceHeight { get; }
    public double? ExistingOffset { get; }
    public IReadOnlyList<ScanCalibrationCell> Cells { get; }
    public double? Mean { get; private set; }
    public double? Minimum { get; private set; }
    public double? Maximum { get; private set; }
    public double? StandardDeviation { get; private set; }
    public double? Difference => Mean.HasValue && ExistingOffset.HasValue ? Mean.Value - ExistingOffset.Value : null;
    public string Status => Mean.HasValue ? (Cells.Count(predicate: cell => cell.Included) == 1 ? "One observation; SD unavailable" : "Ready to adopt") : ExistingOffset.HasValue ? "No included observations; existing offset retained" : "No included observations; monitoring offset unavailable";
    public ScanCalibrationRow(ScanProcessingHead head, IReadOnlyList<ScanCalibrationCell> cells)
    {
        PolygonId = head.PolygonId; PointId = head.PointId; ReferenceHeight = head.ReferenceHeight; ExistingOffset = head.ScanOffset; Cells = cells;
        foreach (ScanCalibrationCell cell in cells) cell.PropertyChanged += (_, _) => Recalculate();
        Recalculate();
    }
    public void Recalculate()
    {
        long count = 0; double mean = 0, m2 = 0; Minimum = null; Maximum = null;
        foreach (ScanCalibrationCell cell in Cells)
        {
            if (!cell.Included || !cell.Offset.HasValue) continue;
            double value = cell.Offset.Value; count++; double delta = value - mean; mean += delta / count; m2 += delta * (value - mean);
            Minimum = Minimum.HasValue ? Math.Min(val1: Minimum.Value, val2: value) : value;
            Maximum = Maximum.HasValue ? Math.Max(val1: Maximum.Value, val2: value) : value;
        }
        Mean = count > 0 ? mean : null;
        StandardDeviation = count > 1 ? Math.Sqrt(d: Math.Max(val1: 0d, val2: m2) / (count - 1)) : null;
        PropertyChanged?.Invoke(sender: this, e: new(propertyName: string.Empty));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class ScanCalibrationReview
{
    public Guid BatchId { get; } = Guid.NewGuid();
    public IReadOnlyList<ScanProcessingResult> Results { get; }
    public IReadOnlyList<ScanCalibrationRow> Rows { get; }
    public ScanCalibrationReview(IReadOnlyList<ScanProcessingResult> results)
    {
        ArgumentNullException.ThrowIfNull(argument: results);
        if (results.Count == 0) throw new InvalidOperationException(message: "No calibration scans to review.");
        Results = results.ToArray();
        HashSet<string> hashes = new(comparer: StringComparer.OrdinalIgnoreCase);
        Dictionary<long, ScanProcessingHead> heads = new();
        foreach (ScanProcessingRail rail in results[0].Rails)
            foreach (ScanProcessingHead head in rail.Heads) heads.Add(key: head.PolygonId, value: head);
        List<Dictionary<long, ScanOffsetReading>> observations = new();
        foreach (ScanProcessingResult result in Results)
        {
            if (!result.CalculateOffsets || !hashes.Add(item: result.Sha256)) throw new InvalidOperationException(message: "Calibration requires distinct LAS files. The same file content cannot be counted twice.");
            Dictionary<long, ScanOffsetReading> map = new();
            foreach (ScanOffsetReading offset in result.Offsets)
            {
                if (!heads.TryGetValue(key: offset.PolygonId, value: out ScanProcessingHead? head) || offset.PointId != head.PointId || offset.ReferenceHeight != head.ReferenceHeight)
                    throw new InvalidOperationException(message: "Calibration scans do not share the same railhead reference.");
                if (offset.ScanPointCount > 0 && (!offset.ScanOffset.HasValue || !offset.RawMean.HasValue || !double.IsFinite(d: offset.ScanOffset.Value) || !double.IsFinite(d: offset.RawMean.Value) || Math.Abs(value: (double)offset.ReferenceHeight - offset.RawMean.Value - offset.ScanOffset.Value) > 1e-9))
                    throw new InvalidOperationException(message: "Invalid calibration observation.");
                map.Add(key: offset.PolygonId, value: offset);
            }
            if (map.Count != heads.Count) throw new InvalidOperationException(message: "Calibration point sets differ between scans.");
            observations.Add(item: map);
        }
        List<ScanCalibrationRow> rows = new();
        foreach (ScanProcessingHead head in heads.Values)
        {
            List<ScanCalibrationCell> cells = new();
            foreach (var map in observations) cells.Add(item: new(observation: map[head.PolygonId]));
            rows.Add(item: new(head: head, cells: cells.AsReadOnly()));
        }
        Rows = rows.AsReadOnly();
    }
    public void IncludeFile(int index, bool include)
    { foreach (ScanCalibrationRow row in Rows) row.Cells[index].Included = include; }
    public void Export(string path)
    {
        static string Quote(string value) => "\"" + value.Replace(oldValue: "\"", newValue: "\"\"") + "\"";
        static string Number(double? value) => value?.ToString(format: "F4", provider: CultureInfo.InvariantCulture) ?? string.Empty;
        string full = Path.GetFullPath(path: path); string temporary = full + "." + Guid.NewGuid().ToString(format: "N") + ".tmp";
        try
        {
            using (StreamWriter writer = new(path: temporary, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                List<string> headers = new() { "Railhead point" };
                for (int i = 0; i < Results.Count; i++) { headers.Add(item: $"Scan {i + 1}: {Results[i].SourcePath} ({Results[i].Readings[0].UtcTimestamp:yyyy-MM-dd HH:mm:ss} UTC) offset (m)"); headers.Add(item: $"Scan {i + 1} included"); }
                headers.AddRange(collection: new[] { "Mean offset (m)", "Min (m)", "Max (m)", "Standard deviation (m)", "Existing offset (m)", "Difference (m)", "Status" });
                writer.WriteLine(value: string.Join(separator: ",", values: headers.Select(selector: Quote)));
                foreach (ScanCalibrationRow row in Rows)
                {
                    List<string> fields = new() { Quote(value: row.PointId) };
                    foreach (ScanCalibrationCell cell in row.Cells) { fields.Add(item: Number(value: cell.Offset)); fields.Add(item: cell.Included ? "Yes" : "No"); }
                    fields.AddRange(collection: new[] { Number(value: row.Mean), Number(value: row.Minimum), Number(value: row.Maximum), Number(value: row.StandardDeviation), Number(value: row.ExistingOffset), Number(value: row.Difference), Quote(value: row.Status) });
                    writer.WriteLine(value: string.Join(separator: ",", values: fields));
                }
            }
            File.Move(sourceFileName: temporary, destFileName: full, overwrite: true);
        }
        finally { if (File.Exists(path: temporary)) File.Delete(path: temporary); }
    }
}
#endregion
