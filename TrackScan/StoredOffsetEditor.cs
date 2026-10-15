#region Per Point Offset Selection And Mean Preview
using System.ComponentModel;
namespace TrackScan;
public sealed record StoredOffsetChoice(long PolygonId, Guid CalibrationId, int FileIndex, bool Selected);
public sealed class StoredOffsetCell : INotifyPropertyChanged
{
    public StoredOffsetObservation? Observation { get; }
    public double? Offset => Observation?.Offset;
    public bool IsValid => Offset.HasValue && double.IsFinite(d: Offset.Value) && Observation!.ScanPointCount > 0;
    private bool _selected;
    public bool Selected { get => _selected; set { bool next = value && IsValid; if (next == _selected) return; _selected = next; PropertyChanged?.Invoke(sender: this, e: new(propertyName: nameof(Selected))); } }
    public bool OriginalSelected { get; }
    public StoredOffsetCell(StoredOffsetObservation? observation, Guid? adoptedCalibrationId)
    {
        Observation = observation;
        _selected = IsValid && (observation!.SelectedForMean ?? (observation.Included && observation.CalibrationId == adoptedCalibrationId));
        OriginalSelected = _selected;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class StoredOffsetEditorRow : INotifyPropertyChanged
{
    public StoredOffsetPoint Point { get; }
    public string PointId => Point.PointId;
    public double? ExistingMean => Point.AdoptedMean;
    public double? Mean { get; private set; }
    public double? StandardError { get; private set; }
    public long PointCount { get; private set; }
    public long RejectedCount { get; private set; }
    public IReadOnlyList<StoredOffsetCell> Cells { get; }
    public string Status { get; private set; } = "Awaiting recomputation";
    private bool _recomputed;
    public bool HasChanges => Cells.Any(predicate: cell => cell.Selected != cell.OriginalSelected) ||
        (_recomputed && Cells.Any(predicate: cell => cell.Observation is not null) && Mean != ExistingMean);
    public StoredOffsetEditorRow(StoredOffsetPoint point, IReadOnlyList<StoredOffsetCell> cells) { Point = point; Cells = cells; }
    public void Invalidate() { _recomputed = false; Mean = null; Status = "Selection changed; recompute required"; PropertyChanged?.Invoke(sender: this, e: new(propertyName: string.Empty)); }
    public void Recompute()
    {
        long count = 0; double mean = 0, m2 = 0; PointCount = 0; RejectedCount = 0;
        foreach (StoredOffsetCell cell in Cells)
        {
            if (!cell.Selected || !cell.IsValid) continue;
            count++; double delta = cell.Offset!.Value - mean; mean += delta / count; m2 += delta * (cell.Offset.Value - mean);
            PointCount = checked(PointCount + cell.Observation!.ScanPointCount);
            RejectedCount = checked(RejectedCount + cell.Observation.RejectedPointCount);
        }
        Mean = count > 0 ? mean : null; StandardError = count > 1 ? Math.Sqrt(d: Math.Max(val1: 0, val2: m2) / (count - 1) / count) : null;
        _recomputed = true;
        Status = count > 0 ? $"{count} ticked offsets" : "No ticked offsets; no adopted baseline after Save";
        PropertyChanged?.Invoke(sender: this, e: new(propertyName: string.Empty));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class StoredOffsetEditor
{
    public StoredOffsetReview Source { get; }
    public IReadOnlyList<StoredOffsetEditorRow> Rows { get; }
    public IReadOnlyList<StoredOffsetObservation> Scans { get; }
    public bool IsRecomputed { get; private set; }
    public bool HasChanges => Rows.Any(predicate: row => row.HasChanges);
    public event Action? Changed;
    public StoredOffsetEditor(StoredOffsetReview source)
    {
        Source = source ?? throw new ArgumentNullException(paramName: nameof(source));
        List<StoredOffsetObservation> scans = new(); HashSet<(Guid, int)> keys = new();
        Dictionary<(long, Guid, int), StoredOffsetObservation> values = new();
        foreach (StoredOffsetObservation observation in source.Observations)
        {
            if (keys.Add(item: (observation.CalibrationId, observation.FileIndex))) scans.Add(item: observation);
            values.Add(key: (observation.PolygonId, observation.CalibrationId, observation.FileIndex), value: observation);
        }
        Scans = scans.AsReadOnly(); List<StoredOffsetEditorRow> rows = new();
        foreach (StoredOffsetPoint point in source.Points)
        {
            List<StoredOffsetCell> cells = new();
            foreach (StoredOffsetObservation scan in scans)
            {
                values.TryGetValue(key: (point.PolygonId, scan.CalibrationId, scan.FileIndex), value: out StoredOffsetObservation? observation);
                StoredOffsetCell cell = new(observation: observation, adoptedCalibrationId: point.AdoptedCalibrationId); cells.Add(item: cell);
            }
            StoredOffsetEditorRow row = new(point: point, cells: cells.AsReadOnly()); rows.Add(item: row);
            foreach (StoredOffsetCell cell in cells) cell.PropertyChanged += (_, _) => { IsRecomputed = false; row.Invalidate(); Changed?.Invoke(); };
        }
        Rows = rows.AsReadOnly();
    }
    public void Recompute() { foreach (StoredOffsetEditorRow row in Rows) row.Recompute(); IsRecomputed = true; }
    public IReadOnlyList<StoredOffsetChoice> Choices()
    {
        List<StoredOffsetChoice> choices = new();
        foreach (StoredOffsetEditorRow row in Rows) foreach (StoredOffsetCell cell in row.Cells)
            if (cell.Observation is StoredOffsetObservation observation)
                choices.Add(item: new(PolygonId: row.Point.PolygonId, CalibrationId: observation.CalibrationId, FileIndex: observation.FileIndex, Selected: cell.Selected));
        return choices.AsReadOnly();
    }
}
#endregion
