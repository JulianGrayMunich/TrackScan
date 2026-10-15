#region Read Only Saved Calibration History
using global::TrackScan.Core;
using System.Data;
using Microsoft.Data.SqlClient;
namespace TrackScan;

internal sealed partial class ScanRepository
{
    public async Task<StoredOffsetReview> LoadStoredOffsetReviewAsync(ScanProjectState expected)
    {
        ArgumentNullException.ThrowIfNull(argument: expected);
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        StoredOffsetReview result = await ReadStoredOffsetReviewAsync(expected: expected, connection: connection, transaction: transaction);
        await transaction.CommitAsync(); return result;
    }
    private async Task<StoredOffsetReview> ReadStoredOffsetReviewAsync(ScanProjectState expected, SqlConnection connection, SqlTransaction transaction)
    {
        using (SqlCommand identity = Command(connection: connection, transaction: transaction, sql: """
            -- Match ResolveProjectAsync: the stored identity uses datetime2(3).
            -- Comparing directly with sys.databases datetime can introduce fractional precision differences.
            SELECT s.ScanProjectId,s.PreparationRevision,
                (SELECT service_broker_guid FROM sys.databases WHERE database_id=DB_ID())
            FROM dbo.ScanProject s
            JOIN DBTrackGeometry.dbo.Project p ON p.Project_ID=s.GeometryProjectId AND p.IsDeleted=0
            WHERE s.ScanProjectId=@project AND s.GeometryProjectId=@geometry
                AND s.GeometryDatabaseCreated=(SELECT CONVERT(datetime2(3),create_date) FROM sys.databases WHERE name=N'DBTrackGeometry');
            """))
        {
            Add(command: identity, name: "@project", type: SqlDbType.Int, value: expected.ScanProjectId);
            Add(command: identity, name: "@geometry", type: SqlDbType.Int, value: _geometryProjectId);
            using SqlDataReader reader = await identity.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException(message: "The active project has changed. Refresh Track Configuration.");
            VerifyRevision(expected: expected, projectId: reader.GetInt32(i: 0), revision: reader.GetInt64(i: 1), databaseIdentity: reader.GetGuid(i: 2));
        }
        async Task<bool> Exists(string name)
        {
            using SqlCommand check = Command(connection: connection, transaction: transaction, sql: "SELECT CASE WHEN OBJECT_ID(@name) IS NULL THEN 0 ELSE 1 END;");
            Add(command: check, name: "@name", type: SqlDbType.NVarChar, value: name, size: 300);
            return (int)(await check.ExecuteScalarAsync() ?? 0) == 1;
        }
        List<StoredOffsetPoint> points = new(); List<StoredOffsetObservation> observations = new();
        if (!await Exists(name: "DBTrackGeometry.dbo.ScanCurrentPolygonDefinition"))
        { return new(Points: points, Observations: observations); }
        bool hasOffsets = await Exists(name: "DBTrackGeometry.dbo.ScanRailheadOffset");
        bool hasCalibrationId = false;
        if (hasOffsets)
        {
            using SqlCommand check = Command(connection: connection, transaction: transaction,
                sql: "SELECT CASE WHEN COL_LENGTH(N'DBTrackGeometry.dbo.ScanRailheadOffset',N'CalibrationId') IS NULL THEN 0 ELSE 1 END;");
            hasCalibrationId = (int)(await check.ExecuteScalarAsync() ?? 0) == 1;
        }
        string offsetColumns = hasOffsets ? "o.ScanOffset," + (hasCalibrationId ? "o.CalibrationId" : "CAST(NULL AS uniqueidentifier)") : "CAST(NULL AS float),CAST(NULL AS uniqueidentifier)";
        string offsetJoin = hasOffsets ? "LEFT JOIN DBTrackGeometry.dbo.ScanRailheadOffset o ON o.PolygonId=d.PolygonId" : string.Empty;
        string pointSql = """
            SELECT d.PolygonId,v.RailheadPoint,t.TrackName,
            """ + offsetColumns + (hasOffsets ? ",ISNULL(o.OffsetVersion,0),cp.TopOfRailHeight" : ",CAST(0 AS bigint),cp.TopOfRailHeight") + """
             FROM DBTrackGeometry.dbo.ScanCurrentPolygonDefinition d
            JOIN DBTrackGeometry.dbo.ScanPolygonBatch b ON b.BatchId=d.BatchId
            JOIN dbo.ScanTrack t ON t.TrackId=d.TrackId AND t.ScanProjectId=b.ScanProjectId AND t.IsDeleted=0
            JOIN DBTrackGeometry.dbo.ScanPolygonVertex v ON v.PolygonId=d.PolygonId AND v.Sequence=1
            JOIN dbo.ScanComputedPoint cp ON cp.RunId=d.SourceRunId AND cp.Rail=d.Rail AND cp.PointName=v.RailheadPoint
            """ + " " + offsetJoin + """
             WHERE d.Kind=N'Railhead' AND b.ScanProjectId=@project AND b.GeometryProjectId=@geometry
            ORDER BY t.TrackSlot,d.Rail DESC,cp.Sequence;
            """;
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql: pointSql))
        {
            Add(command: command, name: "@project", type: SqlDbType.Int, value: expected.ScanProjectId);
            Add(command: command, name: "@geometry", type: SqlDbType.Int, value: _geometryProjectId);
            using SqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) points.Add(item: new(PolygonId: reader.GetInt64(i: 0), PointId: reader.GetString(i: 1),
                TrackName: reader.GetString(i: 2), AdoptedMean: reader.IsDBNull(i: 3) ? null : reader.GetDouble(i: 3),
                AdoptedCalibrationId: reader.IsDBNull(i: 4) ? null : reader.GetGuid(i: 4)) { OffsetVersion = reader.GetInt64(i: 5), ReferenceHeight = reader.GetDecimal(i: 6) });
        }
        if (await Exists(name: "DBTrackGeometry.dbo.ScanCalibrationObservation") &&
            await Exists(name: "DBTrackGeometry.dbo.ScanCalibrationFile") && await Exists(name: "DBTrackGeometry.dbo.ScanCalibrationBatch"))
        {
            using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
                SELECT o.PolygonId,o.CalibrationId,o.FileIndex,f.SourceFilePath,f.ObservationUtc,c.AdoptedUtc,o.ScanOffset,o.Included,o.ReviewStatus,o.ScanPointCount,o.RejectedPointCount,f.SourceSha256
                FROM DBTrackGeometry.dbo.ScanCalibrationObservation o
                JOIN DBTrackGeometry.dbo.ScanCalibrationFile f ON f.CalibrationId=o.CalibrationId AND f.FileIndex=o.FileIndex
                JOIN DBTrackGeometry.dbo.ScanCalibrationBatch c ON c.CalibrationId=o.CalibrationId
                JOIN DBTrackGeometry.dbo.ScanCurrentPolygonDefinition d ON d.PolygonId=o.PolygonId
                JOIN DBTrackGeometry.dbo.ScanPolygonBatch b ON b.BatchId=d.BatchId
                WHERE b.ScanProjectId=@project AND b.GeometryProjectId=@geometry AND c.GeometryProjectId=@geometry AND d.Kind=N'Railhead'
                ORDER BY c.AdoptedUtc,o.CalibrationId,o.FileIndex,o.PolygonId;
                """);
            Add(command: command, name: "@project", type: SqlDbType.Int, value: expected.ScanProjectId);
            Add(command: command, name: "@geometry", type: SqlDbType.Int, value: _geometryProjectId);
            using SqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) observations.Add(item: new(PolygonId: reader.GetInt64(i: 0), CalibrationId: reader.GetGuid(i: 1),
                FileIndex: reader.GetInt32(i: 2), FilePath: reader.GetString(i: 3), ObservationUtc: reader.GetDateTime(i: 4),
                AdoptedUtc: reader.GetDateTime(i: 5), Offset: reader.IsDBNull(i: 6) ? null : reader.GetDouble(i: 6), Included: reader.GetBoolean(i: 7), Status: reader.GetString(i: 8)) { ScanPointCount = reader.GetInt64(i: 9), RejectedPointCount = reader.GetInt64(i: 10), SourceSha256 = reader.GetString(i: 11) });
        }
        if (hasOffsets && await Exists(name: "DBTrackGeometry.dbo.ScanOffsetReviewChoice"))
        {
            using SqlCommand choices = Command(connection: connection, transaction: transaction, sql: """
                SELECT c.PolygonId,c.CalibrationId,c.FileIndex,c.Selected
                FROM DBTrackGeometry.dbo.ScanOffsetReviewChoice c
                LEFT JOIN DBTrackGeometry.dbo.ScanRailheadOffset o ON o.PolygonId=c.PolygonId
                WHERE c.GeometryProjectId=@geometry
                    AND c.OffsetVersion=(SELECT MAX(latest.OffsetVersion) FROM DBTrackGeometry.dbo.ScanOffsetReviewChoice latest WHERE latest.PolygonId=c.PolygonId)
                    AND (o.PolygonId IS NULL OR (o.OffsetVersion=c.OffsetVersion
                        AND CONVERT(datetime2(0),o.CalibrationUtc)=CONVERT(datetime2(0),c.ReviewedUtc)));
                """);
            Add(command: choices, name: "@geometry", type: SqlDbType.Int, value: _geometryProjectId);
            Dictionary<(long, Guid, int), bool> selected = new();
            using (SqlDataReader reader = await choices.ExecuteReaderAsync())
                while (await reader.ReadAsync()) selected.Add(key: (reader.GetInt64(i: 0), reader.GetGuid(i: 1), reader.GetInt32(i: 2)), value: reader.GetBoolean(i: 3));
            for (int index = 0; index < observations.Count; index++)
            {
                StoredOffsetObservation observation = observations[index];
                if (selected.TryGetValue(key: (observation.PolygonId, observation.CalibrationId, observation.FileIndex), value: out bool include))
                    observations[index] = observation with { SelectedForMean = include };
            }
        }
        return new(Points: points.AsReadOnly(), Observations: observations.AsReadOnly());
    }
}
#endregion
