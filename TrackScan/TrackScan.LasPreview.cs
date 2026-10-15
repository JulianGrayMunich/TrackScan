#region LAS Coordinate Diagnostic Models
using System.Buffers.Binary;
using System.IO;
namespace TrackScan.Core;

public sealed record ScanLasPreviewPoint(long Record, double Easting, double Northing, double Height);
public sealed record ScanLasPreview(string SourcePath, int VersionMinor, int PointFormat, ulong PointCount,
    double ScaleE, double ScaleN, double ScaleH, double OffsetE, double OffsetN, double OffsetH,
    IReadOnlyList<ScanLasPreviewPoint> Points);
#endregion

public static partial class TrackScan
{
    #region Bounded LAS Coordinate Preview
    // LAS is binary. Record is the one-based physical point-record number, not a survey point name.
    // Read at most 100 point records; never load the complete scan for this diagnostic.
    public static ScanLasPreview ReadLasCoordinatePreview(string path)
    {
        const int previewLimit = 100;
        using FileStream stream = new(path: path, mode: FileMode.Open, access: FileAccess.Read,
            share: FileShare.Read, bufferSize: 4096, options: FileOptions.SequentialScan);
        DiagnosticLasHeader info = ReadDiagnosticLasHeader(stream: stream);
        stream.Position = info.PointOffset;
        int previewCount = (int)Math.Min(val1: (ulong)previewLimit, val2: info.Count);
        byte[] record = new byte[info.RecordSize];
        List<ScanLasPreviewPoint> points = new(capacity: previewCount);
        for (int index = 0; index < previewCount; index++)
        {
            stream.ReadExactly(buffer: record.AsSpan());
            var point = DecodeDiagnosticPoint(record: record, info: info);
            points.Add(item: new(Record: index + 1, Easting: point.E, Northing: point.N, Height: point.H));
        }
        return new(SourcePath: Path.GetFullPath(path: path), VersionMinor: info.Minor, PointFormat: info.Format, PointCount: info.Count,
            ScaleE: info.ScaleE, ScaleN: info.ScaleN, ScaleH: info.ScaleH, OffsetE: info.OffsetE, OffsetN: info.OffsetN, OffsetH: info.OffsetH,
            Points: points.AsReadOnly());
    }
    private sealed record DiagnosticLasHeader(int Minor, int Format, int RecordSize, uint PointOffset, ulong Count,
        double ScaleE, double ScaleN, double ScaleH, double OffsetE, double OffsetN, double OffsetH);

    private static DiagnosticLasHeader ReadDiagnosticLasHeader(FileStream stream)
    {
        byte[] header = new byte[375];
        stream.ReadExactly(buffer: header.AsSpan(start: 0, length: 227));
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
        if (minor == 4) stream.ReadExactly(buffer: header.AsSpan(start: 227, length: 148));
        ulong count = minor == 4 ? BinaryPrimitives.ReadUInt64LittleEndian(source: header.AsSpan(start: 247)) : 0;
        if (count == 0) count = BinaryPrimitives.ReadUInt32LittleEndian(source: header.AsSpan(start: 107));
        if (pointOffset > stream.Length || count > (ulong)((stream.Length - pointOffset) / recordSize))
            throw new InvalidDataException(message: "LAS point data is truncated.");
        double scaleE = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 131));
        double scaleN = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 139));
        double scaleH = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 147));
        double offsetE = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 155));
        double offsetN = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 163));
        double offsetH = BinaryPrimitives.ReadDoubleLittleEndian(source: header.AsSpan(start: 171));
        if (!double.IsFinite(d: scaleE) || !double.IsFinite(d: scaleN) || !double.IsFinite(d: scaleH) ||
            scaleE <= 0 || scaleN <= 0 || scaleH <= 0 ||
            !double.IsFinite(d: offsetE) || !double.IsFinite(d: offsetN) || !double.IsFinite(d: offsetH))
            throw new InvalidDataException(message: "LAS coordinate scales or offsets are invalid.");
        return new(Minor: minor, Format: format, RecordSize: recordSize, PointOffset: pointOffset, Count: count,
            ScaleE: scaleE, ScaleN: scaleN, ScaleH: scaleH, OffsetE: offsetE, OffsetN: offsetN, OffsetH: offsetH);
    }

    private static (double E, double N, double H) DecodeDiagnosticPoint(ReadOnlySpan<byte> record, DiagnosticLasHeader info)
    {
        double e = BinaryPrimitives.ReadInt32LittleEndian(source: record) * info.ScaleE + info.OffsetE;
        double n = BinaryPrimitives.ReadInt32LittleEndian(source: record.Slice(start: 4)) * info.ScaleN + info.OffsetN;
        double h = BinaryPrimitives.ReadInt32LittleEndian(source: record.Slice(start: 8)) * info.ScaleH + info.OffsetH;
        if (!double.IsFinite(d: e) || !double.IsFinite(d: n) || !double.IsFinite(d: h))
            throw new InvalidDataException(message: "LAS contains invalid decoded coordinates.");
        return (e, n, h);
    }
    #endregion
}
