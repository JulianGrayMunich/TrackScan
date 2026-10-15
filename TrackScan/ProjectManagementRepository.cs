#region Shared Project Management
using System.Data;
using Microsoft.Data.SqlClient;

namespace TrackScan;

internal sealed record ManagedProject(int Id, string Name, DateTime StartDate, string TimeZoneId, string OutputPath, bool IsDeleted)
{
    public bool IsActive { get; init; }
}

internal sealed class ProjectManagementRepository
{
    #region Connection And Validation
    private readonly string _connectionString;
    public ProjectManagementRepository(string connectionString)
    {
        _connectionString = ProjectConnection.Normalise(connectionString: connectionString ?? throw new ArgumentNullException(paramName: nameof(connectionString)), database: "DBTrackGeometry");
    }
    private SqlConnection Connection() => new(connectionString: _connectionString);
    private static SqlCommand Command(SqlConnection connection, string sql, SqlTransaction? transaction = null)
        => new(cmdText: sql, connection: connection, transaction: transaction) { CommandTimeout = 30 };
    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        SqlParameter parameter = new(parameterName: name, dbType: type) { Value = value };
        if (size > 0) parameter.Size = size;
        command.Parameters.Add(value: parameter);
    }
    private static string ValidateName(string name)
    {
        string result = (name ?? throw new ArgumentNullException(paramName: nameof(name))).Trim();
        if (result.Length == 0 || result.Length > 200) throw new ArgumentException(message: "Project name must contain 1 to 200 characters.", paramName: nameof(name));
        return result;
    }
    private static void ValidateSettings(string timeZoneId, string outputPath, bool requireOutput)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(id: timeZoneId);
        if (timeZoneId.Length > 200) throw new ArgumentException(message: "Time zone exceeds 200 characters.");
        if (outputPath.Length > 1000 || (requireOutput && string.IsNullOrWhiteSpace(value: outputPath)))
            throw new ArgumentException(message: "Select an output path of no more than 1000 characters.");
    }
    private static void ExpectOne(int rows)
    {
        if (rows != 1) throw new InvalidOperationException(message: "The project changed in another application. Refresh and try again.");
    }
    private static ManagedProject Read(SqlDataReader reader) => new(Id: reader.GetInt32(i: 0), Name: reader.GetString(i: 1),
        StartDate: reader.IsDBNull(i: 2) ? DateTime.Today : reader.GetDateTime(i: 2),
        TimeZoneId: reader.IsDBNull(i: 3) ? string.Empty : reader.GetString(i: 3),
        OutputPath: reader.IsDBNull(i: 4) ? string.Empty : reader.GetString(i: 4), IsDeleted: reader.GetBoolean(i: 5));
    private const string Columns = "Project_ID,ProjectName,ProjectStartDate,TimeZoneId,DefaultReportOutputPath,IsDeleted";
    #endregion

    #region Read Projects
    public async Task<List<ManagedProject>> LoadAsync()
    {
        await using SqlConnection connection = Connection(); await connection.OpenAsync();
        using SqlCommand command = Command(connection: connection, sql: $"SELECT {Columns} FROM dbo.Project ORDER BY ProjectName,Project_ID;");
        using SqlDataReader reader = await command.ExecuteReaderAsync();
        List<ManagedProject> projects = new();
        while (await reader.ReadAsync()) projects.Add(item: Read(reader: reader));
        return projects;
    }
    public async Task<ManagedProject?> FindAsync(string name)
    {
        await using SqlConnection connection = Connection(); await connection.OpenAsync();
        using SqlCommand command = Command(connection: connection, sql: $"SELECT {Columns} FROM dbo.Project WHERE ProjectName=@name;");
        Add(command: command, name: "@name", type: SqlDbType.NVarChar, value: ValidateName(name: name), size: 200);
        using SqlDataReader reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader: reader) : null;
    }
    #endregion

    #region Add Overwrite And Restore
    // expected is the exact existing record for which the operator confirmed overwrite/restore.
    public async Task SaveProjectAsync(string name, DateTime startDate, string timeZoneId, string outputPath, ManagedProject? expected)
    {
        name = ValidateName(name: name); outputPath = outputPath.Trim();
        ValidateSettings(timeZoneId: timeZoneId, outputPath: outputPath, requireOutput: true);
        await using SqlConnection connection = Connection(); await connection.OpenAsync();
        using SqlTransaction transaction = connection.BeginTransaction(iso: IsolationLevel.Serializable);
        using SqlCommand check = Command(connection: connection, transaction: transaction,
            sql: "SELECT Project_ID,IsDeleted FROM dbo.Project WITH(UPDLOCK,HOLDLOCK) WHERE ProjectName=@name;");
        Add(command: check, name: "@name", type: SqlDbType.NVarChar, value: name, size: 200);
        int? id = null; bool deleted = false;
        using (SqlDataReader reader = await check.ExecuteReaderAsync())
            if (await reader.ReadAsync()) { id = reader.GetInt32(i: 0); deleted = reader.GetBoolean(i: 1); }
        if (id != expected?.Id || (expected is not null && deleted != expected.IsDeleted))
            throw new InvalidOperationException(message: "The project changed before confirmation could be applied. Refresh and try again.");
        string sql = id.HasValue
            ? "UPDATE dbo.Project SET ProjectStartDate=@date,TimeZoneId=@zone,DefaultReportOutputPath=@path,IsDeleted=0 WHERE Project_ID=@id;"
            : "INSERT dbo.Project(ProjectName,ProjectStartDate,TimeZoneId,DefaultReportOutputPath,IsDeleted) VALUES(@name,@date,@zone,@path,0);";
        using SqlCommand save = Command(connection: connection, sql: sql, transaction: transaction);
        Add(command: save, name: "@id", type: SqlDbType.Int, value: (object?)id ?? DBNull.Value);
        Add(command: save, name: "@name", type: SqlDbType.NVarChar, value: name, size: 200);
        Add(command: save, name: "@date", type: SqlDbType.Date, value: startDate.Date);
        Add(command: save, name: "@zone", type: SqlDbType.NVarChar, value: timeZoneId, size: 200);
        Add(command: save, name: "@path", type: SqlDbType.NVarChar, value: outputPath, size: 1000);
        ExpectOne(rows: await save.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }
    #endregion

    #region Update Existing Project
    public async Task RenameAsync(ManagedProject expected, string name)
    {
        name = ValidateName(name: name);
        await UpdateAsync(expected: expected, assignments: "ProjectName=@value", parameter: new(parameterName: "@value", dbType: SqlDbType.NVarChar, size: 200) { Value = name });
    }
    public async Task SetStartDateAsync(ManagedProject expected, DateTime startDate)
        => await UpdateAsync(expected: expected, assignments: "ProjectStartDate=@value", parameter: new(parameterName: "@value", dbType: SqlDbType.Date) { Value = startDate.Date });

    public async Task SaveSettingsAsync(ManagedProject expected, string timeZoneId, string outputPath)
    {
        outputPath = outputPath.Trim(); ValidateSettings(timeZoneId: timeZoneId, outputPath: outputPath, requireOutput: false);
        await using SqlConnection connection = Connection(); await connection.OpenAsync();
        using SqlCommand command = Command(connection: connection, sql: "UPDATE dbo.Project SET TimeZoneId=@zone,DefaultReportOutputPath=NULLIF(@path,N'') WHERE Project_ID=@id AND ProjectName=@name AND IsDeleted=@deleted;");
        AddExpected(command: command, expected: expected);
        Add(command: command, name: "@zone", type: SqlDbType.NVarChar, value: timeZoneId, size: 200);
        Add(command: command, name: "@path", type: SqlDbType.NVarChar, value: outputPath, size: 1000);
        ExpectOne(rows: await command.ExecuteNonQueryAsync());
    }
    private async Task UpdateAsync(ManagedProject expected, string assignments, SqlParameter parameter)
    {
        await using SqlConnection connection = Connection(); await connection.OpenAsync();
        using SqlCommand command = Command(connection: connection, sql: $"UPDATE dbo.Project SET {assignments} WHERE Project_ID=@id AND ProjectName=@name AND IsDeleted=@deleted;");
        AddExpected(command: command, expected: expected); command.Parameters.Add(value: parameter);
        ExpectOne(rows: await command.ExecuteNonQueryAsync());
    }
    private static void AddExpected(SqlCommand command, ManagedProject expected)
    {
        Add(command: command, name: "@id", type: SqlDbType.Int, value: expected.Id);
        Add(command: command, name: "@name", type: SqlDbType.NVarChar, value: expected.Name, size: 200);
        Add(command: command, name: "@deleted", type: SqlDbType.Bit, value: expected.IsDeleted);
    }
    #endregion

    #region Soft Delete And Restore
    public async Task SetDeletedAsync(ManagedProject expected, bool deleted, int? activeProjectId)
    {
        if (deleted && expected.Id == activeProjectId) throw new InvalidOperationException(message: "The active project cannot be deleted.");
        await using SqlConnection connection = Connection(); await connection.OpenAsync();
        if (deleted)
        {
            using SqlCommand takeLock = Command(connection: connection, sql: """
                DECLARE @r int;
                EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0;
                IF @r<0 THROW 51063, 'This project is active in another application and cannot be deleted.', 1;
                """);
            Add(command: takeLock, name: "@resource", type: SqlDbType.NVarChar, value: $"GNA_DLRreport:Project:{expected.Id}", size: 255);
            await takeLock.ExecuteNonQueryAsync();
        }
        using SqlCommand command = Command(connection: connection, sql: "UPDATE dbo.Project SET IsDeleted=@newState WHERE Project_ID=@id AND ProjectName=@name AND IsDeleted=@deleted;");
        AddExpected(command: command, expected: expected);
        Add(command: command, name: "@newState", type: SqlDbType.Bit, value: deleted);
        ExpectOne(rows: await command.ExecuteNonQueryAsync());
        // Connection pooling is disabled: disposal releases the session application lock.
    }
    #endregion
}
#endregion
