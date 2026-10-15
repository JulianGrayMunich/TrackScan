using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
#endregion

namespace TrackScan;

internal sealed partial class ScanRepository
{
    #region Invalidate Derived Reference Points
    private static async Task InvalidateComputedRunsAsync(SqlConnection connection, SqlTransaction transaction, int projectId, int trackId)
    {
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql:
            "UPDATE dbo.ScanComputedRun SET IsCurrent=0 WHERE ScanProjectId=@project AND (@track=0 OR TrackId=@track) AND IsCurrent=1;");
        Add(command: command, name: "@project", type: SqlDbType.Int, value: projectId);
        Add(command: command, name: "@track", type: SqlDbType.Int, value: trackId);
        await command.ExecuteNonQueryAsync();
    }
    #endregion

    #region Read A Consistent Computation Input
    public async Task<ScanComputationInput> LoadComputationAsync(ScanProjectState expected, int trackId)
    {
        ScanTrack? track = null;
        foreach (ScanTrack candidate in expected.Tracks)
            if (candidate.TrackId == trackId) { track = candidate; break; }
        ScanTrack selected = track ?? throw new InvalidOperationException(message: "Select a saved track before computing points.");
        var snapshot = await LoadCurrentSurveySnapshotAsync(expected: expected);
        return new ScanComputationInput(SurveyId: snapshot.SurveyId, Track: selected with { PointSpacing = ScanEngine.FixedRailheadSpacingMetres }, Survey: snapshot.Survey);
    }

    public async Task<IReadOnlyList<ScanSurveyPoint>> LoadStartPointSurveyAsync(ScanProjectState expected)
    {
        var snapshot = await LoadCurrentSurveySnapshotAsync(expected: expected);
        return snapshot.Survey;
    }

    private async Task<(long SurveyId, IReadOnlyList<ScanSurveyPoint> Survey)> LoadCurrentSurveySnapshotAsync(ScanProjectState expected)
    {
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        long surveyId;
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql:
            "SELECT CurrentSurveyId FROM dbo.ScanProject WHERE ScanProjectId=@project;"))
        {
            Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
            object? value = await command.ExecuteScalarAsync();
            surveyId = value is long id ? id : throw new InvalidOperationException(message: "Import a Railhead survey before computing track points.");
        }
        List<ScanSurveyPoint> points = new();
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            SELECT SourceRecord,PointName,Easting,Northing,TopOfRailHeight
            FROM dbo.ScanReferenceSurveyPoint WHERE SurveyId=@survey ORDER BY SourceRecord;
            """))
        {
            Add(command: command, name: "@survey", type: SqlDbType.BigInt, value: surveyId);
            using SqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                points.Add(item: new ScanSurveyPoint(Record: reader.GetInt64(i: 0), PointName: reader.GetString(i: 1),
                    Easting: reader.GetDecimal(i: 2), Northing: reader.GetDecimal(i: 3), Height: reader.GetDecimal(i: 4)));
        }
        await transaction.CommitAsync();
        return (surveyId, points.AsReadOnly());
    }
    #endregion

    #region Atomically Publish A Completed Reference Run
    public async Task<long> SaveComputationAsync(ScanProjectState expected, ScanComputationInput input, ScanComputationResult result)
    {
        if (input.Track != result.Track || result.Points.Count < 4)
            throw new InvalidOperationException(message: "The computation does not match the selected track.");
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        await InvalidateComputedRunsAsync(connection: connection, transaction: transaction, projectId: project.Id, trackId: input.Track.TrackId);
        long runId;
        using (SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            IF NOT EXISTS(SELECT 1 FROM dbo.ScanProject WHERE ScanProjectId=@project AND CurrentSurveyId=@survey)
                THROW 51067, 'The current survey has changed. Refresh and compute again.', 1;
            IF NOT EXISTS(SELECT 1 FROM dbo.ScanTrack WHERE TrackId=@track AND ScanProjectId=@project AND IsDeleted=0)
                THROW 51067, 'The selected track is no longer available.', 1;
            INSERT dbo.ScanComputedRun(ScanProjectId,TrackId,SurveyId,SourcePreparationRevision,TrackDefinition,PointCount,IsCurrent)
                OUTPUT INSERTED.RunId VALUES(@project,@track,@survey,@revision,@definition,@count,1);
            """))
        {
            Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
            Add(command: command, name: "@track", type: SqlDbType.Int, value: input.Track.TrackId);
            Add(command: command, name: "@survey", type: SqlDbType.BigInt, value: input.SurveyId);
            Add(command: command, name: "@revision", type: SqlDbType.BigInt, value: project.Revision);
            Add(command: command, name: "@definition", type: SqlDbType.NVarChar, value: JsonSerializer.Serialize(value: input.Track), size: -1);
            Add(command: command, name: "@count", type: SqlDbType.Int, value: result.Points.Count);
            runId = (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException(message: "No computation identity was returned."));
        }
        using DataTable batch = new();
        batch.Columns.Add(columnName: "RunId", type: typeof(long));
        batch.Columns.Add(columnName: "Rail", type: typeof(string));
        batch.Columns.Add(columnName: "Sequence", type: typeof(int));
        batch.Columns.Add(columnName: "PointName", type: typeof(string));
        batch.Columns.Add(columnName: "Easting", type: typeof(decimal));
        batch.Columns.Add(columnName: "Northing", type: typeof(decimal));
        batch.Columns.Add(columnName: "TopOfRailHeight", type: typeof(decimal));
        batch.Columns.Add(columnName: "DistanceAlongRail", type: typeof(decimal));
        batch.Columns.Add(columnName: "SurveyPointCount", type: typeof(int));
        batch.Columns.Add(columnName: "HeightInterpolated", type: typeof(bool));
        using SqlBulkCopy bulk = new(connection: connection, copyOptions: SqlBulkCopyOptions.CheckConstraints, externalTransaction: transaction)
        { DestinationTableName = "dbo.ScanComputedPoint", BatchSize = ScanSurveySnapshot.BulkBatchSize, BulkCopyTimeout = SqlTimeoutSeconds };
        foreach (DataColumn column in batch.Columns)
            bulk.ColumnMappings.Add(sourceColumn: column.ColumnName, destinationColumn: column.ColumnName);
        int nearestSurveyPointCount = 0;
        foreach (ScanComputedPoint point in result.Points)
        {
            if (point.HeightFromNearestSurvey) nearestSurveyPointCount++;
            batch.Rows.Add(values: new object[] { runId, point.Rail, point.Sequence, point.PointName, point.Easting, point.Northing,
                point.Height, point.DistanceAlongRail, point.SurveyPointCount, point.HeightInterpolated });
            if (batch.Rows.Count == ScanSurveySnapshot.BulkBatchSize)
            { await bulk.WriteToServerAsync(table: batch); batch.Clear(); }
        }
        if (batch.Rows.Count > 0) await bulk.WriteToServerAsync(table: batch);
        await RecordChangeAsync(connection: connection, transaction: transaction, projectId: project.Id, kind: "Track points computed",
            details: JsonSerializer.Serialize(value: new { RunId = runId, input.SurveyId, input.Track.TrackId, PointCount = result.Points.Count, NearestSurveyPointCount = nearestSurveyPointCount }));
        await transaction.CommitAsync();
        return runId;
    }
    #endregion
}
