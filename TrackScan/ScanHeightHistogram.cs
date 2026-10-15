#region Unfiltered Debug Height Histogram
namespace TrackScan.Core;

public sealed class ScanHeightHistogram
{
    public double BandWidthMetres { get; }
    public bool IsFiltered { get; }
    private readonly SortedDictionary<long, long> _bands = new();
    public string PointId { get; }
    public long Count { get; private set; }
    public double? Mean { get; private set; }
    public IReadOnlyDictionary<long, long> Bands => _bands;
    public ScanHeightHistogram(string pointId, bool isFiltered = false)
    { PointId = pointId; IsFiltered = isFiltered; BandWidthMetres = 0.001d; }
    public static bool IsRequested(string pointId)
    {
        string name = pointId.StartsWith(value: "Track_", comparisonType: StringComparison.OrdinalIgnoreCase) ? pointId.Substring(startIndex: 6) : pointId;
        return name.Length == 7 && name.StartsWith(value: "AB_P", comparisonType: StringComparison.OrdinalIgnoreCase)
            && int.TryParse(s: name.AsSpan(start: 4), result: out int sequence) && sequence >= 21 && sequence <= 27;
    }
    public void Observe(double height)
    {
        // Millimetre-aligned bands are [lower, upper); absorb floating-point decoding noise at boundaries.
        long band = checked((long)Math.Floor(d: height / BandWidthMetres + 1e-8d));
        _bands.TryGetValue(key: band, value: out long count);
        _bands[band] = checked(count + 1);
        Count++;
        Mean = Mean.HasValue ? Mean.Value + (height - Mean.Value) / Count : height;
    }
}
#endregion
