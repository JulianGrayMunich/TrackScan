#region Independent Full Scan Per Railhead Polygon
using System.IO;
using System.Security.Cryptography;
namespace TrackScan.Core;
public static partial class TrackScan
{
    public static ScanProcessingResult ProcessLasRailheadsIndividually(string path, DateTime utcTimestamp,
        IReadOnlyList<ScanProcessingRail> rails, bool calculateOffsets = false, IProgress<string>? progress = null,
        IProgress<ScanEpochReading>? readingProgress = null, CancellationToken cancellationToken = default, int rejectionCount = 50)
    {
        ValidateRejectionCount(rejectionCount: rejectionCount);
        if (rails.Count == 0 || rails.Any(predicate: rail => rail.Heads.Count == 0))
            throw new InvalidOperationException(message: "Select rails with saved railhead polygons.");
        string? warning = MissingScanOffsetMessage(rails: rails, calculateOffsets: calculateOffsets);
        if (warning is not null) throw new InvalidOperationException(message: warning);
        DateTime epoch = RoundScanSecond(utc: utcTimestamp);
        using FileStream scan = new(path: path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read,
            bufferSize: 1024 * 1024, options: FileOptions.SequentialScan);
        DiagnosticLasHeader info = ReadDiagnosticLasHeader(stream: scan);
        if (info.Count == 0) throw new InvalidDataException(message: "The LAS file contains no scan points.");
        // Hold the same read-only file handle for every pass, preventing replacement or writes during the run.
        scan.Position = 0;
        byte[] buffer = new byte[Math.Max(val1: 1024 * 1024, val2: info.RecordSize)];
        progress?.Report(value: "Reading LAS fingerprint...");
        using IncrementalHash hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);
        int bytes;
        while ((bytes = scan.Read(buffer: buffer, offset: 0, count: buffer.Length)) > 0)
        { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(data: buffer.AsSpan(start: 0, length: bytes)); }
        string fingerprint = Convert.ToHexString(inArray: hash.GetHashAndReset());
        List<ScanEpochReading> readings = new();
        List<ScanOffsetReading> offsets = new();
        List<string> warnings = new();
        int totalHeads = rails.Sum(selector: rail => rail.Heads.Count);
        string temporary = Path.Combine(path1: Path.GetTempPath(), path2: "GNA-DirectHead-" + Guid.NewGuid().ToString(format: "N") + ".tmp");
        using FileStream heights = new(path: temporary, mode: FileMode.CreateNew, access: FileAccess.ReadWrite, share: FileShare.None,
            bufferSize: 65536, options: FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        using BinaryWriter writer = new(output: heights, encoding: System.Text.Encoding.UTF8, leaveOpen: true);
        using BinaryReader reader = new(input: heights, encoding: System.Text.Encoding.UTF8, leaveOpen: true);
        foreach (ScanProcessingRail rail in rails)
            foreach (ScanProcessingHead head in rail.Heads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // No corridor, coverage boundary, other polygon, or spatial index participates in this pass.
                ProcessingTarget target = new(rail: rail, head: head, corridor: null, index: readings.Count, rejectionCount: rejectionCount);
                heights.SetLength(value: 0); heights.Position = 0;
                scan.Position = info.PointOffset;
                ulong processed = 0;
                int lastPercent = -1;
                int recordsPerBlock = buffer.Length / info.RecordSize;
                while (processed < info.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(val1: (ulong)recordsPerBlock, val2: info.Count - processed);
                    scan.ReadExactly(buffer: buffer.AsSpan(start: 0, length: count * info.RecordSize));
                    for (int i = 0; i < count; i++)
                    {
                        var point = DecodeDiagnosticPoint(record: buffer.AsSpan(start: i * info.RecordSize, length: info.RecordSize), info: info);
                        if (!target.Boundary.Contains(e: point.E, n: point.N, tolerance: 0d)) continue;
                        target.ObserveInitial(height: point.H);
                        writer.Write(value: point.H);
                    }
                    processed += (ulong)count;
                    int percent = (int)(processed * 100d / info.Count);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(value: $"Railhead {readings.Count + 1}/{totalHeads}: {head.PointId} — complete LAS pass {percent}%.");
                    }
                }
                writer.Flush(); heights.Position = 0;
                target.PrepareSurface();
                for (int pass = 0; pass < 2 && target.NeedsPass; pass++)
                {
                    target.BeginPass(); heights.Position = 0;
                    while (heights.Position < heights.Length)
                    { cancellationToken.ThrowIfCancellationRequested(); target.Accumulate(height: reader.ReadDouble()); }
                    target.EndPass();
                }
                if (target.Warning is not null) warnings.Add(item: head.PointId + ": " + target.Warning);
                ScanEpochReading reading = target.Reading(epoch: epoch, calculateOffset: calculateOffsets);
                readings.Add(item: reading);
                if (calculateOffsets) offsets.Add(item: target.OffsetReading());
                readingProgress?.Report(value: reading);
            }
        return new(SourcePath: Path.GetFullPath(path: path), Sha256: fingerprint, FileBytes: scan.Length, LasPointCount: checked((long)info.Count),
            Rails: rails, Readings: readings.AsReadOnly()) { RejectionCount = rejectionCount, Warnings = warnings.AsReadOnly(), CalculateOffsets = calculateOffsets, Offsets = offsets.AsReadOnly() };
    }
}
#endregion
