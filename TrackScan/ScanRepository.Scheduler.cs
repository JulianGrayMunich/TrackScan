using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Scheduled Scan Database Publication
using System.Data;
using Microsoft.Data.SqlClient;
namespace TrackScan;
public sealed record ScheduledScanCommit(string ArchivePath);
public sealed record ScheduledScanFile(string Hash, string SourcePath, string ArchivePath, DateTime EpochUtc, int MissingCount, bool Archived, bool Logged);
internal sealed partial class ScanRepository
{
    private const string SchedulerSchema = """
        IF OBJECT_ID(N'dbo.ScanScheduledFile',N'U') IS NULL
        CREATE TABLE dbo.ScanScheduledFile(
            ScanProjectId int NOT NULL REFERENCES dbo.ScanProject(ScanProjectId), SourceSha256 char(64) NOT NULL,
            SourcePath nvarchar(2048) NOT NULL, ArchivePath nvarchar(2048) NOT NULL, EpochUtc datetime2(0) NOT NULL,
            MissingCount int NOT NULL, CommittedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            Archived bit NOT NULL DEFAULT 0, Logged bit NOT NULL DEFAULT 0,
            PRIMARY KEY(ScanProjectId,SourceSha256));
        """;
    public async Task<IReadOnlyList<string>> UpdateRailheadNamesAsync(ScanProjectState expected)
    {
        IReadOnlyList<ScanProcessingRail> rails = await LoadProcessingRailsAsync(expected: expected);
        HashSet<string> names = new(comparer: StringComparer.OrdinalIgnoreCase);
        foreach (ScanProcessingRail rail in rails) foreach (ScanProcessingHead head in rail.Heads) names.Add(item: head.PointId);
        List<string> report = new();
        await using SqlConnection connection = Connection(database: DatabaseName); await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        foreach (string name in names)
        {
            if (name.Length > 50) { report.Add(item: $"CONFLICT: {name} exceeds PointName's 50-character limit."); continue; }
            using SqlCommand check = Command(connection: connection, transaction: transaction, sql: "SELECT IsDeleted FROM DBTrackGeometry.dbo.PointName WITH(UPDLOCK,HOLDLOCK) WHERE Project_ID=@project AND PointName=@name;");
            Add(command: check, name: "@project", type: SqlDbType.Int, value: _geometryProjectId); Add(command: check, name: "@name", type: SqlDbType.NVarChar, value: name, size: 50);
            object? value = await check.ExecuteScalarAsync();
            if (value is not null) { report.Add(item: (Convert.ToBoolean(value: value) ? "CONFLICT (deleted name): " : "Existing: ") + name); continue; }
            using SqlCommand insert = Command(connection: connection, transaction: transaction, sql: "INSERT DBTrackGeometry.dbo.PointName(PointName,ReplacementName,Project_ID,IsDeleted) VALUES(@name,@name,@project,0);");
            Add(command: insert, name: "@project", type: SqlDbType.Int, value: _geometryProjectId); Add(command: insert, name: "@name", type: SqlDbType.NVarChar, value: name, size: 50);
            await insert.ExecuteNonQueryAsync(); report.Add(item: "Added: " + name);
        }
        await transaction.CommitAsync();
        if (names.Count == 0) report.Add(item: "No current saved railhead polygons found.");
        return report.AsReadOnly();
    }
    private async Task SaveScheduledGeometryAsync(SqlConnection connection, SqlTransaction transaction, ScanProcessingResult result, ScheduledScanCommit scheduled, int scanProjectId)
    {
        if (result.CalculateOffsets) throw new InvalidOperationException(message: "Scheduled scans must use adopted offsets.");
        using (SqlCommand schema = Command(connection: connection, transaction: transaction, sql: SchedulerSchema)) await schema.ExecuteNonQueryAsync();
        foreach (ScanEpochReading reading in result.Readings)
        {
            using SqlCommand insert = Command(connection: connection, transaction: transaction, sql: """
                DECLARE @point int;
                SELECT @point=PointName_ID FROM DBTrackGeometry.dbo.PointName WITH(UPDLOCK,HOLDLOCK)
                  WHERE Project_ID=@project AND PointName=@name AND IsDeleted=0;
                IF @point IS NULL THROW 51120, 'Railhead name missing or deleted in DBTrackGeometry. Use Update Railhead Names.', 1;
                IF EXISTS(SELECT 1 FROM DBTrackGeometry.dbo.ToREpochs WHERE PointName_ID=@point AND UTCtime=@utc)
                  THROW 51121, 'A ToREpochs reading already exists for this railhead and timestamp. No readings overwritten.', 1;
                INSERT DBTrackGeometry.dbo.ToREpochs(PointName_ID,UTCtime,ToR,IsDeleted) VALUES(@point,@utc,@height,0);
                """);
            Add(command: insert, name: "@project", type: SqlDbType.Int, value: _geometryProjectId);
            Add(command: insert, name: "@name", type: SqlDbType.NVarChar, value: reading.PointId, size: 220);
            Add(command: insert, name: "@utc", type: SqlDbType.DateTime2, value: reading.UtcTimestamp);
            Add(command: insert, name: "@height", type: SqlDbType.Decimal, value: reading.ScanHt.HasValue ? decimal.Round(d: checked((decimal)reading.ScanHt.Value), decimals: 4, mode: MidpointRounding.ToEven) : DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        using SqlCommand ledger = Command(connection: connection, transaction: transaction, sql: "INSERT dbo.ScanScheduledFile(ScanProjectId,SourceSha256,SourcePath,ArchivePath,EpochUtc,MissingCount) VALUES(@project,@hash,@source,@archive,@epoch,@missing);");
        Add(command: ledger, name: "@project", type: SqlDbType.Int, value: scanProjectId);
        Add(command: ledger, name: "@hash", type: SqlDbType.Char, value: result.Sha256, size: 64);
        Add(command: ledger, name: "@source", type: SqlDbType.NVarChar, value: result.SourcePath, size: 2048);
        Add(command: ledger, name: "@archive", type: SqlDbType.NVarChar, value: scheduled.ArchivePath, size: 2048);
        Add(command: ledger, name: "@epoch", type: SqlDbType.DateTime2, value: result.Readings[0].UtcTimestamp);
        Add(command: ledger, name: "@missing", type: SqlDbType.Int, value: result.Readings.Count(predicate: reading => !reading.ScanHt.HasValue));
        await ledger.ExecuteNonQueryAsync();
    }
    public async Task<IReadOnlyList<ScheduledScanFile>> ScheduledFilesAsync(ScanProjectState expected, string? hash = null, bool pendingOnly = false)
    {
        await using SqlConnection connection = Connection(database: DatabaseName); await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        await LockAsync(connection: connection, transaction: transaction, resource: "GNA:ScanEpochSchema");
        using (SqlCommand schema = Command(connection: connection, transaction: transaction, sql: SchedulerSchema)) await schema.ExecuteNonQueryAsync();
        using SqlCommand read = Command(connection: connection, transaction: transaction, sql: "SELECT SourceSha256,SourcePath,ArchivePath,EpochUtc,MissingCount,Archived,Logged FROM dbo.ScanScheduledFile WHERE ScanProjectId=@project AND (@hash IS NULL OR SourceSha256=@hash) AND (@pending=0 OR Logged=0);");
        Add(command: read, name: "@project", type: SqlDbType.Int, value: project.Id);
        Add(command: read, name: "@hash", type: SqlDbType.Char, value: (object?)hash ?? DBNull.Value, size: 64);
        Add(command: read, name: "@pending", type: SqlDbType.Bit, value: pendingOnly);
        List<ScheduledScanFile> rows = new();
        using (SqlDataReader reader = await read.ExecuteReaderAsync()) while (await reader.ReadAsync()) rows.Add(item: new(Hash: reader.GetString(i: 0), SourcePath: reader.GetString(i: 1), ArchivePath: reader.GetString(i: 2), EpochUtc: DateTime.SpecifyKind(value: reader.GetDateTime(i: 3), kind: DateTimeKind.Utc), MissingCount: reader.GetInt32(i: 4), Archived: reader.GetBoolean(i: 5), Logged: reader.GetBoolean(i: 6)));
        await transaction.CommitAsync(); return rows.AsReadOnly();
    }
    public async Task MarkScheduledFileAsync(ScanProjectState expected, string hash, bool logged)
    {
        await using SqlConnection connection = Connection(database: DatabaseName); await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: "UPDATE dbo.ScanScheduledFile SET Archived=1,Logged=@logged WHERE ScanProjectId=@project AND SourceSha256=@hash;");
        Add(command: command, name: "@project", type: SqlDbType.Int, value: expected.ScanProjectId);
        Add(command: command, name: "@hash", type: SqlDbType.Char, value: hash, size: 64); Add(command: command, name: "@logged", type: SqlDbType.Bit, value: logged);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
#endregion
