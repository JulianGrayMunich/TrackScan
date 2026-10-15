#region Scan Coverage Models
using System.IO;
namespace TrackScan.Core;
public sealed record ScanFenceVertex(int Ring, int Sequence, double Easting, double Northing);
public sealed record ScanCoveragePoint(string Track, string Rail, string PointId, decimal Easting, decimal Northing, decimal Height, string Status);
public sealed record ScanCoverageResult(string SourcePath, IReadOnlyList<IReadOnlyList<ScanFenceVertex>> Rings, IReadOnlyList<ScanCoveragePoint> Points);
#endregion

public static partial class TrackScan
{
    #region Occupied Scan Footprint
    public const double CoverageCellMetres = 0.5;
    private const double FenceIntervalMetres = 5.0;
    private const int MaximumCoverageCells = 1_000_000;
    private const int MaximumFenceEdges = 2_000_000;
    private readonly record struct FenceNode(long E, long N);

    public static ScanCoverageResult CheckLasCoverage(string path, IReadOnlyList<ScanPolygonRail> rails,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        HashSet<FenceNode> cells = new();
        using (FileStream stream = new(path: path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read,
            bufferSize: 1024 * 1024, options: FileOptions.SequentialScan))
        {
            DiagnosticLasHeader info = ReadDiagnosticLasHeader(stream: stream);
            if (info.Count == 0) throw new InvalidDataException(message: "The LAS file contains no scan points.");
            stream.Position = info.PointOffset;
            int batchRecords = Math.Max(val1: 1, val2: (1024 * 1024) / info.RecordSize);
            byte[] buffer = new byte[checked(batchRecords * info.RecordSize)];
            ulong processed = 0;
            int lastPercent = -1;
            while (processed < info.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(val1: (ulong)batchRecords, val2: info.Count - processed);
                stream.ReadExactly(buffer: buffer.AsSpan(start: 0, length: count * info.RecordSize));
                for (int index = 0; index < count; index++)
                {
                    var point = DecodeDiagnosticPoint(record: buffer.AsSpan(start: index * info.RecordSize, length: info.RecordSize), info: info);
                    if (Math.Abs(value: point.E) > 1e9 || Math.Abs(value: point.N) > 1e9)
                        throw new InvalidDataException(message: "LAS coordinates are outside the supported metre-coordinate range.");
                    cells.Add(item: new(E: (long)Math.Floor(d: point.E / CoverageCellMetres), N: (long)Math.Floor(d: point.N / CoverageCellMetres)));
                    if (cells.Count > MaximumCoverageCells)
                        throw new InvalidOperationException(message: "This scan footprint exceeds the diagnostic memory limit. No coverage result was produced.");
                }
                processed += (ulong)count;
                int percent = (int)(100.0 * processed / info.Count);
                if (percent != lastPercent) { lastPercent = percent; progress?.Report(value: $"Reading scan footprint: {percent}%"); }
            }
        }
        progress?.Report(value: "Tracing the scan boundary and checking railhead points...");
        var rings = TraceCoverageFence(cells: cells, cancellationToken: cancellationToken);
        List<ScanCoveragePoint> points = new();
        foreach (ScanPolygonRail rail in rails)
            foreach (ScanComputedPoint point in rail.Points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool inside = InsideCoverageFence(e: (double)point.Easting, n: (double)point.Northing, rings: rings);
                points.Add(item: new(Track: rail.TrackName, Rail: rail.Rail == "R" ? "Primary" : "Secondary", PointId: point.PointName,
                    Easting: point.Easting, Northing: point.Northing, Height: point.Height, Status: inside ? "Inside" : "Outside"));
            }
        return new(SourcePath: Path.GetFullPath(path: path), Rings: rings, Points: points.AsReadOnly());
    }
    #endregion

    #region Concave Fence With Separate Components And Holes
    // Directed edges keep occupied cells on their left. Left turns at diagonal contacts keep rings separate.
    private static IReadOnlyList<IReadOnlyList<ScanFenceVertex>> TraceCoverageFence(HashSet<FenceNode> cells, CancellationToken cancellationToken)
    {
        Dictionary<FenceNode, byte> edges = new();
        int edgeCount = 0;
        void Add(FenceNode node, int direction)
        {
            edges.TryGetValue(key: node, value: out byte mask);
            edges[node] = (byte)(mask | (1 << direction));
            if (++edgeCount > MaximumFenceEdges) throw new InvalidOperationException(message: "The scan boundary is too fragmented for this diagnostic. No coverage result was produced.");
        }
        foreach (FenceNode cell in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cells.Contains(item: new(E: cell.E, N: cell.N - 1))) Add(node: cell, direction: 0);
            if (!cells.Contains(item: new(E: cell.E + 1, N: cell.N))) Add(node: new(E: cell.E + 1, N: cell.N), direction: 1);
            if (!cells.Contains(item: new(E: cell.E, N: cell.N + 1))) Add(node: new(E: cell.E + 1, N: cell.N + 1), direction: 2);
            if (!cells.Contains(item: new(E: cell.E - 1, N: cell.N))) Add(node: new(E: cell.E, N: cell.N + 1), direction: 3);
        }
        List<IReadOnlyList<ScanFenceVertex>> rings = new();
        List<FenceNode> starts = edges.Keys.ToList();
        starts.Sort(comparison: (a, b) => a.E != b.E ? a.E.CompareTo(value: b.E) : a.N.CompareTo(value: b.N));
        foreach (FenceNode start in starts)
            while (edges[start] != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<FenceNode> nodes = new();
                FenceNode current = start;
                int direction = 0;
                while ((edges[start] & (1 << direction)) == 0) direction++;
                do
                {
                    nodes.Add(item: current);
                    edges[current] &= (byte)~(1 << direction);
                    current = direction switch { 0 => new(E: current.E + 1, N: current.N), 1 => new(E: current.E, N: current.N + 1),
                        2 => new(E: current.E - 1, N: current.N), _ => new(E: current.E, N: current.N - 1) };
                    if (current == start) break;
                    byte mask = edges[current];
                    int next = (direction + 1) % 4;
                    if ((mask & (1 << next)) == 0) next = direction;
                    if ((mask & (1 << next)) == 0) next = (direction + 3) % 4;
                    if ((mask & (1 << next)) == 0) throw new InvalidDataException(message: "Unable to close the scan boundary.");
                    direction = next;
                    if (nodes.Count % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
                } while (true);
                List<FenceNode> corners = new();
                for (int i = 0; i < nodes.Count; i++)
                {
                    FenceNode a = nodes[(i + nodes.Count - 1) % nodes.Count], b = nodes[i], c = nodes[(i + 1) % nodes.Count];
                    if ((b.E - a.E) * (c.N - b.N) != (b.N - a.N) * (c.E - b.E)) corners.Add(item: b);
                }
                List<ScanFenceVertex> vertices = new();
                for (int i = 0; i < corners.Count; i++)
                {
                    FenceNode a = corners[i], b = corners[(i + 1) % corners.Count];
                    double length = (Math.Abs(value: b.E - a.E) + Math.Abs(value: b.N - a.N)) * CoverageCellMetres;
                    int sections = Math.Max(val1: 1, val2: (int)Math.Ceiling(a: length / FenceIntervalMetres));
                    for (int j = 0; j < sections; j++)
                        vertices.Add(item: new(Ring: rings.Count + 1, Sequence: vertices.Count + 1,
                            Easting: Math.Round(value: (a.E + (b.E - a.E) * (double)j / sections) * CoverageCellMetres, digits: 4),
                            Northing: Math.Round(value: (a.N + (b.N - a.N) * (double)j / sections) * CoverageCellMetres, digits: 4)));
                }
                rings.Add(item: vertices.AsReadOnly());
            }
        return rings.AsReadOnly();
    }

    public static bool InsideCoverageFence(double e, double n, IReadOnlyList<IReadOnlyList<ScanFenceVertex>> rings)
    {
        bool inside = false;
        const double tolerance = 1e-8;
        foreach (var ring in rings)
            for (int i = 0; i < ring.Count; i++)
            {
                ScanFenceVertex a = ring[i], b = ring[(i + 1) % ring.Count];
                double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
                double x = e - a.Easting, y = n - a.Northing;
                if (Math.Abs(value: x * dy - y * dx) <= tolerance * Math.Max(val1: Math.Abs(value: dx), val2: Math.Abs(value: dy)) &&
                    e >= Math.Min(val1: a.Easting, val2: b.Easting) - tolerance && e <= Math.Max(val1: a.Easting, val2: b.Easting) + tolerance &&
                    n >= Math.Min(val1: a.Northing, val2: b.Northing) - tolerance && n <= Math.Max(val1: a.Northing, val2: b.Northing) + tolerance) return true;
                if ((a.Northing > n) != (b.Northing > n) && x < (n - a.Northing) * dx / dy) inside = !inside;
            }
        return inside;
    }
    #endregion
}
