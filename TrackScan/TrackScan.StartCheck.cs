#region System Preparation
using System.Globalization;
#endregion

using global::TrackScan;
namespace TrackScan.Core;

#region Staged Start Secondary Point Check
public sealed record ScanStartPointCheck(double OriginalAcrossBearingRadians, double InitialForwardBearingRadians,
    TrackScan.RailCoordinate ApproximateRightSecond, double? CorrectedAcrossBearingRadians,
    TrackScan.RailCoordinate? LeftStart, string? Warning)
{
    public TrackScan.RailCoordinate? FinalPrimaryNext { get; init; }
    public decimal PrimaryNextHeight { get; init; }
    public bool Succeeded => LeftStart.HasValue;

    public string Comments()
    {
        List<string> lines = new()
        {
            "DIR_PS (P01 -> S01): " + OriginalAcrossBearingRadians.ToString(format: "F6", provider: CultureInfo.InvariantCulture) + " rad",
            "DIR P01 -> P02: " + InitialForwardBearingRadians.ToString(format: "F6", provider: CultureInfo.InvariantCulture) + " rad",
            "P02 approximate: E=" + ScanCoordinates.Display(value: ApproximateRightSecond.Easting) + ", N=" + ScanCoordinates.Display(value: ApproximateRightSecond.Northing)
        };
        if (LeftStart is TrackScan.RailCoordinate left && CorrectedAcrossBearingRadians.HasValue)
        {
            lines.Add(item: "DIR P01 -> S01 (corrected): " + CorrectedAcrossBearingRadians.Value.ToString(format: "F6", provider: CultureInfo.InvariantCulture) + " rad");
            lines.Add(item: "S01: E=" + ScanCoordinates.Display(value: left.Easting) + ", N=" + ScanCoordinates.Display(value: left.Northing));
            lines.Add(item: "S01 computed");
        }
        else lines.Add(item: Warning ?? "S01 could not be computed.");
        return string.Join(separator: Environment.NewLine, values: lines);
    }
}

public static partial class TrackScan
{
    private static double SurveyBearingRadians(Vector direction)
    {
        double bearing = Math.Atan2(y: (double)direction.E, x: (double)direction.N);
        return bearing < 0d ? bearing + 2d * Math.PI : bearing;
    }

    public static ScanStartPointCheck CheckStartLeftPoint(RailCoordinate rightStart, RailCoordinate suppliedLeftStart,
        decimal pointSpacing, int gaugeMillimetres, IReadOnlyList<ScanSurveyPoint> survey, RailCoordinate primaryEnd)
    {
        EndpointDirectionCheck check = CheckEndpointDirection(primary: rightStart, oppositePrimary: primaryEnd,
            suppliedSecondary: suppliedLeftStart, pointSpacing: pointSpacing, gaugeMillimetres: gaugeMillimetres, survey: survey, endpoint: "Start");
        return new(OriginalAcrossBearingRadians: check.OriginalAcross, InitialForwardBearingRadians: check.InitialDirection,
            ApproximateRightSecond: check.Approximate, CorrectedAcrossBearingRadians: check.FinalAcross,
            LeftStart: check.Secondary, Warning: check.Warning)
        { FinalPrimaryNext = check.FinalPrimary, PrimaryNextHeight = check.Height };
    }
}
#endregion
