using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Calibration History And Atomic Baseline Adoption
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
namespace TrackScan;
internal sealed partial class ScanRepository
{
    private const string CalibrationSchema = """
        USE DBTrackGeometry;
        IF OBJECT_ID(N'dbo.ScanCalibrationBatch',N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.ScanCalibrationBatch(
                CalibrationId uniqueidentifier NOT NULL PRIMARY KEY, GeometryProjectId int NOT NULL,
                AdoptedUtc datetime2(7) NOT NULL, AlgorithmVersion nvarchar(40) NOT NULL);
            CREATE TABLE dbo.ScanCalibrationFile(
                CalibrationId uniqueidentifier NOT NULL REFERENCES dbo.ScanCalibrationBatch(CalibrationId),
                FileIndex int NOT NULL, SourceFilePath nvarchar(2048) NOT NULL, SourceSha256 char(64) NOT NULL,
                ObservationUtc datetime2(0) NOT NULL, TimeZoneId nvarchar(200) NOT NULL, TimestampSource nvarchar(32) NOT NULL,
                RejectionCount int NOT NULL, PRIMARY KEY(CalibrationId,FileIndex), UNIQUE(CalibrationId,SourceSha256));
            CREATE TABLE dbo.ScanCalibrationObservation(
                CalibrationId uniqueidentifier NOT NULL, FileIndex int NOT NULL,
                PolygonId bigint NOT NULL REFERENCES dbo.ScanPolygonDefinition(PolygonId) ON DELETE CASCADE,
                PointId nvarchar(220) NOT NULL, ReferenceHeight decimal(24,4) NOT NULL,
                RawMean float NULL, ScanOffset float NULL, ScanPointCount bigint NOT NULL, SE float NULL,
                RejectedPointCount bigint NOT NULL, Included bit NOT NULL, ReviewStatus nvarchar(2000) NOT NULL,
                PRIMARY KEY(CalibrationId,FileIndex,PolygonId),
                FOREIGN KEY(CalibrationId,FileIndex) REFERENCES dbo.ScanCalibrationFile(CalibrationId,FileIndex));
            CREATE TABLE dbo.ScanCalibrationBaseline(
                CalibrationId uniqueidentifier NOT NULL REFERENCES dbo.ScanCalibrationBatch(CalibrationId),
                PolygonId bigint NOT NULL REFERENCES dbo.ScanPolygonDefinition(PolygonId) ON DELETE CASCADE,
                PreviousOffset float NULL, MeanOffset float NULL, MinimumOffset float NULL, MaximumOffset float NULL,
                StandardDeviation float NULL, ObservationCount int NOT NULL, OffsetVersion bigint NOT NULL,
                PRIMARY KEY(CalibrationId,PolygonId));
        END;
        IF COL_LENGTH(N'dbo.ScanRailheadOffset',N'CalibrationId') IS NULL
            ALTER TABLE dbo.ScanRailheadOffset ADD CalibrationId uniqueidentifier NULL;
        """;

    public async Task AdoptCalibrationAsync(ScanProjectState expected, ScanCalibrationReview review)
    {
        ArgumentNullException.ThrowIfNull(argument: review);
        // Reconstruct from immutable scan outputs, then copy only the review decisions.
        ScanCalibrationReview verified = new(results: review.Results);
        for (int row = 0; row < verified.Rows.Count; row++)
            for (int file = 0; file < verified.Results.Count; file++)
                verified.Rows[row].Cells[file].Included = review.Rows[row].Cells[file].Included;
        if (!verified.Rows.Any(predicate: row => row.Mean.HasValue)) throw new InvalidOperationException(message: "No valid included calibration offsets to adopt.");
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        foreach (ScanProcessingResult result in verified.Results)
            await ValidateProcessingSnapshotAsync(expected: expected, result: result, connection: connection, transaction: transaction);
        // All selected snapshots must have exactly the same geometry and prior baseline.
        Dictionary<long, ScanProcessingHead> original = new();
        foreach (ScanProcessingRail rail in verified.Results[0].Rails)
            foreach (ScanProcessingHead head in rail.Heads) original.Add(key: head.PolygonId, value: head);
        foreach (ScanProcessingResult result in verified.Results)
        {
            int count = 0;
            foreach (ScanProcessingRail rail in result.Rails)
                foreach (ScanProcessingHead head in rail.Heads)
                {
                    count++;
                    if (!original.TryGetValue(key: head.PolygonId, value: out var first) || first.PointId != head.PointId || first.ReferenceHeight != head.ReferenceHeight || first.ScanOffset != head.ScanOffset || first.OffsetVersion != head.OffsetVersion)
                        throw new InvalidOperationException(message: "Calibration geometry or baseline snapshots differ.");
                }
            if (count != original.Count) throw new InvalidOperationException(message: "Calibration point sets differ.");
        }
        using (SqlCommand schema = Command(connection: connection, transaction: transaction, sql: CalibrationSchema)) await schema.ExecuteNonQueryAsync();
        DateTime adopted = DateTime.UtcNow;
        using (SqlCommand batch = Command(connection: connection, transaction: transaction, sql: "INSERT DBTrackGeometry.dbo.ScanCalibrationBatch VALUES(@id,@project,@utc,N'101-1mm-equal-scan-mean');"))
        {
            Add(command: batch, name: "@id", type: SqlDbType.UniqueIdentifier, value: review.BatchId);
            Add(command: batch, name: "@project", type: SqlDbType.Int, value: _geometryProjectId);
            Add(command: batch, name: "@utc", type: SqlDbType.DateTime2, value: adopted); await batch.ExecuteNonQueryAsync();
        }
        for (int file = 0; file < verified.Results.Count; file++)
        {
            ScanProcessingResult result = verified.Results[file];
            using SqlCommand insert = Command(connection: connection, transaction: transaction, sql: "INSERT DBTrackGeometry.dbo.ScanCalibrationFile VALUES(@id,@index,@path,@hash,@epoch,@zone,@source,@rejection);");
            Add(command: insert, name: "@id", type: SqlDbType.UniqueIdentifier, value: review.BatchId);
            Add(command: insert, name: "@index", type: SqlDbType.Int, value: file);
            Add(command: insert, name: "@path", type: SqlDbType.NVarChar, value: result.SourcePath, size: 2048);
            Add(command: insert, name: "@hash", type: SqlDbType.Char, value: result.Sha256, size: 64);
            Add(command: insert, name: "@epoch", type: SqlDbType.DateTime2, value: result.Readings[0].UtcTimestamp);
            Add(command: insert, name: "@zone", type: SqlDbType.NVarChar, value: result.TimeZoneId, size: 200);
            Add(command: insert, name: "@source", type: SqlDbType.NVarChar, value: result.TimestampSource, size: 32);
            Add(command: insert, name: "@rejection", type: SqlDbType.Int, value: result.RejectionCount); await insert.ExecuteNonQueryAsync();
        }
        string combinedHash = Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: string.Join(separator: "\n", values: verified.Results.Select(selector: result => result.Sha256)))));
        foreach (ScanCalibrationRow row in verified.Rows)
        {
            int included = 0; long pointCount = 0, rejected = 0;
            for (int file = 0; file < row.Cells.Count; file++)
            {
                ScanCalibrationCell cell = row.Cells[file]; ScanOffsetReading observation = cell.Observation;
                if (cell.Included) { included++; pointCount = checked(pointCount + observation.ScanPointCount); rejected = checked(rejected + observation.RejectedPointCount); }
                using SqlCommand insert = Command(connection: connection, transaction: transaction, sql: """
                    INSERT DBTrackGeometry.dbo.ScanCalibrationObservation
                    VALUES(@id,@index,@polygon,@point,@reference,@raw,@offset,@count,@se,@rejected,@included,@status);
                    """);
                Add(command: insert, name: "@id", type: SqlDbType.UniqueIdentifier, value: review.BatchId);
                Add(command: insert, name: "@index", type: SqlDbType.Int, value: file);
                Add(command: insert, name: "@polygon", type: SqlDbType.BigInt, value: row.PolygonId);
                Add(command: insert, name: "@point", type: SqlDbType.NVarChar, value: row.PointId, size: 220);
                Add(command: insert, name: "@reference", type: SqlDbType.Decimal, value: row.ReferenceHeight);
                Add(command: insert, name: "@raw", type: SqlDbType.Float, value: (object?)observation.RawMean ?? DBNull.Value);
                Add(command: insert, name: "@offset", type: SqlDbType.Float, value: (object?)cell.Offset ?? DBNull.Value);
                Add(command: insert, name: "@count", type: SqlDbType.BigInt, value: observation.ScanPointCount);
                Add(command: insert, name: "@se", type: SqlDbType.Float, value: (object?)observation.StandardError ?? DBNull.Value);
                Add(command: insert, name: "@rejected", type: SqlDbType.BigInt, value: observation.RejectedPointCount);
                Add(command: insert, name: "@included", type: SqlDbType.Bit, value: cell.Included);
                Add(command: insert, name: "@status", type: SqlDbType.NVarChar, value: cell.IsValid ? (cell.Included ? "Included" : "Excluded during calibration review") : cell.Status, size: 2000);
                await insert.ExecuteNonQueryAsync();
            }
            long version = original[row.PolygonId].OffsetVersion;
            if (row.Mean.HasValue)
            {
                version++;
                using SqlCommand update = Command(connection: connection, transaction: transaction, sql: """
                    UPDATE DBTrackGeometry.dbo.ScanRailheadOffset SET ScanOffset=@mean,RawMean=@raw,ReferenceHeight=@ref,
                        ScanPointCount=@count,SE=@se,RejectedPointCount=@rejected,CalibrationUtc=@utc,SourceSha256=@hash,
                        OffsetVersion=@version,CalibrationId=@id WHERE PolygonId=@polygon;
                    IF @@ROWCOUNT=0 INSERT DBTrackGeometry.dbo.ScanRailheadOffset
                        (PolygonId,ScanOffset,RawMean,ReferenceHeight,ScanPointCount,SE,RejectedPointCount,CalibrationUtc,SourceSha256,OffsetVersion,CalibrationId)
                        VALUES(@polygon,@mean,@raw,@ref,@count,@se,@rejected,@utc,@hash,@version,@id);
                    """);
                Add(command: update, name: "@polygon", type: SqlDbType.BigInt, value: row.PolygonId);
                Add(command: update, name: "@mean", type: SqlDbType.Float, value: row.Mean.Value);
                Add(command: update, name: "@raw", type: SqlDbType.Float, value: (double)row.ReferenceHeight - row.Mean.Value);
                Add(command: update, name: "@ref", type: SqlDbType.Decimal, value: row.ReferenceHeight);
                Add(command: update, name: "@count", type: SqlDbType.BigInt, value: pointCount);
                Add(command: update, name: "@se", type: SqlDbType.Float, value: row.StandardDeviation.HasValue ? row.StandardDeviation.Value / Math.Sqrt(d: included) : DBNull.Value);
                Add(command: update, name: "@rejected", type: SqlDbType.BigInt, value: rejected);
                Add(command: update, name: "@utc", type: SqlDbType.DateTime2, value: adopted);
                Add(command: update, name: "@hash", type: SqlDbType.Char, value: combinedHash, size: 64);
                Add(command: update, name: "@version", type: SqlDbType.BigInt, value: version);
                Add(command: update, name: "@id", type: SqlDbType.UniqueIdentifier, value: review.BatchId); await update.ExecuteNonQueryAsync();
            }
            using SqlCommand baseline = Command(connection: connection, transaction: transaction, sql: "INSERT DBTrackGeometry.dbo.ScanCalibrationBaseline VALUES(@id,@polygon,@previous,@mean,@min,@max,@sd,@count,@version);");
            Add(command: baseline, name: "@id", type: SqlDbType.UniqueIdentifier, value: review.BatchId);
            Add(command: baseline, name: "@polygon", type: SqlDbType.BigInt, value: row.PolygonId);
            Add(command: baseline, name: "@previous", type: SqlDbType.Float, value: (object?)row.ExistingOffset ?? DBNull.Value);
            Add(command: baseline, name: "@mean", type: SqlDbType.Float, value: (object?)row.Mean ?? DBNull.Value);
            Add(command: baseline, name: "@min", type: SqlDbType.Float, value: (object?)row.Minimum ?? DBNull.Value);
            Add(command: baseline, name: "@max", type: SqlDbType.Float, value: (object?)row.Maximum ?? DBNull.Value);
            Add(command: baseline, name: "@sd", type: SqlDbType.Float, value: (object?)row.StandardDeviation ?? DBNull.Value);
            Add(command: baseline, name: "@count", type: SqlDbType.Int, value: included);
            Add(command: baseline, name: "@version", type: SqlDbType.BigInt, value: version); await baseline.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
#endregion
