using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
#endregion
namespace TrackScan;

internal sealed record ScanPolygonTrack(int TrackId, string TrackName);

internal sealed partial class ScanRepository
{
    #region Polygon Input And Settings
    public async Task<IReadOnlyList<ScanPolygonTrack>> LoadPolygonTracksAsync(ScanProjectState expected)
    {
        await using SqlConnection connection = Connection(database: DatabaseName);
        await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            SELECT t.TrackId,t.TrackName FROM dbo.ScanTrack t
            JOIN dbo.ScanComputedRun r ON r.TrackId=t.TrackId AND r.ScanProjectId=t.ScanProjectId
            WHERE t.ScanProjectId=@project AND t.IsDeleted=0 AND r.IsCurrent=1
            AND (SELECT COUNT(*) FROM dbo.ScanComputedPoint p WHERE p.RunId=r.RunId AND p.Rail='R')>=2
            AND (SELECT COUNT(*) FROM dbo.ScanComputedPoint p WHERE p.RunId=r.RunId AND p.Rail='L')>=2
            ORDER BY t.TrackSlot;
            """);
        Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
        List<ScanPolygonTrack> tracks = new();
        using (SqlDataReader reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tracks.Add(item: new(TrackId: reader.GetInt32(i: 0), TrackName: reader.GetString(i: 1)));
        await transaction.CommitAsync();
        return tracks.AsReadOnly();
    }

    public async Task<IReadOnlyList<ScanPolygonRail>> LoadPolygonRailsAsync(ScanProjectState expected, int? trackId = null)
    {
        await using SqlConnection connection = Connection(database: DatabaseName); await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        using SqlCommand command = Command(connection: connection, transaction: transaction, sql: """
            SELECT r.RunId,t.TrackId,t.TrackName,p.Rail,p.Sequence,p.PointName,p.Easting,p.Northing,p.TopOfRailHeight,
                p.DistanceAlongRail,p.SurveyPointCount,p.HeightInterpolated
            FROM dbo.ScanComputedRun r JOIN dbo.ScanTrack t ON t.TrackId=r.TrackId AND t.ScanProjectId=r.ScanProjectId
            JOIN dbo.ScanComputedPoint p ON p.RunId=r.RunId
            WHERE r.ScanProjectId=@project AND r.IsCurrent=1 AND t.IsDeleted=0 AND (@track IS NULL OR t.TrackId=@track)
            ORDER BY t.TrackSlot,p.Rail DESC,p.Sequence;
            """);
        Add(command: command, name: "@project", type: SqlDbType.Int, value: project.Id);
        Add(command: command, name: "@track", type: SqlDbType.Int, value: (object?)trackId ?? DBNull.Value);
        List<ScanPolygonRail> rails = new(); List<ScanComputedPoint>? points = null; long previousRun = -1; string previousRail = "";
        using (SqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                long run = reader.GetInt64(i: 0); string rail = reader.GetString(i: 3);
                if (run != previousRun || rail != previousRail)
                {
                    points = new(); rails.Add(item: new(RunId: run, TrackId: reader.GetInt32(i: 1), TrackName: reader.GetString(i: 2), Rail: rail, Points: points.AsReadOnly()));
                    previousRun = run; previousRail = rail;
                }
                points!.Add(item: new(PointName: reader.GetString(i: 5), Rail: rail, Sequence: reader.GetInt32(i: 4),
                    Easting: reader.GetDecimal(i: 6), Northing: reader.GetDecimal(i: 7), Height: reader.GetDecimal(i: 8),
                    DistanceAlongRail: reader.GetDecimal(i: 9), SurveyPointCount: reader.GetInt32(i: 10), HeightInterpolated: reader.GetBoolean(i: 11)));
            }
        }
        if (trackId.HasValue && (rails.Count != 2 || rails.Any(predicate: rail => rail.Points.Count < 2) ||
            !rails.Any(predicate: rail => rail.Rail == "R") || !rails.Any(predicate: rail => rail.Rail == "L")))
            throw new InvalidOperationException(message: "The selected track has no complete current railhead points. Refresh Scanning and compute and save its Railhead Points.");
        await transaction.CommitAsync(); return rails.AsReadOnly();
    }

    public async Task<ScanPolygonOptions> LoadPolygonOptionsAsync()
    {
        await using SqlConnection connection = Connection(database: "DBTrackGeometry"); await connection.OpenAsync();
        using SqlCommand command = Command(connection: connection, transaction: null, sql: """
            IF OBJECT_ID(N'dbo.ScanPolygonBatch',N'U') IS NOT NULL
                SELECT TOP(1) CorridorWidth,CollinearityLimit,RailheadWidth,RailheadLength,HeightFilterMillimetres
                FROM dbo.ScanPolygonBatch WHERE GeometryProjectId=@project ORDER BY BatchId DESC;
            """);
        Add(command: command, name: "@project", type: SqlDbType.Int, value: _geometryProjectId);
        using SqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return ScanPolygonOptions.Default;
        return new(CorridorWidth: reader.GetDecimal(i: 0), CollinearityLimit: reader.GetDecimal(i: 1),
            RailheadWidth: reader.GetDecimal(i: 2), RailheadLength: reader.GetDecimal(i: 3), HeightFilterMillimetres: reader.GetInt32(i: 4));
    }
    #endregion

    #region Validate And Save Polygon Batches In DBTrackGeometry
    // Current geometry is read through ScanCurrentPolygonDefinition. A legacy batch can
    // contain several tracks, only some of which remain current after a per-track save.
    // Run identity, rather than the project-wide preparation revision, controls validity.
    private const string PolygonSchema = """
        USE DBTrackGeometry;
        IF OBJECT_ID(N'dbo.ScanPolygonBatch',N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.ScanPolygonBatch(
                BatchId bigint IDENTITY PRIMARY KEY, GeometryProjectId int NOT NULL,
                ScanDatabaseIdentity uniqueidentifier NOT NULL, ScanProjectId int NOT NULL, PreparationRevision bigint NOT NULL,
                CreatedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                CorridorWidth decimal(24,4) NOT NULL, CollinearityLimit decimal(24,4) NOT NULL,
                RailheadWidth decimal(24,4) NOT NULL, RailheadLength decimal(24,4) NOT NULL,
                HeightFilterMillimetres int NOT NULL);
            CREATE INDEX IX_ScanPolygonBatchProject ON dbo.ScanPolygonBatch(GeometryProjectId,BatchId);
            CREATE TABLE dbo.ScanPolygonDefinition(
                PolygonId bigint IDENTITY PRIMARY KEY, BatchId bigint NOT NULL REFERENCES dbo.ScanPolygonBatch(BatchId),
                Identifier nvarchar(300) NOT NULL, Kind nvarchar(20) NOT NULL, SourceRunId bigint NOT NULL,
                TrackId int NOT NULL, Rail char(1) NOT NULL, Shape geometry NOT NULL,
                UNIQUE(BatchId,Identifier));
            CREATE TABLE dbo.ScanPolygonVertex(
                PolygonId bigint NOT NULL REFERENCES dbo.ScanPolygonDefinition(PolygonId), Sequence int NOT NULL,
                Identifier nvarchar(320) NOT NULL, RailheadPoint nvarchar(220) NOT NULL,
                Easting decimal(24,4) NOT NULL, Northing decimal(24,4) NOT NULL, Height decimal(24,4) NOT NULL,
                PRIMARY KEY(PolygonId,Sequence));
        END;
        EXEC(N'CREATE OR ALTER VIEW dbo.ScanCurrentPolygonDefinition AS
            SELECT p.* FROM dbo.ScanPolygonDefinition p
            JOIN dbo.ScanPolygonBatch b ON b.BatchId=p.BatchId
            JOIN DBTrackScan.dbo.ScanProject s ON s.ScanProjectId=b.ScanProjectId AND s.GeometryProjectId=b.GeometryProjectId
            JOIN DBTrackScan.dbo.ScanComputedRun r ON r.RunId=p.SourceRunId AND r.ScanProjectId=b.ScanProjectId AND r.TrackId=p.TrackId AND r.IsCurrent=1
            JOIN DBTrackScan.dbo.ScanTrack t ON t.TrackId=r.TrackId AND t.ScanProjectId=r.ScanProjectId AND t.IsDeleted=0
            WHERE b.ScanDatabaseIdentity=(SELECT service_broker_guid FROM sys.databases WHERE name=N''DBTrackScan'')
            AND b.BatchId=(SELECT MAX(newer.BatchId) FROM dbo.ScanPolygonBatch newer
                JOIN dbo.ScanPolygonDefinition np ON np.BatchId=newer.BatchId
                WHERE newer.GeometryProjectId=b.GeometryProjectId AND newer.ScanDatabaseIdentity=b.ScanDatabaseIdentity
                AND newer.ScanProjectId=b.ScanProjectId AND np.TrackId=p.TrackId);');
        EXEC(N'CREATE OR ALTER VIEW dbo.ScanCurrentPolygonBatch AS
            SELECT b.* FROM dbo.ScanPolygonBatch b
            WHERE EXISTS(SELECT 1 FROM dbo.ScanCurrentPolygonDefinition p WHERE p.BatchId=b.BatchId);');
        """;

    private static string PolygonWkt(ScanPolygon polygon, decimal originEasting = 0m, decimal originNorthing = 0m)
    {
        List<string> coordinates = new();
        foreach (ScanPolygonVertex vertex in polygon.Vertices)
            coordinates.Add(item: (vertex.Easting - originEasting).ToString(format: "F4", provider: CultureInfo.InvariantCulture) + " " + (vertex.Northing - originNorthing).ToString(format: "F4", provider: CultureInfo.InvariantCulture));
        coordinates.Add(item: coordinates[0]);
        return "POLYGON((" + string.Join(separator: ",", values: coordinates) + "))";
    }

    public async Task ValidatePolygonsAsync(ScanPolygonSet set)
    {
        await using SqlConnection connection = Connection(database: "DBTrackGeometry"); await connection.OpenAsync();
        await ValidatePolygonGeometryAsync(connection: connection, transaction: null, set: set);
    }

    private static async Task ValidatePolygonGeometryAsync(SqlConnection connection, SqlTransaction? transaction, ScanPolygonSet set)
    {
        Dictionary<(int Track, string Rail), ScanPolygon> corridors = new();
        foreach (ScanPolygon polygon in set.Polygons)
            if (polygon.Kind == "Corridor") corridors.Add(key: (polygon.TrackId, polygon.Rail), value: polygon);
        foreach (ScanPolygon polygon in set.Polygons)
        {
            // Subtract a shared local origin before converting to SQL geometry. This avoids
            // loss of significance when calculating small areas at large survey coordinates.
            decimal originEasting = polygon.Vertices[0].Easting;
            decimal originNorthing = polygon.Vertices[0].Northing;
            using SqlCommand command = Command(connection: connection, transaction: transaction, sql:
                "DECLARE @g geometry=geometry::STGeomFromText(@wkt,0); SELECT CASE WHEN @g.STIsValid()=1 AND @g.STArea()>0 THEN 1 ELSE 0 END;");
            Add(command: command, name: "@wkt", type: SqlDbType.NVarChar, value: PolygonWkt(polygon: polygon, originEasting: originEasting, originNorthing: originNorthing), size: -1);
            if ((int)(await command.ExecuteScalarAsync() ?? 0) != 1)
                throw new InvalidOperationException(message: $"Polygon '{polygon.Identifier}' is invalid or self-intersecting. Review the rail alignment or collinearity limit. No polygons saved.");
            if (polygon.Kind == "Railhead" && !set.RailheadOnly)
            {
                if (!corridors.TryGetValue(key: (polygon.TrackId, polygon.Rail), value: out ScanPolygon? corridor))
                    throw new InvalidOperationException(message: "A railhead polygon has no corresponding corridor.");
                // Each independently rounded vertex may move by sqrt(2)*0.00005 m.
                // The combined boundary uncertainty is below 0.00015 m (0.15 mm).
                // This allowance is used only for validation; stored polygons are unchanged.
                const double coordinateRoundingTolerance = 0.00015d;
                using SqlCommand coverage = Command(connection: connection, transaction: transaction, sql:
                    "SELECT geometry::STGeomFromText(@head,0).STDifference(geometry::STGeomFromText(@corridor,0).STBuffer(@roundingTolerance)).STIsEmpty();");
                Add(command: coverage, name: "@head", type: SqlDbType.NVarChar, value: PolygonWkt(polygon: polygon, originEasting: originEasting, originNorthing: originNorthing), size: -1);
                Add(command: coverage, name: "@corridor", type: SqlDbType.NVarChar, value: PolygonWkt(polygon: corridor, originEasting: originEasting, originNorthing: originNorthing), size: -1);
                Add(command: coverage, name: "@roundingTolerance", type: SqlDbType.Float, value: coordinateRoundingTolerance);
                if (!Convert.ToBoolean(value: await coverage.ExecuteScalarAsync(), provider: CultureInfo.InvariantCulture))
                    throw new InvalidOperationException(message: $"Railhead polygon '{polygon.Identifier}' extends outside its corridor. Select a wider corridor or smaller collinearity limit; no polygons saved.");
            }
        }
    }

    public async Task<long> SavePolygonsAsync(ScanProjectState expected, ScanPolygonSet set)
    {
        set.Options.Validate();
        if (set.Polygons.Count == 0) throw new InvalidOperationException(message: "No polygons to save.");
        if (set.RailheadOnly && set.Polygons.Any(predicate: polygon => polygon.Kind != "Railhead"))
            throw new InvalidOperationException(message: "Railhead-only mode cannot save rail corridors.");
        string requiredKind = set.RailheadOnly ? "Railhead" : "Corridor";
        int trackId = set.Polygons[0].TrackId;
        if (set.Polygons.Any(predicate: polygon => polygon.TrackId != trackId) ||
            !set.Polygons.Any(predicate: polygon => polygon.Rail == "R" && polygon.Kind == requiredKind) ||
            !set.Polygons.Any(predicate: polygon => polygon.Rail == "L" && polygon.Kind == requiredKind))
            throw new InvalidOperationException(message: "Save polygons for one selected track with both rails only.");
        await using SqlConnection connection = Connection(database: DatabaseName); await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        var project = await ResolveProjectAsync(connection: connection, transaction: transaction);
        VerifyRevision(expected: expected, projectId: project.Id, revision: project.Revision, databaseIdentity: project.DatabaseIdentity);
        await LockAsync(connection: connection, transaction: transaction, resource: "GNA:ScanPolygonSchema");
        // The scan project lock prevents replacement of the source survey or railhead runs during this save.
        foreach (long run in set.Polygons.Select(selector: p => p.RunId).Distinct())
        {
            using SqlCommand check = Command(connection: connection, transaction: transaction,
                sql: "SELECT COUNT(*) FROM dbo.ScanComputedRun WHERE RunId=@run AND ScanProjectId=@project AND TrackId=@track AND IsCurrent=1;");
            Add(command: check, name: "@track", type: SqlDbType.Int, value: trackId);
            Add(command: check, name: "@run", type: SqlDbType.BigInt, value: run);
            Add(command: check, name: "@project", type: SqlDbType.Int, value: project.Id);
            if ((int)(await check.ExecuteScalarAsync() ?? 0) != 1) throw new InvalidOperationException(message: "Railhead points changed. Refresh and create polygons again.");
        }
        using (SqlCommand schema = Command(connection: connection, transaction: transaction, sql: PolygonSchema)) await schema.ExecuteNonQueryAsync();
        await ValidatePolygonGeometryAsync(connection: connection, transaction: transaction, set: set);
        using SqlCommand batch = Command(connection: connection, transaction: transaction, sql: """
            INSERT DBTrackGeometry.dbo.ScanPolygonBatch(GeometryProjectId,ScanDatabaseIdentity,ScanProjectId,PreparationRevision,
                CorridorWidth,CollinearityLimit,RailheadWidth,RailheadLength,HeightFilterMillimetres)
            OUTPUT INSERTED.BatchId VALUES(@geometry,@identity,@project,@revision,@width,@limit,@headWidth,@length,@height);
            """);
        Add(command: batch, name: "@geometry", type: SqlDbType.Int, value: _geometryProjectId);
        Add(command: batch, name: "@identity", type: SqlDbType.UniqueIdentifier, value: project.DatabaseIdentity);
        Add(command: batch, name: "@project", type: SqlDbType.Int, value: project.Id);
        Add(command: batch, name: "@revision", type: SqlDbType.BigInt, value: project.Revision);
        Add(command: batch, name: "@width", type: SqlDbType.Decimal, value: set.Options.CorridorWidth);
        Add(command: batch, name: "@limit", type: SqlDbType.Decimal, value: set.Options.CollinearityLimit);
        Add(command: batch, name: "@headWidth", type: SqlDbType.Decimal, value: set.Options.RailheadWidth);
        Add(command: batch, name: "@length", type: SqlDbType.Decimal, value: set.Options.RailheadLength);
        Add(command: batch, name: "@height", type: SqlDbType.Int, value: set.Options.HeightFilterMillimetres);
        long batchId = (long)(await batch.ExecuteScalarAsync() ?? throw new InvalidOperationException(message: "No polygon batch identity returned."));
        using DataTable vertices = new();
        foreach (var column in new[] { ("PolygonId", typeof(long)), ("Sequence", typeof(int)), ("Identifier", typeof(string)), ("RailheadPoint", typeof(string)), ("Easting", typeof(decimal)), ("Northing", typeof(decimal)), ("Height", typeof(decimal)) })
            vertices.Columns.Add(columnName: column.Item1, type: column.Item2);
        foreach (ScanPolygon polygon in set.Polygons)
        {
            using SqlCommand definition = Command(connection: connection, transaction: transaction, sql: """
                INSERT DBTrackGeometry.dbo.ScanPolygonDefinition(BatchId,Identifier,Kind,SourceRunId,TrackId,Rail,Shape)
                OUTPUT INSERTED.PolygonId VALUES(@batch,@identifier,@kind,@run,@track,@rail,geometry::STGeomFromText(@wkt,0));
                """);
            Add(command: definition, name: "@batch", type: SqlDbType.BigInt, value: batchId);
            Add(command: definition, name: "@identifier", type: SqlDbType.NVarChar, value: polygon.Identifier, size: 300);
            Add(command: definition, name: "@kind", type: SqlDbType.NVarChar, value: polygon.Kind, size: 20);
            Add(command: definition, name: "@run", type: SqlDbType.BigInt, value: polygon.RunId);
            Add(command: definition, name: "@track", type: SqlDbType.Int, value: polygon.TrackId);
            Add(command: definition, name: "@rail", type: SqlDbType.Char, value: polygon.Rail, size: 1);
            Add(command: definition, name: "@wkt", type: SqlDbType.NVarChar, value: PolygonWkt(polygon: polygon), size: -1);
            long polygonId = (long)(await definition.ExecuteScalarAsync() ?? throw new InvalidOperationException(message: "No polygon identity returned."));
            foreach (ScanPolygonVertex v in polygon.Vertices) vertices.Rows.Add(values: new object[] { polygonId, v.Sequence, v.Identifier, v.RailheadPoint, v.Easting, v.Northing, v.Height });
        }
        using SqlBulkCopy bulk = new(connection: connection, copyOptions: SqlBulkCopyOptions.CheckConstraints, externalTransaction: transaction)
        { DestinationTableName = "DBTrackGeometry.dbo.ScanPolygonVertex", BulkCopyTimeout = SqlTimeoutSeconds, BatchSize = 5000 };
        foreach (DataColumn column in vertices.Columns) bulk.ColumnMappings.Add(sourceColumn: column.ColumnName, destinationColumn: column.ColumnName);
        await bulk.WriteToServerAsync(table: vertices); await transaction.CommitAsync(); return batchId;
    }
    #endregion
}
