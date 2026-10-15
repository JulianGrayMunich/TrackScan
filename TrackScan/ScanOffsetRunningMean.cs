#region Running Mean Of Previously Computed Calibration Offsets
namespace TrackScan.Core;

public sealed record ScanOffsetMeanSnapshot(double? Mean, long Count);

public sealed class ScanOffsetRunningMean
{
    private long _count;
    private double _mean;

    // One instance per Compute Offsets operation; snapshots precede each current reading.
    public IReadOnlyList<ScanOffsetMeanSnapshot> PreviousMeans(IReadOnlyList<ScanOffsetReading> readings)
    {
        ArgumentNullException.ThrowIfNull(argument: readings);
        List<ScanOffsetMeanSnapshot> snapshots = new(capacity: readings.Count);
        foreach (ScanOffsetReading reading in readings)
        {
            snapshots.Add(item: new(Mean: _count > 0 ? _mean : null, Count: _count));
            if (reading.ScanPointCount <= 0 || reading.Warning is not null ||
                !reading.RawMean.HasValue || !double.IsFinite(d: reading.RawMean.Value) ||
                !reading.ScanOffset.HasValue || !double.IsFinite(d: reading.ScanOffset.Value)) continue;
            _count = checked(_count + 1);
            _mean += (reading.ScanOffset.Value - _mean) / _count;
        }
        return snapshots.AsReadOnly();
    }
}
#endregion
