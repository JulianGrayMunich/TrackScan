#region Polygon Models
namespace TrackScan.Core;

public sealed record ScanPolygonOptions(decimal CorridorWidth, decimal CollinearityLimit,
    decimal RailheadWidth, decimal RailheadLength, int HeightFilterMillimetres)
{
    public static ScanPolygonOptions Default => new(CorridorWidth: 0.75m, CollinearityLimit: 0m,
        RailheadWidth: 0.25m, RailheadLength: 0.50m, HeightFilterMillimetres: 3);
    public void Validate()
    {
        if (!new[] { 0.30m, 0.50m, 0.75m, 1.00m }.Contains(value: CorridorWidth) ||
            !new[] { 0m, 0.01m, 0.05m, 0.10m }.Contains(value: CollinearityLimit) ||
            !new[] { 0.02m, 0.03m, 0.04m, 0.05m, 0.06m, 0.075m, 0.10m, 0.20m, 0.25m, 0.50m, 0.75m }.Contains(value: RailheadWidth) ||
            !new[] { 0.40m, 0.50m, 0.60m }.Contains(value: RailheadLength) ||
            !new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10 }.Contains(value: HeightFilterMillimetres))
            throw new ArgumentException(message: "Select polygon parameters from the provided lists.");
    }
}
public sealed record ScanPolygonRail(long RunId, int TrackId, string TrackName, string Rail, IReadOnlyList<ScanComputedPoint> Points);
public sealed record ScanPolygonVertex(string Identifier, int Sequence, string RailheadPoint, decimal Easting, decimal Northing, decimal Height);
public sealed record ScanPolygon(string Identifier, string Kind, long RunId, int TrackId, string Rail, IReadOnlyList<ScanPolygonVertex> Vertices);
public sealed record ScanPolygonSet(ScanPolygonOptions Options, IReadOnlyList<ScanPolygon> Polygons)
{
    public bool RailheadOnly { get; init; }
}
#endregion

public static partial class TrackScan
{
    #region Corridor And Railhead Polygon Construction
    public static ScanPolygonSet CreatePolygons(IReadOnlyList<ScanPolygonRail> rails, ScanPolygonOptions options, bool includeCorridors = true)
    {
        ArgumentNullException.ThrowIfNull(argument: rails);
        ArgumentNullException.ThrowIfNull(argument: options);
        options.Validate();
        if (rails.Count == 0) throw new InvalidOperationException(message: "Compute and save railhead points before creating polygons.");
        List<ScanPolygon> polygons = new();
        foreach (ScanPolygonRail rail in rails)
        {
            if (rail.Points.Count < 2) throw new InvalidOperationException(message: "Each rail requires at least two saved railhead points.");
            List<ScanPolygonVertex> sideA = new(), sideB = new();
            string prefix = $"{rail.TrackName}_T{rail.TrackId}_{(rail.Rail == "R" ? "P" : "S")}";
            string corridorId = prefix + "_CORRIDOR";
            for (int i = 0; i < rail.Points.Count; i++)
            {
                ScanComputedPoint point = rail.Points[i];
                if (point.Sequence != i + 1 || point.Rail != rail.Rail)
                    throw new InvalidOperationException(message: "Saved railhead points are not in continuous rail sequence.");
                RailCoordinate centre = new(Easting: point.Easting, Northing: point.Northing);
                ScanComputedPoint before = rail.Points[Math.Max(val1: 0, val2: i - 1)];
                ScanComputedPoint after = rail.Points[Math.Min(val1: rail.Points.Count - 1, val2: i + 1)];
                Vector forward = Unit(value: new(E: after.Easting - before.Easting, N: after.Northing - before.Northing));
                Vector normal = LeftFromForward(forward: forward);
                string headId = prefix + "_HEAD_" + point.Sequence.ToString(format: "D3", provider: System.Globalization.CultureInfo.InvariantCulture);
                List<ScanPolygonVertex> oblong = new();
                foreach (var corner in new[] { (-1m, 1m), (1m, 1m), (1m, -1m), (-1m, -1m) })
                {
                    RailCoordinate along = Project(origin: centre, direction: forward, distance: corner.Item1 * options.RailheadLength / 2m);
                    RailCoordinate vertex = Project(origin: along, direction: normal, distance: corner.Item2 * options.RailheadWidth / 2m);
                    oblong.Add(item: PolygonVertex(identifier: headId, sequence: oblong.Count + 1, point: point, coordinate: vertex));
                }
                polygons.Add(item: new(Identifier: headId, Kind: "Railhead", RunId: rail.RunId, TrackId: rail.TrackId, Rail: rail.Rail, Vertices: oblong.AsReadOnly()));
                if (!includeCorridors) continue;
                decimal extension = Math.Max(val1: options.CorridorWidth, val2: options.RailheadLength) / 2m;
                RailCoordinate corridorCentre = i == 0 ? Project(origin: centre, direction: forward, distance: -extension)
                    : i == rail.Points.Count - 1 ? Project(origin: centre, direction: forward, distance: extension) : centre;
                sideA.Add(item: PolygonVertex(identifier: corridorId, sequence: 0, point: point,
                    coordinate: Project(origin: corridorCentre, direction: normal, distance: options.CorridorWidth / 2m)));
                sideB.Add(item: PolygonVertex(identifier: corridorId, sequence: 0, point: point,
                    coordinate: Project(origin: corridorCentre, direction: normal, distance: -options.CorridorWidth / 2m)));
            }
            if (!includeCorridors) continue;
            List<ScanPolygonVertex> ring = SimplifyPolygonSide(points: sideA, tolerance: options.CollinearityLimit);
            List<ScanPolygonVertex> opposite = SimplifyPolygonSide(points: sideB, tolerance: options.CollinearityLimit);
            opposite.Reverse(); ring.AddRange(collection: opposite);
            for (int i = 0; i < ring.Count; i++) ring[i] = ring[i] with { Identifier = corridorId + "_V" + (i + 1).ToString(format: "D4"), Sequence = i + 1 };
            polygons.Add(item: new(Identifier: corridorId, Kind: "Corridor", RunId: rail.RunId, TrackId: rail.TrackId, Rail: rail.Rail, Vertices: ring.AsReadOnly()));
        }
        return new(Options: options, Polygons: polygons.AsReadOnly()) { RailheadOnly = !includeCorridors };
    }

    private static ScanPolygonVertex PolygonVertex(string identifier, int sequence, ScanComputedPoint point, RailCoordinate coordinate) =>
        new(Identifier: identifier + "_V" + sequence.ToString(format: "D4"), Sequence: sequence, RailheadPoint: point.PointName,
            Easting: RoundCoordinate(value: coordinate.Easting), Northing: RoundCoordinate(value: coordinate.Northing), Height: point.Height);

    // Iterative maximum-deviation simplification. Every removed vertex is within
    // tolerance of its final replacement segment; endpoints always survive.
    private static List<ScanPolygonVertex> SimplifyPolygonSide(List<ScanPolygonVertex> points, decimal tolerance)
    {
        // Zero represents the explicit None selection: retain every original side vertex.
        if (tolerance == 0m) return new List<ScanPolygonVertex>(collection: points);
        bool[] keep = new bool[points.Count]; keep[0] = keep[^1] = true;
        Stack<(int Start, int End)> pending = new(); pending.Push(item: (0, points.Count - 1));
        while (pending.Count > 0)
        {
            var section = pending.Pop(); int farthest = -1; decimal maximum = tolerance * tolerance;
            ScanPolygonVertex a = points[section.Start], b = points[section.End];
            decimal de = b.Easting - a.Easting, dn = b.Northing - a.Northing, lengthSquared = de * de + dn * dn;
            if (lengthSquared == 0m) throw new InvalidOperationException(message: "A polygon side returns to the same coordinate. Check the rail alignment.");
            for (int i = section.Start + 1; i < section.End; i++)
            {
                decimal e = points[i].Easting - a.Easting, n = points[i].Northing - a.Northing;
                decimal fraction = Math.Clamp(value: (e * de + n * dn) / lengthSquared, min: 0m, max: 1m);
                decimal x = e - fraction * de, y = n - fraction * dn, squared = x * x + y * y;
                if (squared > maximum) { maximum = squared; farthest = i; }
            }
            if (farthest < 0) continue;
            keep[farthest] = true; pending.Push(item: (section.Start, farthest)); pending.Push(item: (farthest, section.End));
        }
        List<ScanPolygonVertex> retained = new();
        for (int i = 0; i < points.Count; i++) if (keep[i]) retained.Add(item: points[i]);
        return retained;
    }
    #endregion
}
