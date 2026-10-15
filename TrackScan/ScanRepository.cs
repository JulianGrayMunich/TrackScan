using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
#endregion

namespace TrackScan;

#region Scan Database Repository
internal sealed partial class ScanRepository
{
    public const string DatabaseName = "DBTrackScan";
    private const int SqlTimeoutSeconds = 120;
    private readonly string _baseConnectionString;
    private readonly int _geometryProjectId;

    public ScanRepository(string baseConnectionString, int geometryProjectId)
    {
        _baseConnectionString = baseConnectionString ?? throw new ArgumentNullException(paramName: nameof(baseConnectionString));
        _geometryProjectId = geometryProjectId;
    }

    #region Connections And Schema
    private SqlConnection Connection(string database, bool pooling = true)
    {
        SqlConnectionStringBuilder builder = new(connectionString: _baseConnectionString) { InitialCatalog = database, Pooling = pooling };
        return new SqlConnection(connectionString: builder.ConnectionString);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
        => new(cmdText: sql, connection: connection, transaction: transaction) { CommandTimeout = SqlTimeoutSeconds };

    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        SqlParameter parameter = new(parameterName: name, dbType: type) { Value = value };
        if (size != 0) parameter.Size = size;
        if (type == SqlDbType.Decimal) { parameter.Precision = 24; parameter.Scale = 4; }
        command.Parameters.Add(value: parameter);
    }

    private static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, string resource)
    {
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            DECLARE @r int;
            EXEC @r = sys.sp_getapplock @Resource=@resource, @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=10000;
            IF @r < 0 THROW 51063, 'Scan preparation is busy in another application. Please retry.', 1;
            """);
        Add(command: command, name: "@resource", type: SqlDbType.NVarChar, value: resource, size: 255);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> DatabaseExistsAsync()
    {
        await using SqlConnection master = Connection(database: "master");
        await master.OpenAsync();
        using SqlCommand command = Command(connection: master, transaction: null,
            sql: "SELECT CASE WHEN DB_ID(N'DBTrackScan') IS NULL THEN 0 ELSE 1 END;");
        return (int)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException(message: "Unable to check DBTrackScan existence.")) == 1;
    }

    public async Task CreateDatabaseAsync(bool recreateExistingDatabase = false)
    {
        // Hold the session lock through schema installation. Disabling pooling guarantees
        // that disposal releases the SQL session lock even when database creation fails.
        await using SqlConnection master = Connection(database: "master", pooling: false);
        await master.OpenAsync();
        bool databaseCreated;
        using (SqlCommand create = Command(connection: master, transaction: null, sql: """
                DECLARE @r int;
                DECLARE @created bit=0;
                EXEC @r = sys.sp_getapplock @Resource=N'GNA:CreateDBTrackScan', @LockMode=N'Exclusive',
                    @LockOwner=N'Session', @LockTimeout=10000;
                IF @r < 0 THROW 51063, 'DBTrackScan creation is busy. Please retry.', 1;
                BEGIN TRY
                    IF @recreate=1 AND DB_ID(N'DBTrackScan') IS NOT NULL
                    BEGIN
                        ALTER DATABASE [DBTrackScan] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [DBTrackScan];
                    END;
                    IF DB_ID(N'DBTrackScan') IS NULL
                    BEGIN
                        EXEC(N'CREATE DATABASE [DBTrackScan]');
                        SET @created=1;
                    END;
                END TRY
                BEGIN CATCH
                    -- If DROP failed, do not leave the surviving database in single-user mode.
                    IF @recreate=1 AND DB_ID(N'DBTrackScan') IS NOT NULL
                    BEGIN TRY
                        ALTER DATABASE [DBTrackScan] SET MULTI_USER;
                    END TRY
                    BEGIN CATCH
                        PRINT N'Unable to restore DBTrackScan multi-user mode.';
                    END CATCH;
                    THROW;
                END CATCH;
                SELECT CONVERT(int,@created);
                """))
        {
            Add(command: create, name: "@recreate", type: SqlDbType.Bit, value: recreateExistingDatabase);
            databaseCreated = (int)(await create.ExecuteScalarAsync()
                ?? throw new InvalidOperationException(message: "Cannot confirm DBTrackScan creation.")) == 1;
        }
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        await EnsurePreparationSchemaAsync(connection: connection, allowCreate: true);
        if (databaseCreated)
        {
            // Reduction tables are installed only in a newly created database.
            // Opening an existing database must not perform this schema update.
            using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
            await LockAsync(connection: connection, transaction: transaction, resource: "GNA:ScanEpochSchema");
            using SqlCommand epochs = Command(connection: connection, transaction: transaction, sql: EpochSchema);
            await epochs.ExecuteNonQueryAsync();
            using SqlCommand reductions = Command(connection: connection, transaction: transaction, sql: ReductionCreationSchema);
            await reductions.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
    }

    private static async Task EnsurePreparationSchemaAsync(SqlConnection connection, bool allowCreate)
    {
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        await LockAsync(connection: connection, transaction: transaction, resource: "GNA:ScanSchema");
        using SqlCommand check = Command(connection: connection, transaction: transaction, sql: """
            IF OBJECT_ID(N'dbo.ScanSchemaVersion',N'U') IS NULL SELECT 0;
            ELSE EXEC(N'SELECT CASE WHEN COUNT(*)=1 THEN MAX(VersionNumber) ELSE -1 END FROM dbo.ScanSchemaVersion');
            """);
        int version = (int)(await check.ExecuteScalarAsync() ?? throw new InvalidOperationException(message: "Cannot identify the provisional scan schema."));
        if (version == 3) { await transaction.CommitAsync(); return; }
        if ((version == 0 && !allowCreate) || (version != 0 && version != 1 && version != 2))
            throw new InvalidOperationException(message: "Create DBTrackScan first, or use a supported provisional scan schema.");
        if (version < 2)
            await ExecuteSchemaResourceAsync(connection: connection, transaction: transaction,
                resource: version == 1 ? "TrackScan.DBTrackScan.Upgrade.sql" : "TrackScan.DBTrackScan.sql");
        await ExecuteSchemaResourceAsync(connection: connection, transaction: transaction, resource: "TrackScan.DBTrackScan.Computation.sql");
        await transaction.CommitAsync();
    }

    private static async Task ExecuteSchemaResourceAsync(SqlConnection connection, SqlTransaction transaction, string resource)
    {
        using Stream stream = typeof(ScanRepository).Assembly.GetManifestResourceStream(name: resource)
            ?? throw new InvalidOperationException(message: "The DBTrackScan schema resource is missing.");
        using StreamReader reader = new(stream: stream);
        using SqlCommand schema = Command(connection: connection, transaction: transaction, sql: await reader.ReadToEndAsync());
        await schema.ExecuteNonQueryAsync();
    }

    private async Task<(int Id, long Revision, string Name, Guid DatabaseIdentity)> ResolveProjectAsync(SqlConnection connection, SqlTransaction transaction)
    {
        if (_geometryProjectId <= 0) throw new InvalidOperationException(message: "Select an active project first.");
        await LockAsync(connection: connection, transaction: transaction, resource: $"GNA:ScanProject:{_geometryProjectId}");
        // The scan database resides on the same SQL instance as DBTrackGeometry.
        // Database creation time prevents reassociation after a geometry database recreation.
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            IF OBJECT_ID(N'dbo.ScanSchemaVersion', N'U') IS NULL
                THROW 51063, 'Create DBTrackScan from the Database tab first.', 1;
            IF NOT EXISTS(SELECT 1 FROM dbo.ScanSchemaVersion WHERE VersionNumber=3)
                THROW 51063, 'Unsupported DBTrackScan schema version.', 1;
            DECLARE @name nvarchar(200), @created datetime2(3);
            SELECT @name=ProjectName FROM DBTrackGeometry.dbo.Project WITH (HOLDLOCK)
                WHERE Project_ID=@geometryId AND IsDeleted=0;
            IF @name IS NULL THROW 51063, 'The active geometry project is no longer available. Refresh the project selection.', 1;
            SELECT @created=create_date FROM sys.databases WHERE name=N'DBTrackGeometry';
            IF @created IS NULL THROW 51063, 'Cannot identify DBTrackGeometry.', 1;
            IF NOT EXISTS(SELECT 1 FROM dbo.ScanProject WHERE GeometryProjectId=@geometryId AND GeometryDatabaseCreated=@created)
                INSERT dbo.ScanProject(GeometryProjectId, GeometryDatabaseCreated, ProjectName) VALUES(@geometryId,@created,@name);
            UPDATE dbo.ScanProject SET ProjectName=@name
                WHERE GeometryProjectId=@geometryId AND GeometryDatabaseCreated=@created AND ProjectName<>@name;
            SELECT ScanProjectId, PreparationRevision, ProjectName,
                (SELECT service_broker_guid FROM sys.databases WHERE database_id=DB_ID()) AS DatabaseIdentity FROM dbo.ScanProject
                WHERE GeometryProjectId=@geometryId AND GeometryDatabaseCreated=@created;
            """);
        Add(command: command, name: "@geometryId", type: SqlDbType.Int, value: _geometryProjectId);
        using SqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException(message: "Unable to resolve the scan project.");
        return (reader.GetInt32(i: 0), reader.GetInt64(i: 1), reader.GetString(i: 2), reader.GetGuid(i: 3));
    }
    #endregion

    #region Read Preparation State
    public async Task<ScanProjectState> LoadAsync()
    {
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        await EnsurePreparationSchemaAsync(connection: connection, allowCreate: false);
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        List<ScanTrack> tracks = await ReadActiveTracksAsync(connection: connection, transaction: transaction, projectId: project.Id);
        string survey = "No Railhead survey imported.";
        bool hasSurvey = false;
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            SELECT s.SourceFileName, s.PointCount, s.ImportedUtc FROM dbo.ScanProject p
            JOIN dbo.ScanReferenceSurvey s ON s.SurveyId=p.CurrentSurveyId WHERE p.ScanProjectId=@project;
            """))
        {
            Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
            using SqlDataReader reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                hasSurvey = true;
                survey = $"{reader.GetString(i: 0)} — {reader.GetInt64(i: 1):N0} points — {reader.GetDateTime(i: 2):yyyy-MM-dd HH:mm:ss} UTC";
            }
        }
        await transaction.CommitAsync();
        return new ScanProjectState(ScanProjectId: project.Id, Revision: project.Revision, ProjectName: project.Name, SurveyDescription: survey, Tracks: tracks)
        { DatabaseIdentity = project.DatabaseIdentity, HasSurvey = hasSurvey };
    }

    private static void VerifyRevision(ScanProjectState expected, int projectId, long revision, Guid databaseIdentity)
    {
        if (expected.DatabaseIdentity != databaseIdentity || expected.ScanProjectId != projectId || expected.Revision != revision)
            throw new InvalidOperationException(message: "Scan preparation changed in another operation. Refresh and review it before saving.");
    }

    private static async Task RecordChangeAsync(SqlConnection connection, SqlTransaction transaction, int projectId, string kind, string details)
    {
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            UPDATE dbo.ScanProject SET PreparationRevision=PreparationRevision+1, ModifiedUtc=SYSUTCDATETIME() WHERE ScanProjectId=@project;
            INSERT dbo.ScanPreparationChange(ScanProjectId, PreparationRevision, ChangeKind, Details)
                SELECT ScanProjectId, PreparationRevision, @kind, @details FROM dbo.ScanProject WHERE ScanProjectId=@project;
            """);
        Add(command: command, name: "@project", type: SqlDbType.Int, value: projectId);
        Add(command: command, name: "@kind", type: SqlDbType.NVarChar, value: kind, size: 40);
        Add(command: command, name: "@details", type: SqlDbType.NVarChar, value: details, size: -1);
        await command.ExecuteNonQueryAsync();
    }
    #endregion

    #region Read Active Tracks Within The Project Transaction
    private static async Task<List<ScanTrack>> ReadActiveTracksAsync(SqlConnection connection, SqlTransaction transaction, int projectId)
    {
        List<ScanTrack> tracks = new();
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            SELECT TrackId, TrackSlot, TrackName, LeftStartE, LeftStartN, RightStartE, RightStartN,
                LeftEndE, LeftEndN, RightEndE, RightEndN, GaugeMillimetres, PointSpacing
            FROM dbo.ScanTrack WHERE ScanProjectId=@project AND IsDeleted=0 ORDER BY TrackSlot;
            """))
        {
            Add(command: command, name: "@project", type: SqlDbType.Int, value: projectId);
            using SqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tracks.Add(item: new ScanTrack(TrackId: reader.GetInt32(i: 0), Slot: reader.GetByte(i: 1), Name: reader.GetString(i: 2),
                    LeftStartE: reader.GetDecimal(i: 3), LeftStartN: reader.GetDecimal(i: 4), RightStartE: reader.GetDecimal(i: 5), RightStartN: reader.GetDecimal(i: 6),
                    LeftEndE: reader.GetDecimal(i: 7), LeftEndN: reader.GetDecimal(i: 8), RightEndE: reader.GetDecimal(i: 9), RightEndN: reader.GetDecimal(i: 10), GaugeMillimetres: reader.GetInt32(i: 11), PointSpacing: reader.GetDecimal(i: 12)));
        }
        return tracks;
    }
    #endregion

    #region Track Save And Delete
    public Task SaveTrackAsync(ScanProjectState expected, ScanTrack track, bool delete) =>
        SaveTrackCoreAsync(expected: expected, track: track, delete: delete, reviewedEndpoints: false);

    public Task SaveReviewedTrackAsync(ScanProjectState expected, ScanTrack track) =>
        SaveTrackCoreAsync(expected: expected, track: track, delete: false, reviewedEndpoints: true);

    private async Task SaveTrackCoreAsync(ScanProjectState expected, ScanTrack track, bool delete, bool reviewedEndpoints)
    {
        if (!delete)
        {
            track = ScanEngine.RoundTrackCoordinates(track: track with { PointSpacing = ScanEngine.FixedRailheadSpacingMetres });
            ScanCoordinates.ValidateTrack(track: track, roundedCoordinates: true);
        }
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        int originalTrackId = track.TrackId;
        if (!delete)
        {
            // Read under the same serializable transaction as the write. Never rely
            // solely on the interface's cached list when enforcing uniqueness.
            List<ScanTrack> currentTracks = await ReadActiveTracksAsync(connection: connection, transaction: transaction, projectId: project.Id);
            ScanTrackDuplicates.RejectDuplicate(candidate: track, existingTracks: currentTracks);
        }
        string sql = delete ? """
            UPDATE dbo.ScanTrack SET IsDeleted=1, ModifiedUtc=SYSUTCDATETIME()
                WHERE ScanProjectId=@project AND TrackId=@id AND IsDeleted=0;
            IF @@ROWCOUNT<>1 THROW 51063, 'The selected track is no longer available.', 1;
            """ : """
            IF EXISTS(SELECT 1 FROM dbo.ScanTrack WHERE ScanProjectId=@project AND IsDeleted=0 AND TrackName=@name AND TrackId<>@id)
                THROW 51063, 'A track with this name already exists in this project.', 1;
            IF @id=0
            BEGIN
                DECLARE @slot tinyint;
                SELECT @slot=MIN(v.n) FROM (VALUES(1),(2),(3),(4),(5),(6)) v(n)
                    WHERE NOT EXISTS(SELECT 1 FROM dbo.ScanTrack t WHERE t.ScanProjectId=@project AND t.IsDeleted=0 AND t.TrackSlot=v.n);
                IF @slot IS NULL THROW 51063, 'A project can contain at most six active tracks.', 1;
                INSERT dbo.ScanTrack(ScanProjectId,TrackSlot,TrackName,LeftStartE,LeftStartN,RightStartE,RightStartN,LeftEndE,LeftEndN,RightEndE,RightEndN,GaugeMillimetres,PointSpacing)
                    VALUES(@project,@slot,@name,@lse,@lsn,@rse,@rsn,@lee,@len,@ree,@ren,@gauge,@spacing);
            END
            ELSE
            BEGIN
                UPDATE dbo.ScanTrack SET TrackName=@name,LeftStartE=@lse,LeftStartN=@lsn,RightStartE=@rse,RightStartN=@rsn,
                    LeftEndE=@lee,LeftEndN=@len,RightEndE=@ree,RightEndN=@ren,GaugeMillimetres=@gauge,PointSpacing=@spacing,ModifiedUtc=SYSUTCDATETIME()
                    WHERE ScanProjectId=@project AND TrackId=@id AND IsDeleted=0;
                IF @@ROWCOUNT<>1 THROW 51063, 'The selected track is no longer available.', 1;
            END;
            """;
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: sql);
        Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
        Add(command: command, name: "@id", type: SqlDbType.Int, value: track.TrackId);
        Add(command: command, name: "@name", type: SqlDbType.NVarChar, value: track.Name.Trim(), size: 200);
        Add(command: command, name: "@spacing", type: SqlDbType.Decimal, value: track.PointSpacing);
        Add(command: command, name: "@gauge", type: SqlDbType.Int, value: track.GaugeMillimetres);
        Add(command: command, name: "@lse", type: SqlDbType.Decimal, value: track.LeftStartE);
        Add(command: command, name: "@lsn", type: SqlDbType.Decimal, value: track.LeftStartN);
        Add(command: command, name: "@rse", type: SqlDbType.Decimal, value: track.RightStartE);
        Add(command: command, name: "@rsn", type: SqlDbType.Decimal, value: track.RightStartN);
        Add(command: command, name: "@lee", type: SqlDbType.Decimal, value: track.LeftEndE);
        Add(command: command, name: "@len", type: SqlDbType.Decimal, value: track.LeftEndN);
        Add(command: command, name: "@ree", type: SqlDbType.Decimal, value: track.RightEndE);
        Add(command: command, name: "@ren", type: SqlDbType.Decimal, value: track.RightEndN);
        await command.ExecuteNonQueryAsync();
        if (track.TrackId != 0)
            await InvalidateComputedRunsAsync(connection: connection, transaction: transaction, projectId: project.Id, trackId: track.TrackId);
        ScanTrack? previous = null;
        foreach (ScanTrack item in expected.Tracks) if (item.TrackId == originalTrackId) previous = item;
        await RecordChangeAsync(connection: connection, transaction: transaction, projectId: project.Id,
            kind: delete ? "Track deleted" : "Track saved",
            details: JsonSerializer.Serialize(value: new { Previous = previous, Submitted = track }));
        await transaction.CommitAsync();
    }
    #endregion

    #region Atomic Survey Import
    public async Task ImportAsync(ScanProjectState expected, ScanSurveySnapshot snapshot, IProgress<long> progress)
    {
        if (snapshot.InvalidCount != 0 || snapshot.ValidCount == 0)
            throw new InvalidOperationException(message: "The survey must pass validation before import.");
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            UPDATE dbo.ScanProject SET CurrentSurveyId=NULL WHERE ScanProjectId=@project;
            DELETE point FROM dbo.ScanComputedPoint point
                JOIN dbo.ScanComputedRun run ON run.RunId=point.RunId WHERE run.ScanProjectId=@project;
            DELETE FROM dbo.ScanComputedRun WHERE ScanProjectId=@project;
            DELETE point FROM dbo.ScanReferenceSurveyPoint point
                JOIN dbo.ScanReferenceSurvey survey ON survey.SurveyId=point.SurveyId
                WHERE survey.ScanProjectId=@project;
            DELETE FROM dbo.ScanReferenceSurvey WHERE ScanProjectId=@project;
            """))
        {
            Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
            await command.ExecuteNonQueryAsync();
        }
        long surveyId;
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            INSERT dbo.ScanReferenceSurvey(ScanProjectId,SourceFileName,Sha256,IncludesHeader,PointCount)
                OUTPUT INSERTED.SurveyId VALUES(@project,@file,@hash,@header,@count);
            """))
        {
            Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
            Add(command: command, name: "@file", type: SqlDbType.NVarChar, value: snapshot.SourceName, size: 260);
            Add(command: command, name: "@hash", type: SqlDbType.Char, value: snapshot.Sha256, size: 64);
            Add(command: command, name: "@header", type: SqlDbType.Bit, value: snapshot.IncludesHeader);
            Add(command: command, name: "@count", type: SqlDbType.BigInt, value: snapshot.ValidCount);
            surveyId = (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException(message: "Survey creation returned no identity."));
        }
        using DataTable batch = new();
        batch.Columns.Add(columnName: "SurveyId", type: typeof(long));
        batch.Columns.Add(columnName: "SourceRecord", type: typeof(long));
        batch.Columns.Add(columnName: "PointName", type: typeof(string));
        batch.Columns.Add(columnName: "Easting", type: typeof(decimal));
        batch.Columns.Add(columnName: "Northing", type: typeof(decimal));
        batch.Columns.Add(columnName: "TopOfRailHeight", type: typeof(decimal));
        using SqlBulkCopy bulk = new(connection: connection, copyOptions: SqlBulkCopyOptions.CheckConstraints, externalTransaction: transaction)
        { DestinationTableName = "dbo.ScanReferenceSurveyPoint", BatchSize = ScanSurveySnapshot.BulkBatchSize, BulkCopyTimeout = SqlTimeoutSeconds };
        foreach (DataColumn column in batch.Columns)
            bulk.ColumnMappings.Add(sourceColumn: column.ColumnName, destinationColumn: column.ColumnName);
        long count = 0;
        foreach (ScanSurveyPoint point in snapshot.ReadPoints())
        {
            batch.Rows.Add(values: new object[] { surveyId, point.Record, point.PointName, point.Easting, point.Northing, point.Height });
            count++;
            if (batch.Rows.Count == ScanSurveySnapshot.BulkBatchSize)
            {
                await bulk.WriteToServerAsync(table: batch);
                batch.Clear();
                progress.Report(value: count);
            }
        }
        if (batch.Rows.Count > 0) await bulk.WriteToServerAsync(table: batch);
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql:
            "UPDATE dbo.ScanProject SET CurrentSurveyId=@survey WHERE ScanProjectId=@project;"))
        {
            Add(command: command, name: "@survey", type: SqlDbType.BigInt, value: surveyId);
            Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
            await command.ExecuteNonQueryAsync();
        }
        await RecordChangeAsync(connection: connection, transaction: transaction, projectId: project.Id, kind: "Survey imported",
            details: JsonSerializer.Serialize(value: new { SurveyId = surveyId, snapshot.SourceName, snapshot.Sha256, snapshot.IncludesHeader, snapshot.ValidCount }));
        await transaction.CommitAsync();
        progress.Report(value: count);
    }
    #endregion
}
#endregion
