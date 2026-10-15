#region System Preparation
using System;
using System.Collections.Generic;
#endregion

using global::TrackScan;
namespace TrackScan.Core;

public static partial class TrackScan
{
    #region Endpoint Direction Selection And Secondary Offset
    private const decimal EndpointSearchRadiusMetres = 0.5m;
    private const double EndpointReferenceLimitDegrees = 70d;
    private const double EndpointSearchLimitDegrees = 10d;
    private const double EndpointSideLimitDegrees = 20d;
    private const double AngularComparisonToleranceDegrees = 0.0000000001d;

    private sealed record EndpointDirectionCheck(double OriginalAcross, double InitialDirection,
        RailCoordinate Approximate, double? FinalAcross, RailCoordinate? Secondary,
        RailCoordinate? FinalPrimary, decimal Height, string? Warning);

    private static double BearingDifferenceDegrees(double first, double second)
    {
        double difference = Math.Abs(value: first - second) % (2d * Math.PI);
        return Math.Min(val1: difference, val2: 2d * Math.PI - difference) * 180d / Math.PI;
    }

    private static bool BearingWithin(Vector candidate, Vector reference, double limitDegrees) =>
        BearingDifferenceDegrees(first: SurveyBearingRadians(direction: candidate), second: SurveyBearingRadians(direction: reference))
            <= limitDegrees + AngularComparisonToleranceDegrees;

    private static EndpointDirectionCheck CheckEndpointDirection(RailCoordinate primary, RailCoordinate oppositePrimary,
        RailCoordinate suppliedSecondary, decimal pointSpacing, int gaugeMillimetres,
        IReadOnlyList<ScanSurveyPoint> survey, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(argument: survey);
        ValidateGauge(gaugeMillimetres: gaugeMillimetres);
        ValidateSpacing(spacing: pointSpacing);
        Vector reference = Unit(value: Between(from: primary, to: oppositePrimary));
        Vector across = Unit(value: Between(from: primary, to: suppliedSecondary));
        // Survey bearings increase clockwise. Across -90 degrees supplies the
        // first candidate; reverse once if required by the endpoint chord.
        Vector direction = new(E: -across.N, N: across.E);
        if (!BearingWithin(candidate: direction, reference: reference, limitDegrees: EndpointReferenceLimitDegrees))
            direction = new(E: -direction.E, N: -direction.N);
        RailCoordinate approximate = Project(origin: primary, direction: direction, distance: pointSpacing);
        double originalAcross = SurveyBearingRadians(direction: across);
        double initialDirection = SurveyBearingRadians(direction: direction);
        if (!BearingWithin(candidate: direction, reference: reference, limitDegrees: EndpointReferenceLimitDegrees))
            return new(OriginalAcross: originalAcross, InitialDirection: initialDirection, Approximate: approximate,
                FinalAcross: null, Secondary: null, FinalPrimary: null, Height: 0m,
                Warning: endpoint + ": neither candidate direction is within 70 degrees of the direction towards the opposite primary endpoint. Coordinates have not been changed.");

        SurveyIndex index = new(survey: survey, cellWidth: EndpointSearchRadiusMetres);
        ScanSurveyPoint? nearest = index.NearestInDirection(centre: approximate, origin: primary, direction: direction,
            radius: EndpointSearchRadiusMetres, limitDegrees: EndpointSearchLimitDegrees);
        if (nearest is null)
            return new(OriginalAcross: originalAcross, InitialDirection: initialDirection, Approximate: approximate,
                FinalAcross: null, Secondary: null, FinalPrimary: null, Height: 0m,
                Warning: endpoint + ": no Railhead survey point satisfies both the 0.500 m search radius and the +/-10 degree direction limit. Coordinates have not been changed.");

        Vector finalDirection = Unit(value: Between(from: primary, to: new(Easting: nearest.Easting, Northing: nearest.Northing)));
        RailCoordinate finalPrimary = Project(origin: primary, direction: finalDirection, distance: pointSpacing);
        ScanSurveyPoint? heightPoint = index.Nearest(centre: finalPrimary, radius: HeightSearchRadiusMetres);
        // Final direction +90 degrees, then one 180-degree correction if necessary
        // to retain the supplied secondary side. Comparisons wrap through North.
        Vector finalAcross = new(E: finalDirection.N, N: -finalDirection.E);
        if (!BearingWithin(candidate: finalAcross, reference: across, limitDegrees: EndpointSideLimitDegrees))
            finalAcross = new(E: -finalAcross.E, N: -finalAcross.N);
        RailCoordinate secondary = Project(origin: primary, direction: finalAcross,
            distance: (gaugeMillimetres + RailHeadWidthMillimetres) / MillimetresPerMetre);
        return new(OriginalAcross: originalAcross, InitialDirection: initialDirection, Approximate: approximate,
            FinalAcross: SurveyBearingRadians(direction: finalAcross),
            Secondary: new(Easting: RoundCoordinate(value: secondary.Easting), Northing: RoundCoordinate(value: secondary.Northing)),
            FinalPrimary: new(Easting: RoundCoordinate(value: finalPrimary.Easting), Northing: RoundCoordinate(value: finalPrimary.Northing)),
            Height: RoundCoordinate(value: heightPoint?.Height ?? 0m), Warning: null);
    }
    #endregion
}
