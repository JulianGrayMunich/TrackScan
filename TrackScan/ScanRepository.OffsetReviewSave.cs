#region Atomic Future Offset Baseline Review Save
using global::TrackScan.Core;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
namespace TrackScan;
internal sealed partial class ScanRepository
{
    public async Task SaveStoredOffsetReviewAsync(ScanProjectState expected, StoredOffsetEditor editor)
    {
        ArgumentNullException.ThrowIfNull(argument: editor);
        if (!editor.IsRecomputed) throw new InvalidOperationException(message: "Recompute mean before saving.");
        IReadOnlyList<StoredOffsetChoice> choices = editor.Choices();
        await using SqlConnection connection = Connection(database: DatabaseName); await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        await LockAsync(connection: connection, transaction: transaction, resource: $"GNA:ScanProject:{_geometryProjectId}");
        StoredOffsetReview current = await ReadStoredOffsetReviewAsync(expected: expected, connection: connection, transaction: transaction);
        if (!current.Points.SequenceEqual(second: editor.Source.Points) || !current.Observations.SequenceEqual(second: editor.Source.Observations))
            throw new InvalidOperationException(message: "Saved offsets or calibration observations have changed. Cancel and reopen Review offsets before saving.");
        StoredOffsetEditor verified = new(source: current);
        Dictionary<(long, Guid, int), bool> selections = new();
        foreach (StoredOffsetChoice choice in choices) selections.Add(key: (choice.PolygonId, choice.CalibrationId, choice.FileIndex), value: choice.Selected);
        foreach (StoredOffsetEditorRow row in verified.Rows) foreach (StoredOffsetCell cell in row.Cells)
        {
            if (cell.Observation is not StoredOffsetObservation observation) continue;
            if (!selections.Remove(key: (row.Point.PolygonId, observation.CalibrationId, observation.FileIndex), value: out bool selected))
                throw new InvalidOperationException(message: "The review selections do not match the saved observations.");
            if (selected && !cell.IsValid) throw new InvalidOperationException(message: "A missing or invalid offset cannot be selected.");
            cell.Selected = selected;
        }
        if (selections.Count != 0) throw new InvalidOperationException(message: "Unknown calibration selection.");
        verified.Recompute();
        if (!verified.HasChanges) { await transaction.CommitAsync(); return; }
        foreach (StoredOffsetEditorRow row in verified.Rows)
        {
            if (!row.HasChanges) continue;
            HashSet<string> hashes = new(comparer: StringComparer.OrdinalIgnoreCase);
            foreach (StoredOffsetCell cell in row.Cells)
                if (cell.Selected && cell.Observation is StoredOffsetObservation observation && !hashes.Add(item: observation.SourceSha256))
                    throw new InvalidOperationException(message: $"'{row.PointId}' has the same LAS scan selected more than once. Select only one observation from that scan.");
        }
        using (SqlCommand schema = Command(connection: connection, transaction: transaction, sql: """
            IF OBJECT_ID(N'DBTrackGeometry.dbo.ScanOffsetReviewChoice',N'U') IS NULL
            EXEC(N'CREATE TABLE DBTrackGeometry.dbo.ScanOffsetReviewChoice(
                PolygonId bigint NOT NULL,OffsetVersion bigint NOT NULL,CalibrationId uniqueidentifier NOT NULL,FileIndex int NOT NULL,
                GeometryProjectId int NOT NULL,Selected bit NOT NULL,ReviewId uniqueidentifier NOT NULL,ReviewedUtc datetime2(7) NOT NULL,
                PreviousOffset float NULL,NewOffset float NULL,
                PRIMARY KEY(PolygonId,OffsetVersion,CalibrationId,FileIndex));');
            """)) await schema.ExecuteNonQueryAsync();
        Guid reviewId = Guid.NewGuid(); DateTime utc = DateTime.UtcNow;
        foreach (StoredOffsetEditorRow row in verified.Rows)
        {
            if (!row.HasChanges) continue;
            using SqlCommand previousVersion = Command(connection: connection, transaction: transaction,
                sql: "SELECT ISNULL(MAX(OffsetVersion),0) FROM DBTrackGeometry.dbo.ScanOffsetReviewChoice WHERE PolygonId=@polygon;");
            Add(command: previousVersion, name: "@polygon", type: SqlDbType.BigInt, value: row.Point.PolygonId);
            long lastReviewVersion = (long)(await previousVersion.ExecuteScalarAsync() ?? 0L);
            long version = checked(Math.Max(val1: row.Point.OffsetVersion, val2: lastReviewVersion) + 1);
            if (row.Mean.HasValue)
            {
                List<string> hashes = new(); foreach (StoredOffsetCell cell in row.Cells) if (cell.Selected) hashes.Add(item: cell.Observation!.SourceSha256);
                string hash = Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: string.Join(separator: "\n", values: hashes))));
                using SqlCommand update = Command(connection: connection, transaction: transaction, sql: """
                    UPDATE DBTrackGeometry.dbo.ScanRailheadOffset SET ScanOffset=@mean,RawMean=@raw,ReferenceHeight=@reference,
                        ScanPointCount=@count,SE=@se,RejectedPointCount=@rejected,CalibrationUtc=@utc,SourceSha256=@hash,OffsetVersion=@version
                        WHERE PolygonId=@polygon;
                    IF @@ROWCOUNT=0 INSERT DBTrackGeometry.dbo.ScanRailheadOffset
                        (PolygonId,ScanOffset,RawMean,ReferenceHeight,ScanPointCount,SE,RejectedPointCount,CalibrationUtc,SourceSha256,OffsetVersion,CalibrationId)
                        VALUES(@polygon,@mean,@raw,@reference,@count,@se,@rejected,@utc,@hash,@version,NULL);
                    """);
                Add(command: update, name: "@polygon", type: SqlDbType.BigInt, value: row.Point.PolygonId);
                Add(command: update, name: "@mean", type: SqlDbType.Float, value: row.Mean.Value);
                Add(command: update, name: "@raw", type: SqlDbType.Float, value: (double)row.Point.ReferenceHeight - row.Mean.Value);
                Add(command: update, name: "@reference", type: SqlDbType.Decimal, value: row.Point.ReferenceHeight);
                Add(command: update, name: "@count", type: SqlDbType.BigInt, value: row.PointCount);
                Add(command: update, name: "@se", type: SqlDbType.Float, value: (object?)row.StandardError ?? DBNull.Value);
                Add(command: update, name: "@rejected", type: SqlDbType.BigInt, value: row.RejectedCount);
                Add(command: update, name: "@utc", type: SqlDbType.DateTime2, value: utc);
                Add(command: update, name: "@hash", type: SqlDbType.Char, value: hash, size: 64);
                Add(command: update, name: "@version", type: SqlDbType.BigInt, value: version);
                await update.ExecuteNonQueryAsync();
            }
            else
            {
                using SqlCommand retain = Command(connection: connection, transaction: transaction,
                    sql: "DELETE FROM DBTrackGeometry.dbo.ScanRailheadOffset WHERE PolygonId=@polygon;");
                Add(command: retain, name: "@version", type: SqlDbType.BigInt, value: version);
                Add(command: retain, name: "@polygon", type: SqlDbType.BigInt, value: row.Point.PolygonId);
                await retain.ExecuteNonQueryAsync();
            }
            foreach (StoredOffsetCell cell in row.Cells)
            {
                if (cell.Observation is not StoredOffsetObservation observation) continue;
                using SqlCommand audit = Command(connection: connection, transaction: transaction, sql: """
                    INSERT DBTrackGeometry.dbo.ScanOffsetReviewChoice
                    (PolygonId,OffsetVersion,CalibrationId,FileIndex,GeometryProjectId,Selected,ReviewId,ReviewedUtc,PreviousOffset,NewOffset)
                    VALUES(@polygon,@version,@calibration,@file,@geometry,@selected,@review,@utc,@old,@new);
                    """);
                Add(command: audit, name: "@polygon", type: SqlDbType.BigInt, value: row.Point.PolygonId);
                Add(command: audit, name: "@version", type: SqlDbType.BigInt, value: version);
                Add(command: audit, name: "@calibration", type: SqlDbType.UniqueIdentifier, value: observation.CalibrationId);
                Add(command: audit, name: "@file", type: SqlDbType.Int, value: observation.FileIndex);
                Add(command: audit, name: "@geometry", type: SqlDbType.Int, value: _geometryProjectId);
                Add(command: audit, name: "@selected", type: SqlDbType.Bit, value: cell.Selected);
                Add(command: audit, name: "@review", type: SqlDbType.UniqueIdentifier, value: reviewId);
                Add(command: audit, name: "@utc", type: SqlDbType.DateTime2, value: utc);
                Add(command: audit, name: "@old", type: SqlDbType.Float, value: (object?)row.ExistingMean ?? DBNull.Value);
                Add(command: audit, name: "@new", type: SqlDbType.Float, value: (object?)row.Mean ?? DBNull.Value);
                await audit.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }
}
#endregion
