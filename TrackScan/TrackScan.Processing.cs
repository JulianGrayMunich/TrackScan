#region Processing Models
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
namespace TrackScan.Core;

public sealed record ScanProcessingHead(int Sequence, string PointId, decimal ReferenceHeight, ScanPolygon Polygon)
{
    public long PolygonId { get; init; }
    public double? ScanOffset { get; init; }
    public long OffsetVersion { get; init; }
}
public sealed record ScanProcessingRail(long BatchId, long RunId, int TrackId, string TrackName, string Rail,
    int HeightFilterMillimetres, ScanPolygon? Corridor, IReadOnlyList<ScanProcessingHead> Heads)
{
    public string DisplayName => TrackName + (Rail == "R" ? " — Primary" : " — Secondary");
}
public sealed record ScanEpochReading(long RunId, string Rail, int Sequence, string PointId,
    DateTime UtcTimestamp, double? ScanHt, long ScanPointCount, double? StandardError, long RejectedPointCount)
{
    public double? ScanOffsetUsed { get; init; }
}
public sealed record ScanOffsetReading(long PolygonId, string PointId, decimal ReferenceHeight, double? ScanOffset,
    long ScanPointCount, double? StandardError, double? RawMean, long RejectedPointCount)
{
    public double? MaximumHeight { get; init; }
    public double? MinimumHeight { get; init; }
    public long ReferenceRejectedPointCount { get; init; }
    public string? Warning { get; init; }
    public string Status => Warning is not null ? Warning + (ScanOffset.HasValue ? " Earlier offset retained." : " No stored offset.")
        : ScanPointCount > 0 ? "Offset stored" + (ReferenceRejectedPointCount > 0 ? $"; {ReferenceRejectedPointCount:N0} points outside 1 m rejected" : string.Empty)
        : ScanOffset.HasValue ? "No accepted points; earlier offset retained" : "No accepted points; no stored offset";
}
public sealed record ScanCorridorSelection(string TrackName, string Rail, long ScanPointCount);
public sealed record ScanProcessingResult(string SourcePath, string Sha256, long FileBytes, long LasPointCount,
    IReadOnlyList<ScanProcessingRail> Rails, IReadOnlyList<ScanEpochReading> Readings)
{
    public IReadOnlyList<ScanHeightHistogram> DebugHistograms { get; init; } = Array.Empty<ScanHeightHistogram>();
    public IReadOnlyList<string> ChartDiagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TemporaryDiagnostics { get; init; } = Array.Empty<string>();
    public int RejectionCount { get; init; } = 50;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ScanCorridorSelection> CorridorSelections { get; init; } = Array.Empty<ScanCorridorSelection>();
    public bool CalculateOffsets { get; init; }
    public IReadOnlyList<ScanOffsetReading> Offsets { get; init; } = Array.Empty<ScanOffsetReading>();
    public string TimeZoneId { get; init; } = string.Empty;
    public string TimestampSource { get; init; } = TrackScan.FilenameTimestampSource;
}
#endregion

public static partial class TrackScan
{
    #region Offset Readiness Validation
    public static string? MissingScanCorridorMessage(IReadOnlyList<ScanProcessingRail> rails)
    {
        foreach (ScanProcessingRail rail in rails)
            if (rail.Corridor is null)
                return $"No saved rail corridor exists for '{rail.DisplayName}'. Open Track Configuration / Polygons, select the track, then Compute Polygons and save both polygon sets. Recalculate offsets for the new polygons using Calculate Offset. No readings saved.";
        return null;
    }

    public static string? MissingScanOffsetMessage(IReadOnlyList<ScanProcessingRail> rails, bool calculateOffsets)
    {
        if (calculateOffsets) return null;
        foreach (ScanProcessingRail rail in rails)
            foreach (ScanProcessingHead head in rail.Heads)
                if (!head.ScanOffset.HasValue || !double.IsFinite(d: head.ScanOffset.Value))
                    return $"No stored ScanOffset exists for '{head.PointId}'. Tick Calculate Offset on the Processing tab and process a calibration scan first. No readings saved.";
        return null;
    }
    #endregion

    #region Single LAS Pass And Reduced Height Filtering
    public static DateTime RoundScanMinute(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException(message: "The scan timestamp must be UTC.");
        long ticks = checked((utc.Ticks + TimeSpan.TicksPerMinute / 2) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute);
        return new DateTime(ticks: ticks, kind: DateTimeKind.Utc);
    }

    public static ScanProcessingResult ProcessLas(string path, DateTime utcTimestamp,
        IReadOnlyList<ScanProcessingRail> rails, IProgress<string>? progress = null, bool calculateOffsets = false,
        IProgress<ScanEpochReading>? readingProgress = null, CancellationToken cancellationToken = default, bool collectDebugHistograms = false, int rejectionCount = 50)
    {
        ValidateRejectionCount(rejectionCount: rejectionCount);
        if (rails.Count == 0) throw new InvalidOperationException(message: "Select at least one rail with saved polygons.");
        cancellationToken.ThrowIfCancellationRequested();
        string? offsetWarning = MissingScanCorridorMessage(rails: rails) ?? MissingScanOffsetMessage(rails: rails, calculateOffsets: calculateOffsets);
        if (offsetWarning is not null) throw new InvalidOperationException(message: offsetWarning);
        DateTime epoch = RoundScanSecond(utc: utcTimestamp);
        List<ProcessingTarget> targets = new();
        Dictionary<(long E, long N), List<ProcessingCorridor>> corridorCells = new();
        List<ProcessingCorridor> corridors = new();
        foreach (ScanProcessingRail rail in rails)
        {
            if (rail.Heads.Count == 0) throw new InvalidOperationException(message: "The selected rail has no railhead polygons.");
            cancellationToken.ThrowIfCancellationRequested();
            PreparedBoundary boundary = new(polygon: rail.Corridor ?? throw new InvalidOperationException(message: "This processing mode requires a saved corridor."));
            ProcessingCorridor corridor = new(rail: rail, boundary: boundary);
            corridors.Add(item: corridor);
            long minE = Cell(value: boundary.MinE - CoordinateBoundaryAllowance), maxE = Cell(value: boundary.MaxE + CoordinateBoundaryAllowance);
            long minN = Cell(value: boundary.MinN - CoordinateBoundaryAllowance), maxN = Cell(value: boundary.MaxN + CoordinateBoundaryAllowance);
            if ((double)(maxE - minE + 1) * (maxN - minN + 1) > 1_000_000d)
                throw new InvalidDataException(message: "A rail corridor exceeds the supported search area. Check the saved polygon coordinates.");
            for (long e = minE; e <= maxE; e++)
                for (long n = minN; n <= maxN; n++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!boundary.IntersectsCell(e: e, n: n, tolerance: CoordinateBoundaryAllowance)) continue;
                    if (!corridorCells.TryGetValue(key: (e, n), value: out List<ProcessingCorridor>? bucket))
                    { bucket = new(); corridorCells.Add(key: (e, n), value: bucket); }
                    bucket.Add(item: corridor);
                }
            foreach (ScanProcessingHead head in rail.Heads)
            {
                ProcessingTarget target = new(rail: rail, head: head, corridor: boundary, index: targets.Count, rejectionCount: rejectionCount);
                if (collectDebugHistograms) target.DebugHistogram = new(pointId: head.PointId);
                targets.Add(item: target);
                for (long e = Cell(value: target.Boundary.MinE - BoundaryArithmeticAllowance); e <= Cell(value: target.Boundary.MaxE + BoundaryArithmeticAllowance); e++)
                    for (long n = Cell(value: target.Boundary.MinN - BoundaryArithmeticAllowance); n <= Cell(value: target.Boundary.MaxN + BoundaryArithmeticAllowance); n++)
                    {
                        if (!corridor.HeadCells.TryGetValue(key: (e, n), value: out List<ProcessingTarget>? bucket))
                        { bucket = new(); corridor.HeadCells.Add(key: (e, n), value: bucket); }
                        bucket.Add(item: target);
                    }
            }
        }
        // Only matching heights are spooled. This keeps memory bounded without rereading the full LAS file.
        string spoolPath = Path.Combine(path1: Path.GetTempPath(), path2: "GNA-ScanHeights-" + Guid.NewGuid().ToString(format: "N") + ".tmp");
        using FileStream spool = new(path: spoolPath, mode: FileMode.CreateNew, access: FileAccess.ReadWrite, share: FileShare.None,
            bufferSize: 1024 * 1024, options: FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        using BinaryWriter spoolWriter = new(output: spool, encoding: System.Text.Encoding.UTF8, leaveOpen: true);
        string fullPath = Path.GetFullPath(path: path);
        using FileStream stream = new(path: fullPath, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read,
            bufferSize: 1024 * 1024, options: FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);
        byte[] header = new byte[375];
        ReadHashed(stream: stream, hash: hash, buffer: header.AsSpan(start: 0, length: 227));
        if (header[0] != 'L' || header[1] != 'A' || header[2] != 'S' || header[3] != 'F' || header[24] != 1 || header[25] > 4)
            throw new InvalidDataException(message: "Select an uncompressed LAS file, version 1.0 to 1.4.");
        int minor = header[25], format = header[104];
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(source: header.AsSpan(start: 94));
        uint pointOffset = BinaryPrimitives.ReadUInt32LittleEndian(source: header.AsSpan(start: 96));
        int recordSize = BinaryPrimitives.ReadUInt16LittleEndian(source: header.AsSpan(start: 105));
        int[] minimumLengths = { 20, 28, 26, 34, 57, 63, 30, 36, 38, 59, 67 };
        if (format > 10 || (format > 5 && minor < 4) || recordSize < minimumLengths[format] ||
            headerSize < (minor == 4 ? 375 : minor == 3 ? 235 : 227) || pointOffset < headerSize)
            throw new InvalidDataException(message: "Unsupported, compressed or malformed LAS point format/header.");
        if (minor == 4) ReadHashed(stream: stream, hash: hash, buffer: header.AsSpan(start: 227, length: 148));
        ulong count = minor == 4 ? BinaryPrimitives.ReadUInt64LittleEndian(source: header.AsSpan(start: 247)) : 0;
        if (count == 0) count = BinaryPrimitives.ReadUInt32LittleEndian(source: header.AsSpan(start: 107));
        if (count == 0 || pointOffset > stream.Length || count > (ulong)((stream.Length - pointOffset) / recordSize))
            throw new InvalidDataException(message: "LAS point data is empty or truncated.");
        double scaleE = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 131));
        double scaleN = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 139));
        double scaleH = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 147));
        double offsetE = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 155));
        double offsetN = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 163));
        double offsetH = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 171));
        foreach (double scale in new[] { scaleE, scaleN, scaleH })
            if (!double.IsFinite(d: scale) || scale <= 0) throw new InvalidDataException(message: "Invalid LAS coordinate scale.");
        foreach (double offset in new[] { offsetE, offsetN, offsetH })
            if (!double.IsFinite(d: offset)) throw new InvalidDataException(message: "Invalid LAS coordinate offset.");
        int recordsPerBlock = Math.Min(val1: 16384, val2: 1024 * 1024 / recordSize);
        byte[] buffer = new byte[Math.Max(val1: 1024 * 1024, val2: recordSize * recordsPerBlock)];
        while (stream.Position < pointOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadHashed(stream: stream, hash: hash, buffer: buffer.AsSpan(start: 0, length: (int)Math.Min(val1: buffer.Length, val2: pointOffset - stream.Position)));
        }
        long processed = 0, total = checked((long)count);
        int lastPercent = -1;
        while (processed < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int records = (int)Math.Min(val1: recordsPerBlock, val2: total - processed);
            ReadHashed(stream: stream, hash: hash, buffer: buffer.AsSpan(start: 0, length: records * recordSize));
            for (int i = 0; i < records; i++)
            {
                ReadOnlySpan<byte> record = buffer.AsSpan(start: i * recordSize, length: recordSize);
                double e = BinaryPrimitives.ReadInt32LittleEndian(source: record) * scaleE + offsetE;
                double n = BinaryPrimitives.ReadInt32LittleEndian(source: record.Slice(start: 4)) * scaleN + offsetN;
                double height = BinaryPrimitives.ReadInt32LittleEndian(source: record.Slice(start: 8)) * scaleH + offsetH;
                if (!double.IsFinite(d: e) || !double.IsFinite(d: n) || !double.IsFinite(d: height))
                    throw new InvalidDataException(message: "LAS contains an invalid coordinate.");
                var cell = (Cell(value: e), Cell(value: n));
                if (!corridorCells.TryGetValue(key: cell, value: out List<ProcessingCorridor>? candidates)) continue;
                foreach (ProcessingCorridor corridor in candidates)
                {
                    // First reduction is exclusively the saved rail corridor, independent of railhead locations or scan footprint.
                    if (!corridor.Boundary.Contains(e: e, n: n, tolerance: CoordinateBoundaryAllowance)) continue;
                    corridor.PointCount++;
                    if (!corridor.HeadCells.TryGetValue(key: cell, value: out List<ProcessingTarget>? heads)) continue;
                    foreach (ProcessingTarget target in heads)
                    {
                        if (!target.Boundary.Contains(e: e, n: n, tolerance: 0d)) continue;
                        target.ObserveInitial(height: height);
                        spoolWriter.Write(value: target.Index);
                        spoolWriter.Write(value: height);
                    }
                }
            }
            processed += records;
            int percent = (int)(processed * 100d / total);
            if (percent != lastPercent) { lastPercent = percent; progress?.Report(value: $"Reading LAS: {percent}% ({processed:N0} of {total:N0} points)."); }
        }
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadHashed(stream: stream, hash: hash, buffer: buffer.AsSpan(start: 0, length: (int)Math.Min(val1: buffer.Length, val2: stream.Length - stream.Position)));
        }
        progress?.Report(value: "Checking three adjacent supported 1 mm bands and selecting the highest qualifying group...");
        spoolWriter.Flush();
        spool.Position = 0;
        using BinaryReader spoolReader = new(input: spool, encoding: System.Text.Encoding.UTF8, leaveOpen: true);
        foreach (ProcessingTarget target in targets) target.PrepareSurface();
        for (int pass = 0; pass < 2 && targets.Any(predicate: target => target.NeedsPass); pass++)
        {
            foreach (ProcessingTarget target in targets) target.BeginPass();
            spool.Position = 0;
            while (spool.Position < spool.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = spoolReader.ReadInt32();
                targets[index].Accumulate(height: spoolReader.ReadDouble());
            }
            foreach (ProcessingTarget target in targets) target.EndPass();
        }
        List<ScanEpochReading> readings = new();
        List<ScanOffsetReading> offsets = new();
        foreach (ProcessingTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanEpochReading reading = target.Reading(epoch: epoch, calculateOffset: calculateOffsets);
            readings.Add(item: reading);
            readingProgress?.Report(value: reading);
            if (calculateOffsets) offsets.Add(item: target.OffsetReading());
        }
        List<ScanCorridorSelection> selections = new();
        foreach (ProcessingCorridor corridor in corridors)
            selections.Add(item: new(TrackName: corridor.Rail.TrackName, Rail: corridor.Rail.Rail, ScanPointCount: corridor.PointCount));
        return new(SourcePath: fullPath, Sha256: Convert.ToHexString(inArray: hash.GetHashAndReset()), FileBytes: stream.Length,
            LasPointCount: total, Rails: rails, Readings: readings.AsReadOnly())
        { ChartDiagnostics = collectDebugHistograms ? targets.Select(selector: target => target.DescribeDecisions(sourcePath: fullPath, total: total, corridorCount: corridors.First(predicate: corridor => ReferenceEquals(objA: corridor.Boundary, objB: target.Corridor)).PointCount)).ToArray() : Array.Empty<string>(), TemporaryDiagnostics = targets.Where(predicate: target => target.DiagnosticHistogram is not null).Select(selector: target => target.DescribeDecisions(sourcePath: fullPath, total: total, corridorCount: corridors.First(predicate: corridor => ReferenceEquals(objA: corridor.Boundary, objB: target.Corridor)).PointCount)).ToArray(), RejectionCount = rejectionCount, DebugHistograms = targets.Where(predicate: target => target.DebugHistogram is not null).SelectMany(selector: target => new[] { target.DebugHistogram!, target.FilteredHistogram! }).ToArray(), Warnings = targets.Where(predicate: target => target.Warning is not null).Select(selector: target => target.PointId + ": " + target.Warning).ToArray(), CalculateOffsets = calculateOffsets, Offsets = offsets.AsReadOnly(), CorridorSelections = selections.AsReadOnly() };
    }

    private static void ReadHashed(Stream stream, IncrementalHash hash, Span<byte> buffer)
    {
        stream.ReadExactly(buffer: buffer);
        hash.AppendData(data: buffer);
    }

    // One-metre cells keep the search independent of the number of distant railhead points.
    private static long Cell(double value) => checked((long)Math.Floor(d: value));
    private const double CoordinateBoundaryAllowance = 0.00015d;
    private const double BoundaryArithmeticAllowance = 1e-9d;
    #endregion

    #region Prepared Boundaries And Stable Statistics
    private sealed class ProcessingCorridor
    {
        public ScanProcessingRail Rail { get; }
        public PreparedBoundary Boundary { get; }
        public Dictionary<(long E, long N), List<ProcessingTarget>> HeadCells { get; } = new();
        public long PointCount { get; set; }
        public ProcessingCorridor(ScanProcessingRail rail, PreparedBoundary boundary) { Rail = rail; Boundary = boundary; }
    }
    private sealed partial class ProcessingTarget
    {
        private readonly ScanProcessingRail _rail;
        private readonly ScanProcessingHead _head;
        private long _accepted, _rejected;
        private double _mean, _m2, _lower, _upper, _minimum, _maximum;
        private readonly SurfaceRange _surface;
        private readonly Dictionary<long, long> _finalBands = new();
        private bool _finalPass;
        private static long FinalBand(double height) => checked((long)Math.Floor(d: height / 0.001d + 1e-8d));
        public ScanHeightHistogram? DebugHistogram { get; set; }
        public ScanHeightHistogram? FilteredHistogram { get; private set; }
        public string PointId => _head.PointId;
        public string? Warning { get; private set; }
        public bool NeedsPass { get; private set; }
        public int Index { get; }
        public PreparedBoundary? Corridor { get; }
        public PreparedBoundary Boundary { get; }
        public ProcessingTarget(ScanProcessingRail rail, ScanProcessingHead head, PreparedBoundary? corridor, int index, int rejectionCount)
        {
            if (string.Equals(a: head.PointId, b: "Track_AB_S025", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(a: head.PointId, b: "AB_S025", comparisonType: StringComparison.OrdinalIgnoreCase) &&
                 (rail.TrackName.Equals(value: "AB", comparisonType: StringComparison.OrdinalIgnoreCase) || rail.TrackName.Equals(value: "Track_AB", comparisonType: StringComparison.OrdinalIgnoreCase))))
                DiagnosticHistogram = new(pointId: head.PointId);
            _surface = new(rejectionCount: rejectionCount);
            Index = index; _rail = rail; _head = head; Corridor = corridor; Boundary = new(polygon: head.Polygon);
            if (Boundary.MaxE - Boundary.MinE > 2d || Boundary.MaxN - Boundary.MinN > 2d)
                throw new InvalidDataException(message: "A saved railhead polygon exceeds the supported dimensions. Recompute its polygons.");
        }
        public void ObserveInitial(double height)
        {
            DiagnosticHistogram?.Observe(height: height);
            DebugHistogram?.Observe(height: height); // Before both height filters.
            _surface.Observe(height: height, referenceHeight: (double)_head.ReferenceHeight);
        }
        public void PrepareSurface()
        {
            if (DebugHistogram is not null) FilteredHistogram = new(pointId: PointId, isFiltered: true);
            _surface.SelectSupportedMaximum();
            if (!_surface.Maximum.HasValue)
            {
                Fail(message: _surface.Count == 0 ? "No scan points inside polygon." : $"No group of at least 3 adjacent 1 mm bands each contains at least {_surface.RejectionCount} eligible points after the 1 m reference check.");
                return;
            }
            _upper = _surface.Maximum.Value;
            _lower = _upper - _rail.HeightFilterMillimetres / 1000d;
            NeedsPass = true;
        }
        public void BeginPass()
        {
            if (!NeedsPass) return;
            _accepted = 0; _mean = 0; _m2 = 0; _minimum = double.PositiveInfinity; _maximum = double.NegativeInfinity;
        }
        public void Accumulate(double height)
        {
            if (!NeedsPass || !_surface.IsSupported(height: height) || Math.Abs(value: height - (double)_head.ReferenceHeight) > MaximumReferenceDeviationMetres + BoundaryArithmeticAllowance ||
                height < _lower - BoundaryArithmeticAllowance || height > _upper + BoundaryArithmeticAllowance) return;
            long band = FinalBand(height: height);
            if (!_finalPass)
            {
                _finalBands.TryGetValue(key: band, value: out long count);
                _finalBands[band] = checked(count + 1);
                return;
            }
            if (!_finalBands.TryGetValue(key: band, value: out long population) || population < _surface.RejectionCount) return;
            _minimum = Math.Min(val1: _minimum, val2: height);
            _maximum = Math.Max(val1: _maximum, val2: height);
            FilteredHistogram?.Observe(height: height);
            _accepted++;
            double delta = height - _mean;
            _mean += delta / _accepted;
            _m2 += delta * (height - _mean);
        }
        public void EndPass()
        {
            if (!NeedsPass) return;
            if (!_finalPass)
            {
                _finalPass = true;
                if (!_finalBands.Values.Any(predicate: count => count >= _surface.RejectionCount))
                    Fail(message: $"No final 1 mm band contains at least {_surface.RejectionCount} points. Earlier offset retained where available.");
                return;
            }
            _rejected = _surface.Count - _accepted;
            NeedsPass = false;
            if (_accepted == 0) Fail(message: "No scan points remain within the maximum-height band.");
        }
        private void Fail(string message)
        { Warning = message; NeedsPass = false; _accepted = 0; _rejected = _surface.Count; _mean = 0; _m2 = 0; }
        private double? Error => _accepted > 1 ? Math.Sqrt(d: Math.Max(val1: 0d, val2: _m2) / (_accepted - 1) / _accepted) : null;
        public ScanOffsetReading OffsetReading() => new(PolygonId: _head.PolygonId, PointId: _head.PointId,
            ReferenceHeight: _head.ReferenceHeight, ScanOffset: _accepted > 0 ? (double)_head.ReferenceHeight - _mean : _head.ScanOffset,
            ScanPointCount: _accepted, StandardError: Error, RawMean: _accepted > 0 ? _mean : null, RejectedPointCount: _rejected)
            { MaximumHeight = _accepted > 0 ? _maximum : null, MinimumHeight = _accepted > 0 ? _minimum : null, ReferenceRejectedPointCount = _surface.ReferenceRejectedCount, Warning = Warning };
        public ScanEpochReading Reading(DateTime epoch, bool calculateOffset)
        {
            double? offset = calculateOffset && _accepted > 0 ? (double)_head.ReferenceHeight - _mean : _head.ScanOffset;
            return new(RunId: _rail.RunId, Rail: _rail.Rail, Sequence: _head.Sequence,
                PointId: _head.PointId, UtcTimestamp: epoch, ScanHt: _accepted > 0 ? _mean + offset : null, ScanPointCount: _accepted,
                StandardError: Error, RejectedPointCount: _rejected) { ScanOffsetUsed = offset };
        }
    }
    #endregion
}
