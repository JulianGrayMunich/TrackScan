#region System Preparation
using System.Globalization;
#endregion

using global::TrackScan;
namespace TrackScan.Core;

#region Secondary End Coordinate Check
public sealed record ScanEndPointCheck(double InitialBackBearingRadians,
    TrackScan.RailCoordinate ApproximatePrevious, double? CorrectedAcrossBearingRadians,
    TrackScan.RailCoordinate? SecondaryEnd, string? Warning)
{
    public TrackScan.RailCoordinate? FinalPrimaryPrevious { get; init; }
    public decimal PrimaryPreviousHeight { get; init; }
    public bool Succeeded => SecondaryEnd.HasValue;

    public string Comments()
    {
        string text = "Primary end back bearing: " + InitialBackBearingRadians.ToString(format: "F6", provider: CultureInfo.InvariantCulture) + " rad" + Environment.NewLine
            + "Previous primary approximate: E=" + ScanCoordinates.Display(value: ApproximatePrevious.Easting)
            + ", N=" + ScanCoordinates.Display(value: ApproximatePrevious.Northing);
        if (SecondaryEnd is TrackScan.RailCoordinate point && CorrectedAcrossBearingRadians.HasValue)
            return text + Environment.NewLine + "Primary end -> Secondary end (corrected): "
                + CorrectedAcrossBearingRadians.Value.ToString(format: "F6", provider: CultureInfo.InvariantCulture) + " rad"
                + Environment.NewLine + "Secondary end: E=" + ScanCoordinates.Display(value: point.Easting)
                + ", N=" + ScanCoordinates.Display(value: point.Northing) + Environment.NewLine + "Secondary end computed";
        return text + Environment.NewLine + Warning;
    }
}

public static partial class TrackScan
{
    public static ScanEndPointCheck CheckSecondaryEndPoint(RailCoordinate primaryEnd, RailCoordinate suppliedSecondaryEnd,
        decimal pointSpacing, int gaugeMillimetres, IReadOnlyList<ScanSurveyPoint> survey, RailCoordinate primaryStart)
    {
        // The same construction uses End -> Start as the reference direction.
        // The supplied secondary-end bearing determines which perpendicular side to retain.
        EndpointDirectionCheck check = CheckEndpointDirection(primary: primaryEnd, oppositePrimary: primaryStart,
            suppliedSecondary: suppliedSecondaryEnd, pointSpacing: pointSpacing, gaugeMillimetres: gaugeMillimetres, survey: survey, endpoint: "End");
        return new(InitialBackBearingRadians: check.InitialDirection, ApproximatePrevious: check.Approximate,
            CorrectedAcrossBearingRadians: check.FinalAcross, SecondaryEnd: check.Secondary, Warning: check.Warning)
        { FinalPrimaryPrevious = check.FinalPrimary, PrimaryPreviousHeight = check.Height };
    }

    public static ScanTrack RoundTrackCoordinates(ScanTrack track)
    {
        ArgumentNullException.ThrowIfNull(argument: track);
        return track with
        {
            RightStartE = RoundCoordinate(value: track.RightStartE), RightStartN = RoundCoordinate(value: track.RightStartN),
            RightEndE = RoundCoordinate(value: track.RightEndE), RightEndN = RoundCoordinate(value: track.RightEndN),
            LeftStartE = RoundCoordinate(value: track.LeftStartE), LeftStartN = RoundCoordinate(value: track.LeftStartN),
            LeftEndE = RoundCoordinate(value: track.LeftEndE), LeftEndN = RoundCoordinate(value: track.LeftEndN)
        };
    }
}
#endregion
