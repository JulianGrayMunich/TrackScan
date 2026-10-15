using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Polygon Offset Persistence
using System.Data;
using Microsoft.Data.SqlClient;
namespace TrackScan;

internal sealed partial class ScanRepository
{
    private static async Task EnsureOffsetSchemaAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await LockAsync(connection: connection, transaction: transaction, resource: "GNA:ScanPolygonSchema");
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            IF OBJECT_ID(N'DBTrackGeometry.dbo.ScanRailheadOffset',N'U') IS NULL
            EXEC(N'USE DBTrackGeometry;
                CREATE TABLE dbo.ScanRailheadOffset(
                    PolygonId bigint NOT NULL PRIMARY KEY REFERENCES dbo.ScanPolygonDefinition(PolygonId) ON DELETE CASCADE,
                    ScanOffset float NOT NULL, RawMean float NOT NULL, ReferenceHeight decimal(24,4) NOT NULL,
                    ScanPointCount bigint NOT NULL, SE float NULL, RejectedPointCount bigint NOT NULL,
                    CalibrationUtc datetime2(0) NOT NULL, SourceSha256 char(64) NOT NULL,
                    OffsetVersion bigint NOT NULL,
                    CONSTRAINT CK_ScanOffsetCounts CHECK(ScanPointCount>0 AND RejectedPointCount>=0));');
            IF COL_LENGTH(N'DBTrackGeometry.dbo.ScanRailheadOffset',N'CalibrationId') IS NULL
                EXEC(N'ALTER TABLE DBTrackGeometry.dbo.ScanRailheadOffset ADD CalibrationId uniqueidentifier NULL;');
            """);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<string> LoadProcessingTimeZoneAsync()
    {
        await using SqlConnection connection = Connection(database: "DBTrackGeometry");
        await connection.OpenAsync();
        return await ReadProcessingTimeZoneAsync(connection: connection, transaction: null);
    }

    private async Task<string> ReadProcessingTimeZoneAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        using SqlCommand command = Command(connection: connection, transaction: transaction,
            sql: "SELECT TimeZoneId FROM DBTrackGeometry.dbo.Project WHERE Project_ID=@project AND ISNULL(IsDeleted,0)=0;");
        Add(command: command, name: "@project", type: SqlDbType.Int, value: _geometryProjectId);
        object? value = await command.ExecuteScalarAsync();
        if (value is null) throw new InvalidOperationException(message: "The active project is no longer available.");
        return value is DBNull ? string.Empty : (string)value;
    }

    private static async Task VerifyOffsetVersionsAsync(SqlConnection connection, SqlTransaction transaction, ScanProcessingResult result)
    {
        foreach (ScanProcessingRail rail in result.Rails)
            foreach (ScanProcessingHead head in rail.Heads)
            {
                using SqlCommand command = Command(connection: connection, transaction: transaction, sql:
                    "SELECT ScanOffset,OffsetVersion FROM DBTrackGeometry.dbo.ScanRailheadOffset WHERE PolygonId=@polygon;");
                Add(command: command, name: "@polygon", type: SqlDbType.BigInt, value: head.PolygonId);
                using SqlDataReader reader = await command.ExecuteReaderAsync();
                bool exists = await reader.ReadAsync();
                if (exists ? reader.GetInt64(i: 1) != head.OffsetVersion || reader.GetDouble(i: 0) != head.ScanOffset : head.OffsetVersion != 0 || head.ScanOffset.HasValue)
                    throw new InvalidOperationException(message: "A ScanOffset changed during processing. Refresh and process again. No readings saved.");
                if (!result.CalculateOffsets && !exists)
                    throw new InvalidOperationException(message: $"No stored ScanOffset exists for '{head.PointId}'. Tick Calculate Offset first.");
            }
    }

    private static async Task SaveOffsetsAsync(SqlConnection connection, SqlTransaction transaction, ScanProcessingResult result, DateTime epoch)
    {
        if (!result.CalculateOffsets) return;
        Dictionary<long, ScanProcessingHead> heads = new();
        foreach (ScanProcessingRail rail in result.Rails)
            foreach (ScanProcessingHead head in rail.Heads) heads.Add(key: head.PolygonId, value: head);
        if (result.Offsets.Count != heads.Count || result.Offsets.Select(selector: o => o.PolygonId).Distinct().Count() != heads.Count)
            throw new InvalidOperationException(message: "Offset results do not match the selected railhead polygons.");
        foreach (ScanOffsetReading offset in result.Offsets)
        {
            if (!heads.TryGetValue(key: offset.PolygonId, value: out ScanProcessingHead? head) || head.PointId != offset.PointId || head.ReferenceHeight != offset.ReferenceHeight)
                throw new InvalidOperationException(message: "Offset reference identity changed.");
            if (offset.ScanPointCount == 0) continue; // No replacement is possible; preserve any earlier calibration.
            if (!offset.ScanOffset.HasValue || !offset.RawMean.HasValue || !double.IsFinite(d: offset.ScanOffset.Value) ||
                Math.Abs(value: offset.ScanOffset.Value - ((double)head.ReferenceHeight - offset.RawMean.Value)) > 1e-9d)
                throw new InvalidOperationException(message: "The offset does not equal the reference height minus filtered scan mean.");
            using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
                UPDATE DBTrackGeometry.dbo.ScanRailheadOffset
                SET ScanOffset=@offset,RawMean=@mean,ReferenceHeight=@reference,ScanPointCount=@count,SE=@se,
                    RejectedPointCount=@rejected,CalibrationUtc=@epoch,SourceSha256=@hash,OffsetVersion=OffsetVersion+1,CalibrationId=NULL
                WHERE PolygonId=@polygon;
                IF @@ROWCOUNT=0 INSERT DBTrackGeometry.dbo.ScanRailheadOffset
                    (PolygonId,ScanOffset,RawMean,ReferenceHeight,ScanPointCount,SE,RejectedPointCount,CalibrationUtc,SourceSha256,OffsetVersion)
                    VALUES(@polygon,@offset,@mean,@reference,@count,@se,@rejected,@epoch,@hash,1);
                """);
            Add(command: command, name: "@polygon", type: SqlDbType.BigInt, value: offset.PolygonId);
            Add(command: command, name: "@offset", type: SqlDbType.Float, value: offset.ScanOffset.Value);
            Add(command: command, name: "@mean", type: SqlDbType.Float, value: offset.RawMean.Value);
            Add(command: command, name: "@reference", type: SqlDbType.Decimal, value: offset.ReferenceHeight);
            Add(command: command, name: "@count", type: SqlDbType.BigInt, value: offset.ScanPointCount);
            Add(command: command, name: "@se", type: SqlDbType.Float, value: (object?)offset.StandardError ?? DBNull.Value);
            Add(command: command, name: "@rejected", type: SqlDbType.BigInt, value: offset.RejectedPointCount);
            Add(command: command, name: "@epoch", type: SqlDbType.DateTime2, value: epoch);
            Add(command: command, name: "@hash", type: SqlDbType.Char, value: result.Sha256, size: 64);
            await command.ExecuteNonQueryAsync();
        }
    }
}
#endregion
