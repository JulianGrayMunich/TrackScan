namespace TrackScan;

#region Duplicate Rail Definition Validation
internal static class ScanTrackDuplicates
{
    private const decimal EndpointToleranceMetres = 0.100m;

    public static void RejectDuplicate(ScanTrack candidate, IReadOnlyList<ScanTrack> existingTracks)
    {
        ArgumentNullException.ThrowIfNull(argument: candidate);
        ArgumentNullException.ThrowIfNull(argument: existingTracks);
        foreach (ScanTrack existing in existingTracks)
        {
            // Identity, not name, distinguishes an edit/rename from a new track.
            if (candidate.TrackId != 0 && candidate.TrackId == existing.TrackId) continue;
            for (int candidateRail = 0; candidateRail < 2; candidateRail++)
                for (int existingRail = 0; existingRail < 2; existingRail++)
                {
                    RailEndpoints proposed = Endpoints(track: candidate, primary: candidateRail == 0);
                    RailEndpoints stored = Endpoints(track: existing, primary: existingRail == 0);
                    bool sameDirection = Near(e1: proposed.StartE, n1: proposed.StartN, e2: stored.StartE, n2: stored.StartN)
                        && Near(e1: proposed.EndE, n1: proposed.EndN, e2: stored.EndE, n2: stored.EndN);
                    bool reversed = Near(e1: proposed.StartE, n1: proposed.StartN, e2: stored.EndE, n2: stored.EndN)
                        && Near(e1: proposed.EndE, n1: proposed.EndN, e2: stored.StartE, n2: stored.StartN);
                    if (sameDirection || reversed)
                    {
                        string proposedLabel = candidateRail == 0 ? "primary" : "secondary";
                        string storedLabel = existingRail == 0 ? "primary" : "secondary";
                        throw new InvalidOperationException(message: $"Duplicate rail definition: the {proposedLabel} rail matches the {storedLabel} rail of track '{existing.Name}'. Both endpoints are within 0.100 m horizontally (including reversed endpoints). No track has been saved.");
                    }
                }
        }
    }

    private readonly record struct RailEndpoints(decimal StartE, decimal StartN, decimal EndE, decimal EndN);

    private static RailEndpoints Endpoints(ScanTrack track, bool primary) => primary
        ? new(StartE: track.RightStartE, StartN: track.RightStartN, EndE: track.RightEndE, EndN: track.RightEndN)
        : new(StartE: track.LeftStartE, StartN: track.LeftStartN, EndE: track.LeftEndE, EndN: track.LeftEndN);

    private static bool Near(decimal e1, decimal n1, decimal e2, decimal n2)
    {
        decimal de = e1 - e2;
        decimal dn = n1 - n2;
        // Compare squared horizontal distances exactly at the stored precision.
        return de * de + dn * dn <= EndpointToleranceMetres * EndpointToleranceMetres;
    }
}
#endregion
