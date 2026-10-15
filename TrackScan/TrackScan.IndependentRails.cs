#region Independent Survey Following At Fixed Spacing
using global::TrackScan;
namespace TrackScan.Core;

public static partial class TrackScan
{
    public const decimal FixedRailheadSpacingMetres = 3.000m;
    private const decimal EndpointReplacementRadiusMetres = 0.500m;

    private static (List<WorkingPoint> Points, string? Warning) FollowIndependentRail(
        RailCoordinate start, RailCoordinate end, Vector initialDirection, SurveyIndex survey, string railName)
    {
        List<WorkingPoint> points = new();
        AddPoint(points: points, coordinate: start, survey: survey);
        if (Length(value: Between(from: start, to: end)) <= FixedRailheadSpacingMetres)
        {
            AddPoint(points: points, coordinate: end, survey: survey);
            return (points, null);
        }
        Vector terminalNormal = Unit(value: Between(from: start, to: end));
        Vector direction = initialDirection;
        HashSet<RailCoordinate> visited = new() { start };
        while (true)
        {
            if (points.Count >= MaximumRailPoints)
                return (points, $"{railName} rail exceeded the point limit. Check survey and endpoints. No new points have been saved.");
            RailCoordinate current = points[^1].Coordinate;
            // Keep the supplied start even when the whole rail is shorter than 0.500 m.
            if (points.Count > 1 && Length(value: Between(from: current, to: end)) <= EndpointReplacementRadiusMetres + GeometryToleranceMetres)
            { points.RemoveAt(index: points.Count - 1); break; }
            RailCoordinate approximate = Project(origin: current, direction: direction, distance: FixedRailheadSpacingMetres);
            // Initial orientation retains the across-track bearing and one reverse retry.
            // Each rail performs its own search; subsequent steps never reverse direction.
            bool provisionalPastEnd = IsPastRailEnd(point: approximate, end: end, terminalNormal: terminalNormal);
            ScanSurveyPoint? nearest = survey.NearestInDirection(centre: approximate, origin: current, direction: direction,
                radius: EndpointSearchRadiusMetres, limitDegrees: EndpointSearchLimitDegrees);
            // If survey coverage ends here, no extrapolated survey point is required
            // beyond the supplied endpoint. Otherwise test the corrected position below.
            if (nearest is null && points.Count > 1 && provisionalPastEnd) break;
            if (nearest is null && points.Count == 1)
            {
                direction = new(E: -direction.E, N: -direction.N);
                approximate = Project(origin: current, direction: direction, distance: FixedRailheadSpacingMetres);
                nearest = survey.NearestInDirection(centre: approximate, origin: current, direction: direction,
                    radius: EndpointSearchRadiusMetres, limitDegrees: EndpointSearchLimitDegrees);
            }
            if (nearest is null)
                return (points, $"{railName} point {points.Count + 1:D3}: no ToR point within 0.500 m of the provisional position and +/-10 degrees of the forward bearing. Computation halted; no new points have been saved.");
            direction = Unit(value: Between(from: current, to: new(Easting: nearest.Easting, Northing: nearest.Northing)));
            if (points.Count == 1 && Dot(a: direction, b: terminalNormal) <= 0m)
                return (points, $"{railName}: the initial survey direction points away from the supplied endpoint. Check the track definition. No new points have been saved.");
            RailCoordinate computed = Project(origin: current, direction: direction, distance: FixedRailheadSpacingMetres);
            if (IsPastRailEnd(point: computed, end: end, terminalNormal: terminalNormal) ||
                Length(value: Between(from: computed, to: end)) <= EndpointReplacementRadiusMetres + GeometryToleranceMetres) break;
            RailCoordinate key = new(Easting: RoundCoordinate(value: computed.Easting), Northing: RoundCoordinate(value: computed.Northing));
            if (!visited.Add(item: key))
                return (points, $"{railName} path returned to an earlier point. Check the survey. No new points have been saved.");
            AddPoint(points: points, coordinate: computed, survey: survey);
        }
        AddPoint(points: points, coordinate: end, survey: survey);
        return (points, null);
    }

    // The terminal cross-section is perpendicular to this rail's supplied Start -> End.
    private static bool IsPastRailEnd(RailCoordinate point, RailCoordinate end, Vector terminalNormal) =>
        Dot(a: Between(from: end, to: point), b: terminalNormal) > GeometryToleranceMetres;
}
#endregion
