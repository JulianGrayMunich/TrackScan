using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Processing Repository
using System.Data;
using Microsoft.Data.SqlClient;
namespace TrackScan;

internal sealed partial class ScanRepository
{
    #region Load Current Saved Rail Polygons
    public async Task<IReadOnlyList<ScanProcessingRail>> LoadProcessingRailsAsync(ScanProjectState expected)
    {
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        using (SqlCommand exists = Command(connection: connection, transaction: transaction,
            sql: "SELECT CASE WHEN OBJECT_ID(N'DBTrackGeometry.dbo.ScanCurrentPolygonDefinition',N'V') IS NULL THEN 0 ELSE 1 END;"))
            if ((int)(await exists.ExecuteScalarAsync() ?? 0) == 0) { await transaction.CommitAsync(); return Array.Empty<ScanProcessingRail>(); }
        await EnsureOffsetSchemaAsync(connection: connection, transaction: transaction);
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            SELECT d.PolygonId,d.BatchId,d.SourceRunId,d.TrackId,t.TrackName,d.Rail,b.HeightFilterMillimetres,
                d.Identifier,d.Kind,v.Identifier,v.Sequence,v.RailheadPoint,v.Easting,v.Northing,v.Height,
                cp.Sequence,cp.TopOfRailHeight,
                (SELECT COUNT(*) FROM dbo.ScanComputedPoint p WHERE p.RunId=d.SourceRunId AND p.Rail=d.Rail),
                o.ScanOffset,ISNULL(o.OffsetVersion,0)
            FROM DBTrackGeometry.dbo.ScanCurrentPolygonDefinition d
            JOIN DBTrackGeometry.dbo.ScanPolygonBatch b ON b.BatchId=d.BatchId
            LEFT JOIN DBTrackGeometry.dbo.ScanRailheadOffset o ON o.PolygonId=d.PolygonId
            JOIN dbo.ScanTrack t ON t.TrackId=d.TrackId AND t.ScanProjectId=b.ScanProjectId
            JOIN DBTrackGeometry.dbo.ScanPolygonVertex v ON v.PolygonId=d.PolygonId
            JOIN dbo.ScanComputedPoint cp ON cp.RunId=d.SourceRunId AND cp.Rail=d.Rail AND cp.PointName=v.RailheadPoint
            WHERE b.ScanProjectId=@project AND b.GeometryProjectId=@geometry
            ORDER BY t.TrackSlot,d.Rail DESC,d.Kind,cp.Sequence,v.Sequence;
            """);
        Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
        Add(command: command, name: "@geometry", type: SqlDbType.Int, value: _geometryProjectId);
        Dictionary<long, ProcessingPolygonRow> polygons = new();
        using (SqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                long id = reader.GetInt64(i: 0);
                if (!polygons.TryGetValue(key: id, value: out ProcessingPolygonRow? row))
                {
                    row = new(PolygonId: id, ScanOffset: reader.IsDBNull(i: 18) ? null : reader.GetDouble(i: 18), OffsetVersion: reader.GetInt64(i: 19), BatchId: reader.GetInt64(i: 1), RunId: reader.GetInt64(i: 2), TrackId: reader.GetInt32(i: 3),
                        TrackName: reader.GetString(i: 4), Rail: reader.GetString(i: 5), Filter: reader.GetInt32(i: 6),
                        Identifier: reader.GetString(i: 7), Kind: reader.GetString(i: 8), PointId: reader.GetString(i: 11),
                        PointSequence: reader.GetInt32(i: 15), ReferenceHeight: reader.GetDecimal(i: 16), PointCount: reader.GetInt32(i: 17), Vertices: new());
                    polygons.Add(key: id, value: row);
                }
                row.Vertices.Add(item: new(Identifier: reader.GetString(i: 9), Sequence: reader.GetInt32(i: 10),
                    RailheadPoint: reader.GetString(i: 11), Easting: reader.GetDecimal(i: 12), Northing: reader.GetDecimal(i: 13), Height: reader.GetDecimal(i: 14)));
            }
        }
        List<ScanProcessingRail> result = new();
        foreach (var group in polygons.Values.GroupBy(keySelector: row => (row.TrackId, row.Rail)))
        {
            ProcessingPolygonRow? corridor = group.SingleOrDefault(predicate: row => row.Kind == "Corridor");
            ProcessingPolygonRow? source = group.FirstOrDefault(predicate: row => row.Kind == "Railhead");
            if (source is null) continue;
            List<ScanProcessingHead> heads = new();
            foreach (ProcessingPolygonRow row in group.Where(predicate: row => row.Kind == "Railhead").OrderBy(keySelector: row => row.PointSequence))
                heads.Add(item: new(Sequence: row.PointSequence, PointId: row.PointId, ReferenceHeight: row.ReferenceHeight, Polygon: row.Polygon()) { PolygonId = row.PolygonId, ScanOffset = row.ScanOffset, OffsetVersion = row.OffsetVersion });
            if (heads.Count != source.PointCount || heads.Count < 2) continue;
            result.Add(item: new(BatchId: source.BatchId, RunId: source.RunId, TrackId: source.TrackId, TrackName: source.TrackName,
                Rail: source.Rail, HeightFilterMillimetres: source.Filter, Corridor: corridor?.Polygon(), Heads: heads.AsReadOnly()));
        }
        await transaction.CommitAsync();
        return result.AsReadOnly();
    }

    private sealed record ProcessingPolygonRow(long PolygonId, double? ScanOffset, long OffsetVersion, long BatchId, long RunId, int TrackId, string TrackName, string Rail,
        int Filter, string Identifier, string Kind, string PointId, int PointSequence, decimal ReferenceHeight,
        int PointCount, List<ScanPolygonVertex> Vertices)
    {
        public ScanPolygon Polygon() => new(Identifier: Identifier, Kind: Kind, RunId: RunId, TrackId: TrackId, Rail: Rail,
            Vertices: Vertices.OrderBy(keySelector: v => v.Sequence).ToArray());
    }
    #endregion

    #region Atomic Epoch Publication
    public async Task SaveScanEpochAsync(ScanProjectState expected, ScanProcessingResult result, ScheduledScanCommit? scheduled = null)
    {
        if (result.Readings.Count == 0 || result.Rails.Count == 0) throw new InvalidOperationException(message: "No scan readings to save.");
        DateTime epoch = result.Readings[0].UtcTimestamp;
        if (epoch.Kind != DateTimeKind.Utc || epoch != ScanEngine.RoundScanSecond(utc: epoch))
            throw new InvalidOperationException(message: "The epoch must be UTC rounded to whole seconds.");
        Dictionary<(long Run, string Rail, int Sequence), (ScanProcessingRail Rail, ScanProcessingHead Head)> expectedPoints = new();
        foreach (ScanProcessingRail rail in result.Rails)
            foreach (ScanProcessingHead head in rail.Heads) expectedPoints.Add(key: (rail.RunId, rail.Rail, head.Sequence), value: (rail, head));
        if (expectedPoints.Count != result.Readings.Count || result.Readings.Select(selector: r => (r.RunId, r.Rail, r.Sequence)).Distinct().Count() != result.Readings.Count)
            throw new InvalidOperationException(message: "The scan results do not match the selected railhead points.");
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        await ValidateProcessingSnapshotAsync(expected: expected, result: result, connection: connection, transaction: transaction);
        await SaveOffsetsAsync(connection: connection, transaction: transaction, result: result, epoch: epoch);
        await LockAsync(connection: connection, transaction: transaction, resource: "GNA:ScanEpochSchema");
        using (SqlCommand schema = Command(connection: connection, transaction: transaction, sql: EpochSchema)) await schema.ExecuteNonQueryAsync();
        foreach (ScanEpochReading reading in result.Readings)
        {
            if (reading.UtcTimestamp != epoch || !expectedPoints.TryGetValue(key: (reading.RunId, reading.Rail, reading.Sequence), value: out var source) || source.Head.PointId != reading.PointId)
                throw new InvalidOperationException(message: "Invalid scan result identity or timestamp.");
            using SqlCommand insert = Command(connection: connection, transaction: transaction, sql: """
                IF EXISTS(SELECT 1 FROM dbo.ScanRailheadEpoch WHERE RunId=@run AND Rail=@rail AND Sequence=@sequence AND UTCtimeStamp=@epoch)
                    THROW 51082, 'Readings already exist for a selected rail at this UTC timestamp. No readings saved; existing readings retained.', 1;
                INSERT dbo.ScanRailheadEpoch(RunId,Rail,Sequence,PointID,UTCtimeStamp,ScanHt,ScanPointCount,SE,RejectedPointCount,
                    PolygonBatchId,SourceFilePath,SourceSha256,FileBytes,LasPointCount,ScanOffsetUsed,TimeZoneId,TimestampSource)
                VALUES(@run,@rail,@sequence,@point,@epoch,@height,@count,@se,@rejected,@batch,@file,@hash,@bytes,@lasCount,@offsetUsed,@zone,@timeSource);
                """);
            Add(command: insert, name: "@offsetUsed", type: SqlDbType.Float, value: (object?)reading.ScanOffsetUsed ?? DBNull.Value);
            Add(command: insert, name: "@zone", type: SqlDbType.NVarChar, value: result.TimeZoneId, size: 200);
            Add(command: insert, name: "@timeSource", type: SqlDbType.NVarChar, value: result.TimestampSource, size: 32);
            Add(command: insert, name: "@run", type: SqlDbType.BigInt, value: reading.RunId);
            Add(command: insert, name: "@rail", type: SqlDbType.Char, value: reading.Rail, size: 1);
            Add(command: insert, name: "@sequence", type: SqlDbType.Int, value: reading.Sequence);
            Add(command: insert, name: "@point", type: SqlDbType.NVarChar, value: reading.PointId, size: 220);
            Add(command: insert, name: "@epoch", type: SqlDbType.DateTime2, value: epoch);
            Add(command: insert, name: "@height", type: SqlDbType.Float, value: (object?)reading.ScanHt ?? DBNull.Value);
            Add(command: insert, name: "@count", type: SqlDbType.BigInt, value: reading.ScanPointCount);
            Add(command: insert, name: "@se", type: SqlDbType.Float, value: (object?)reading.StandardError ?? DBNull.Value);
            Add(command: insert, name: "@rejected", type: SqlDbType.BigInt, value: reading.RejectedPointCount);
            Add(command: insert, name: "@batch", type: SqlDbType.BigInt, value: source.Rail.BatchId);
            Add(command: insert, name: "@file", type: SqlDbType.NVarChar, value: result.SourcePath, size: 2048);
            Add(command: insert, name: "@hash", type: SqlDbType.Char, value: result.Sha256, size: 64);
            Add(command: insert, name: "@bytes", type: SqlDbType.BigInt, value: result.FileBytes);
            Add(command: insert, name: "@lasCount", type: SqlDbType.BigInt, value: result.LasPointCount);
            await insert.ExecuteNonQueryAsync();
        }
        if (scheduled is not null)
            await SaveScheduledGeometryAsync(connection: connection, transaction: transaction, result: result, scheduled: scheduled, scanProjectId: expected.ScanProjectId);
        await transaction.CommitAsync();
    }
    private async Task ValidateProcessingSnapshotAsync(ScanProjectState expected, ScanProcessingResult result, SqlConnection connection, SqlTransaction transaction)
    {
        if (result.Readings.Count == 0) throw new InvalidOperationException(message: "No scan readings.");
        DateTime epoch = result.Readings[0].UtcTimestamp;
        if (epoch.Kind != DateTimeKind.Utc || epoch != ScanEngine.RoundScanSecond(utc: epoch))
            throw new InvalidOperationException(message: "Calibration timestamps must be whole UTC seconds.");
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        foreach (ScanProcessingRail rail in result.Rails)
        {
            using SqlCommand check = Command(connection: connection, transaction: transaction, sql: """
                SELECT COUNT(*) FROM DBTrackGeometry.dbo.ScanCurrentPolygonDefinition d
                JOIN DBTrackGeometry.dbo.ScanPolygonBatch b ON b.BatchId=d.BatchId
                WHERE b.ScanProjectId=@project AND b.GeometryProjectId=@geometry AND d.BatchId=@batch
                    AND d.SourceRunId=@run AND d.TrackId=@track AND d.Rail=@rail
                    AND (d.Kind=N'Railhead' OR (@corridor=1 AND d.Kind=N'Corridor'));
                """);
            Add(command: check, name: "@project", type: SqlDbType.Int, value: project.Id);
            Add(command: check, name: "@geometry", type: SqlDbType.Int, value: _geometryProjectId);
            Add(command: check, name: "@batch", type: SqlDbType.BigInt, value: rail.BatchId);
            Add(command: check, name: "@run", type: SqlDbType.BigInt, value: rail.RunId);
            Add(command: check, name: "@track", type: SqlDbType.Int, value: rail.TrackId);
            Add(command: check, name: "@rail", type: SqlDbType.Char, value: rail.Rail, size: 1);
            Add(command: check, name: "@corridor", type: SqlDbType.Bit, value: rail.Corridor is not null);
            if ((int)(await check.ExecuteScalarAsync() ?? 0) != rail.Heads.Count + (rail.Corridor is null ? 0 : 1))
                throw new InvalidOperationException(message: "Railhead points or polygons changed during processing. Refresh and process again. No readings saved.");
        }
        string zone = await ReadProcessingTimeZoneAsync(connection: connection, transaction: transaction);
        if (zone != result.TimeZoneId) throw new InvalidOperationException(message: "The project time zone changed during processing. No readings saved.");
        if (result.TimestampSource == ScanEngine.FilenameTimestampSource)
        {
            if (ScanEngine.ScanEpochFromFilename(path: result.SourcePath, timeZoneId: zone) != epoch)
                throw new InvalidOperationException(message: "The epoch does not match the filename in the project time zone.");
        }
        else if (result.TimestampSource != ScanEngine.FallbackTimestampSource)
            throw new InvalidOperationException(message: "Unsupported scan timestamp source.");
        await EnsureOffsetSchemaAsync(connection: connection, transaction: transaction);
        await VerifyOffsetVersionsAsync(connection: connection, transaction: transaction, result: result);
    }
    #endregion
}
#endregion
