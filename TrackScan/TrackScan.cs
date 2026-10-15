#region System Preparation
using System;
#endregion

using global::TrackScan;
namespace TrackScan.Core;

#region Reusable Track Scan Geometry
/// <summary>Scan geometry methods independent of WPF, SQL and application state.</summary>
public static partial class TrackScan
{
    #region Gauge And Coordinate Contract
    public const int StandardGaugeMillimetres = 1435;
    public const int BroadGaugeMillimetres = 1600;
    public const int RailHeadWidthMillimetres = 70;
    public const int StoredDecimalPlaces = 4;
    private const decimal MillimetresPerMetre = 1000m;

    public readonly record struct RailCoordinate(decimal Easting, decimal Northing);
    public readonly record struct RailPair(RailCoordinate Right, RailCoordinate Left);

    public static decimal RoundCoordinate(decimal value) => decimal.Round(
        d: value, decimals: StoredDecimalPlaces, mode: MidpointRounding.AwayFromZero);

    public static void ValidateGauge(int gaugeMillimetres)
    {
        if (gaugeMillimetres != StandardGaugeMillimetres && gaugeMillimetres != BroadGaugeMillimetres)
            throw new ArgumentOutOfRangeException(paramName: nameof(gaugeMillimetres), message: "Select a gauge of 1435 mm or 1600 mm.");
    }
    #endregion

    #region Adjust A Rail Pair With Fixed Primary Coordinates
    /// <summary>
    /// Retains the primary Primary coordinate and the supplied Primary-to-Secondary bearing.
    /// Only the Secondary coordinate is offset to gauge + 70 mm. Final values are rounded.
    /// Independent coordinate rounding can slightly change the final separation.
    /// </summary>
    public static RailPair AdjustRailPairToGauge(RailCoordinate right, RailCoordinate left, int gaugeMillimetres)
    {
        ValidateGauge(gaugeMillimetres: gaugeMillimetres);
        decimal deltaE = left.Easting - right.Easting;
        decimal deltaN = left.Northing - right.Northing;
        decimal magnitude = Math.Max(val1: Math.Abs(value: deltaE), val2: Math.Abs(value: deltaN));
        if (magnitude == 0m)
            throw new ArgumentException(message: "The Primary and Secondary coordinates must be different to establish a bearing.");

        // Normalising before squaring avoids overflow and avoids subtracting large
        // map coordinates in binary floating point. The unit vector preserves bearing
        // directly, without a bearing-to-angle-to-vector round trip.
        decimal normalE = deltaE / magnitude;
        decimal normalN = deltaN / magnitude;
        decimal normalLength = SquareRoot(value: normalE * normalE + normalN * normalN);
        decimal width = (gaugeMillimetres + RailHeadWidthMillimetres) / MillimetresPerMetre;
        decimal offsetE = width * normalE / normalLength;
        decimal offsetN = width * normalN / normalLength;

        return new RailPair(
            Right: new RailCoordinate(Easting: RoundCoordinate(value: right.Easting), Northing: RoundCoordinate(value: right.Northing)),
            Left: new RailCoordinate(Easting: RoundCoordinate(value: right.Easting + offsetE), Northing: RoundCoordinate(value: right.Northing + offsetN)));
    }

    private static decimal SquareRoot(decimal value)
    {
        // Here value is confined to [1,2]. Iterate at decimal precision without
        // intermediate decimal-place rounding; handle a last-digit two-value cycle.
        decimal estimate = 1m;
        decimal previous = 0m;
        const int MaximumIterations = 32;
        for (int index = 0; index < MaximumIterations; index++)
        {
            decimal next = (estimate + value / estimate) / 2m;
            if (next == estimate || next == previous) return next;
            previous = estimate;
            estimate = next;
        }
        throw new ArithmeticException(message: "Rail pair length did not converge at decimal precision.");
    }
    #endregion
}
#endregion
