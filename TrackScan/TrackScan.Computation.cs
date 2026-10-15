#region System Preparation
using System;
using System.Collections.Generic;
using System.Globalization;
#endregion

using global::TrackScan;
namespace TrackScan.Core;

#region Computed Reference Point Models
public sealed record ScanComputedPoint(string PointName, string Rail, int Sequence,
    decimal Easting, decimal Northing, decimal Height, decimal DistanceAlongRail,
    int SurveyPointCount, bool HeightInterpolated)
{
    public bool HeightFromNearestSurvey { get; init; }
}
public sealed record ScanComputationInput(long SurveyId, ScanTrack Track, IReadOnlyList<ScanSurveyPoint> Survey);
public sealed record ScanComputationResult(ScanTrack Track, IReadOnlyList<ScanComputedPoint> Points);
public sealed record ScanComputationOutcome(ScanComputationResult? Result, string? Warning)
{
    public bool Succeeded => Result is not null;
}
#endregion

public static partial class TrackScan
{
    #region Geometry Validation And Vector Operations
    private const decimal MaximumPairExcessMetres = 0.500m;
    private const decimal GeometryToleranceMetres = 0.00000001m;
    private const decimal StoredEndpointToleranceMetres = 0.0002m;
    private const int MaximumRailPoints = 1000000;
    private readonly record struct Vector(decimal E, decimal N);

    public static void ValidateSpacing(decimal spacing)
    {
        if (spacing != 1m && spacing != 3m)
            throw new ArgumentException(message: "Select Track Point spacing of 1.00 or 3.00 metres.");
    }

    private static Vector Between(RailCoordinate from, RailCoordinate to) =>
        new(E: to.Easting - from.Easting, N: to.Northing - from.Northing);
    private static decimal Length(Vector value)
    {
        decimal scale = Math.Max(val1: Math.Abs(value: value.E), val2: Math.Abs(value: value.N));
        if (scale == 0m) return 0m;
        decimal e = value.E / scale, n = value.N / scale;
        return scale * SquareRoot(value: e * e + n * n);
    }
    private static Vector Unit(Vector value)
    {
        decimal length = Length(value: value);
        if (length <= GeometryToleranceMetres)
            throw new InvalidOperationException(message: "A rail direction cannot be established from coincident points.");
        return new Vector(E: value.E / length, N: value.N / length);
    }
    private static decimal Dot(Vector a, Vector b) => a.E * b.E + a.N * b.N;
    private static RailCoordinate Project(RailCoordinate origin, Vector direction, decimal distance) =>
        new(Easting: origin.Easting + direction.E * distance, Northing: origin.Northing + direction.N * distance);
    private static Vector LeftFromForward(Vector forward) => new(E: -forward.N, N: forward.E);

    public static void ValidateRailDefinition(ScanTrack track, bool roundedCoordinates = false)
    {
        ArgumentNullException.ThrowIfNull(argument: track);
        ValidateGauge(gaugeMillimetres: track.GaugeMillimetres);
        ValidateSpacing(spacing: track.PointSpacing);
        Vector start = Between(from: new(Easting: track.RightStartE, Northing: track.RightStartN), to: new(Easting: track.LeftStartE, Northing: track.LeftStartN));
        Vector end = Between(from: new(Easting: track.RightEndE, Northing: track.RightEndN), to: new(Easting: track.LeftEndE, Northing: track.LeftEndN));
        decimal maximum = track.GaugeMillimetres / MillimetresPerMetre + MaximumPairExcessMetres;
        if (Length(value: start) > maximum || Length(value: end) > maximum)
            throw new ArgumentException(message: $"The Start or End rail separation exceeds the selected gauge plus 0.500 m ({maximum:F3} m). Check the coordinates.");
        decimal agreement = Dot(a: Unit(value: start), b: Unit(value: end));
        decimal minimum = SquareRoot(value: 2m) / 2m;
        // Four-decimal endpoint storage can move a bearing by a few arcseconds.
        // Submitted coordinates are checked strictly; stored pairs get only the
        // tolerance implied by the two rounded coordinate differences.
        decimal roundingAllowance = roundedCoordinates
            ? StoredEndpointToleranceMetres / Length(value: start) + StoredEndpointToleranceMetres / Length(value: end) : 0m;
        if (agreement < minimum - roundingAllowance - 0.000000000001m)
            throw new ArgumentException(message: "The Start and End Primary-to-Secondary bearings differ by more than 45 degrees. Secondary/Primary coordinates may have been swapped. Check them before continuing.");
    }
    #endregion

    #region Preserve Primary Endpoints When Saving A Track
    /// <summary>
    /// Uses only Primary Start -> Primary End for provisional Secondary endpoint positions.
    /// Computation later replaces those positions with offsets normal to the first
    /// and last computed primary segments. Supplied Primary values only receive the
    /// required four-decimal storage rounding; they are never shifted for gauge.
    /// </summary>
    public static ScanTrack AdjustTrackToPrimaryRail(ScanTrack track)
    {
        ScanCoordinates.ValidateTrack(track: track);
        RailCoordinate rightStart = new(Easting: track.RightStartE, Northing: track.RightStartN);
        RailCoordinate rightEnd = new(Easting: track.RightEndE, Northing: track.RightEndN);
        Vector forward = Unit(value: Between(from: rightStart, to: rightEnd));
        Vector across = LeftFromForward(forward: forward);
        decimal width = (track.GaugeMillimetres + RailHeadWidthMillimetres) / MillimetresPerMetre;
        RailCoordinate leftStart = Project(origin: rightStart, direction: across, distance: width);
        RailCoordinate leftEnd = Project(origin: rightEnd, direction: across, distance: width);
        return track with
        {
            RightStartE = RoundCoordinate(value: rightStart.Easting), RightStartN = RoundCoordinate(value: rightStart.Northing),
            RightEndE = RoundCoordinate(value: rightEnd.Easting), RightEndN = RoundCoordinate(value: rightEnd.Northing),
            LeftStartE = RoundCoordinate(value: leftStart.Easting), LeftStartN = RoundCoordinate(value: leftStart.Northing),
            LeftEndE = RoundCoordinate(value: leftEnd.Easting), LeftEndN = RoundCoordinate(value: leftEnd.Northing)
        };
    }
    #endregion

    #region Deterministic Survey Spatial Index
    // Cells use half the spacing as their width. Queries cover their own radius,
    // including the one-metre final-position height search across extra cells.
    // Decimal keys retain local detail at large map coordinates.
    private sealed class SurveyIndex
    {
        private readonly decimal _cellWidth;
        private readonly Dictionary<(decimal E, decimal N), List<ScanSurveyPoint>> _cells = new();
        public SurveyIndex(IReadOnlyList<ScanSurveyPoint> survey, decimal cellWidth)
        {
            _cellWidth = cellWidth;
            foreach (ScanSurveyPoint point in survey)
            {
                var key = Cell(point: new(Easting: point.Easting, Northing: point.Northing));
                if (!_cells.TryGetValue(key: key, value: out List<ScanSurveyPoint>? bucket))
                { bucket = new(); _cells.Add(key: key, value: bucket); }
                bucket.Add(item: point);
            }
            foreach (List<ScanSurveyPoint> bucket in _cells.Values)
                bucket.Sort(comparison: (a, b) => a.Record.CompareTo(value: b.Record));
        }
        private (decimal E, decimal N) Cell(RailCoordinate point) =>
            (decimal.Floor(d: point.Easting / _cellWidth), decimal.Floor(d: point.Northing / _cellWidth));
        private IEnumerable<ScanSurveyPoint> Nearby(RailCoordinate centre, decimal radius)
        {
            var minimum = Cell(point: new(Easting: centre.Easting - radius, Northing: centre.Northing - radius));
            var maximum = Cell(point: new(Easting: centre.Easting + radius, Northing: centre.Northing + radius));
            for (decimal e = minimum.E; e <= maximum.E; e++)
                for (decimal n = minimum.N; n <= maximum.N; n++)
                    if (_cells.TryGetValue(key: (e, n), value: out List<ScanSurveyPoint>? bucket))
                        foreach (ScanSurveyPoint point in bucket) yield return point;
        }
        public ScanSurveyPoint? NearestInDirection(RailCoordinate centre, RailCoordinate origin, Vector direction,
            decimal radius, double limitDegrees)
        {
            decimal bestSquared = radius * radius;
            ScanSurveyPoint? best = null;
            foreach (ScanSurveyPoint point in Nearby(centre: centre, radius: radius))
            {
                decimal e = point.Easting - centre.Easting, n = point.Northing - centre.Northing;
                decimal squared = e * e + n * n;
                if (squared > bestSquared) continue;
                Vector candidate = Between(from: origin, to: new(Easting: point.Easting, Northing: point.Northing));
                if (Length(value: candidate) <= GeometryToleranceMetres ||
                    !BearingWithin(candidate: candidate, reference: direction, limitDegrees: limitDegrees)) continue;
                if (squared < bestSquared || best is null || point.Record < best.Record)
                { bestSquared = squared; best = point; }
            }
            return best;
        }

        public ScanSurveyPoint? Nearest(RailCoordinate centre, decimal? radius = null, bool strictRadius = false)
        {
            decimal searchRadius = radius ?? _cellWidth;
            decimal bestSquared = searchRadius * searchRadius;
            ScanSurveyPoint? best = null;
            foreach (ScanSurveyPoint point in Nearby(centre: centre, radius: searchRadius))
            {
                decimal e = point.Easting - centre.Easting, n = point.Northing - centre.Northing;
                decimal squared = e * e + n * n;
                if (strictRadius && squared >= searchRadius * searchRadius) continue;
                if (squared < bestSquared || (squared == bestSquared && (best is null || point.Record < best.Record)))
                { bestSquared = squared; best = point; }
            }
            return best;
        }
    }
    #endregion

    #region Rail Computation And Height Interpolation
    private sealed class WorkingPoint
    {
        public RailCoordinate Coordinate { get; init; }
        public decimal Distance { get; init; }
        public decimal Height { get; set; }
        public int Count { get; set; }
        public bool Interpolated { get; set; }
        public bool HeightFromNearestSurvey { get; set; }
    }

    private const decimal HeightSearchRadiusMetres = 1m;

    private static void AddPoint(List<WorkingPoint> points, RailCoordinate coordinate, SurveyIndex survey)
    {
        // Always search again at the final horizontal position, independently of
        // the approximate-position search used to establish the bearing.
        // Nearest accepts both zero distance and the inclusive one-metre boundary.
        ScanSurveyPoint? nearest = survey.Nearest(centre: coordinate, radius: HeightSearchRadiusMetres);
        decimal distance = points.Count == 0 ? 0m : points[^1].Distance + Length(value: Between(from: points[^1].Coordinate, to: coordinate));
        points.Add(item: new WorkingPoint
        {
            Coordinate = coordinate, Distance = distance, Height = nearest?.Height ?? 0m,
            Count = nearest is null ? 0 : 1, HeightFromNearestSurvey = nearest is not null
        });
    }

    private static string? MissingEndpointHeightWarning(List<WorkingPoint> points, string rail, string trackName, decimal radius)
    {
        List<string> missing = new();
        int[] endpoints = { 0, points.Count - 1 };
        foreach (int index in endpoints)
        {
            WorkingPoint point = points[index];
            if (point.Height != 0m) continue;
            string pointName = trackName + "_" + (rail == "Primary" ? "P" : "S") + (index + 1).ToString(format: "D3", provider: CultureInfo.InvariantCulture);
            string position = index == 0 ? "Start" : "End";
            string reason = point.Count == 0 ? "No survey point was found" : "The nearest survey point has zero height";
            missing.Add(item: FormattableString.Invariant($"{rail} {position}: {pointName}, E={point.Coordinate.Easting:F4}, N={point.Coordinate.Northing:F4}. {reason} within {radius:F3} m."));
        }
        if (missing.Count == 0) return null;
        return string.Join(separator: Environment.NewLine, values: missing);
    }

    private static void InterpolateHeights(List<WorkingPoint> points)
    {
        // Endpoints have already passed the non-throwing coverage check.
        int preceding = 0;
        while (preceding < points.Count - 1)
        {
            int following = preceding + 1;
            while (points[following].Height == 0m) following++;
            decimal span = points[following].Distance - points[preceding].Distance;
            for (int index = preceding + 1; index < following; index++)
            {
                decimal fraction = (points[index].Distance - points[preceding].Distance) / span;
                points[index].Height = points[preceding].Height + fraction * (points[following].Height - points[preceding].Height);
                points[index].Interpolated = true;
                points[index].HeightFromNearestSurvey = false;
            }
            preceding = following;
        }
    }

    // Compatibility entry point for callers that require a successful result.
    // The desktop workflow uses TryComputeTrackPoints and handles expected survey
    // coverage failures as warnings, without throwing a first-chance exception.
    public static ScanComputationResult ComputeTrackPoints(ScanTrack track, IReadOnlyList<ScanSurveyPoint> survey)
    {
        ScanComputationOutcome outcome = TryComputeTrackPoints(track: track, survey: survey);
        return outcome.Result ?? throw new InvalidOperationException(message: outcome.Warning);
    }

    public static ScanComputationOutcome TryComputeTrackPoints(ScanTrack track, IReadOnlyList<ScanSurveyPoint> survey)
    {
        ArgumentNullException.ThrowIfNull(argument: track);
        track = track with { PointSpacing = FixedRailheadSpacingMetres };
        ScanCoordinates.ValidateTrack(track: track, roundedCoordinates: true);
        ArgumentNullException.ThrowIfNull(argument: survey);
        if (survey.Count == 0) throw new InvalidOperationException(message: "Import a Railhead survey before computing railhead points.");
        RailCoordinate primaryStart = new(Easting: track.RightStartE, Northing: track.RightStartN);
        RailCoordinate secondaryStart = new(Easting: track.LeftStartE, Northing: track.LeftStartN);
        Vector across = Unit(value: Between(from: primaryStart, to: secondaryStart));
        Vector initialDirection = new(E: across.N, N: -across.E);
        SurveyIndex index = new(survey: survey, cellWidth: FixedRailheadSpacingMetres / 2m);
        var primary = FollowIndependentRail(start: primaryStart, end: new(Easting: track.RightEndE, Northing: track.RightEndN),
            initialDirection: initialDirection, survey: index, railName: "Primary");
        if (primary.Warning is not null) return new(Result: null, Warning: primary.Warning);
        var secondary = FollowIndependentRail(start: secondaryStart, end: new(Easting: track.LeftEndE, Northing: track.LeftEndN),
            initialDirection: initialDirection, survey: index, railName: "Secondary");
        if (secondary.Warning is not null) return new(Result: null, Warning: secondary.Warning);
        List<WorkingPoint> right = primary.Points;
        List<WorkingPoint> left = secondary.Points;
        string? rightWarning = MissingEndpointHeightWarning(points: right, rail: "Primary", trackName: track.Name, radius: HeightSearchRadiusMetres);
        string? leftWarning = MissingEndpointHeightWarning(points: left, rail: "Secondary", trackName: track.Name, radius: HeightSearchRadiusMetres);
        if (rightWarning is not null || leftWarning is not null)
        {
            string details = rightWarning is null ? leftWarning! : leftWarning is null ? rightWarning : rightWarning + Environment.NewLine + leftWarning;
            return new ScanComputationOutcome(Result: null, Warning:
                "Track computation halted: endpoint height unavailable." + Environment.NewLine + details + Environment.NewLine +
                "A missing endpoint height cannot be interpolated without valid heights on both sides. Check survey coverage and endpoint coordinates. No new points or CSV have been saved.");
        }
        InterpolateHeights(points: right);
        InterpolateHeights(points: left);
        List<ScanComputedPoint> result = new(capacity: right.Count + left.Count);
        AppendResult(result: result, points: right, name: track.Name, rail: "R");
        AppendResult(result: result, points: left, name: track.Name, rail: "L");
        return new ScanComputationOutcome(Result: new ScanComputationResult(Track: track, Points: result.AsReadOnly()), Warning: null);
    }

    private static void AppendResult(List<ScanComputedPoint> result, List<WorkingPoint> points, string name, string rail)
    {
        for (int index = 0; index < points.Count; index++)
        {
            WorkingPoint point = points[index];
            result.Add(item: new ScanComputedPoint(PointName: name + "_" + (rail == "R" ? "P" : "S") + (index + 1).ToString(format: "D3", provider: CultureInfo.InvariantCulture),
                Rail: rail, Sequence: index + 1, Easting: RoundCoordinate(value: point.Coordinate.Easting), Northing: RoundCoordinate(value: point.Coordinate.Northing),
                Height: RoundCoordinate(value: point.Height), DistanceAlongRail: RoundCoordinate(value: point.Distance), SurveyPointCount: point.Count, HeightInterpolated: point.Interpolated)
                { HeightFromNearestSurvey = point.HeightFromNearestSurvey });
        }
    }
    #endregion
}
