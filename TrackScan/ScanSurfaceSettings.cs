#region Supported Height Bands
namespace TrackScan.Core;
public static partial class TrackScan
{
    public const double MaximumReferenceDeviationMetres = 1.000d;
    private static void ValidateRejectionCount(int rejectionCount)
    {
        if (rejectionCount is not (10 or 25 or 50 or 75 or 100))
            throw new ArgumentOutOfRangeException(paramName: nameof(rejectionCount), message: "Select a rejection count of 10, 25, 50, 75 or 100.");
    }
    private sealed class SurfaceRange
    {
        public int RejectionCount { get; }
        public SurfaceRange(int rejectionCount) { RejectionCount = rejectionCount; }
        private const double BandWidthMetres = 0.001d;
        private readonly Dictionary<long, (long Count, double Maximum)> _bands = new();
        public IReadOnlyDictionary<long, (long Count, double Maximum)> Bands => _bands;
        private const int MinimumAdjacentBands = 3;
        private readonly HashSet<long> _continuousBands = new();
        private long _selectedFirst, _selectedLast;
        public bool HasContinuity(long band) => _continuousBands.Contains(item: band);
        public bool IsSelectedBand(long band) => Maximum.HasValue && band >= _selectedFirst && band <= _selectedLast;
        public long Count { get; private set; }
        public long ReferenceRejectedCount { get; private set; }
        public double? Maximum { get; private set; }
        private static long Band(double height) => checked((long)Math.Floor(d: height / BandWidthMetres + 1e-8d));
        public void Observe(double height, double referenceHeight)
        {
            Count++;
            if (Math.Abs(value: height - referenceHeight) > MaximumReferenceDeviationMetres + BoundaryArithmeticAllowance)
            { ReferenceRejectedCount++; return; }
            long key = Band(height: height);
            if (_bands.TryGetValue(key: key, value: out var band))
                _bands[key] = (checked(band.Count + 1), Math.Max(val1: band.Maximum, val2: height));
            else _bands.Add(key: key, value: (1, height));
        }
        public void SelectSupportedMaximum()
        {
            Maximum = null;
            _continuousBands.Clear();
            List<long> run = new();
            void FinishRun()
            {
                if (run.Count >= MinimumAdjacentBands)
                {
                    foreach (long key in run) _continuousBands.Add(item: key);
                    // Ascending runs ensure the last qualifying run is the highest group.
                    _selectedFirst = run[0]; _selectedLast = run[^1];
                    Maximum = _bands[_selectedLast].Maximum;
                }
                run.Clear();
            }
            List<long> ordered = new(collection: _bands.Keys);
            ordered.Sort();
            foreach (long key in ordered)
            {
                if (_bands[key].Count < RejectionCount) { FinishRun(); continue; }
                if (run.Count > 0 && key - run[^1] != 1) FinishRun();
                run.Add(item: key);
            }
            FinishRun();
        }
        public bool IsSupported(double height) => IsSelectedBand(band: Band(height: height));
    }
}
#endregion
