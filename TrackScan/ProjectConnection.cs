#region Project Connection
using System.Data;
using Microsoft.Data.SqlClient;

namespace TrackScan;

internal sealed record ProjectSelection(int Id, string Name, string TimeZoneId);

internal static class ProjectConnection
{
    #region Connection And Project Queries
    public static string Normalise(string connectionString, string database)
    {
        SqlConnectionStringBuilder builder = new(connectionString: connectionString)
        {
            InitialCatalog = database, ConnectTimeout = 15, Pooling = false
        };
        if (string.IsNullOrWhiteSpace(value: builder.DataSource))
            throw new InvalidOperationException(message: "Enter a SQL Server connection string first.");
        return builder.ConnectionString;
    }

    public static async Task TestAsync(string connectionString)
    {
        await using SqlConnection connection = new(connectionString: Normalise(connectionString: connectionString, database: "master"));
        await connection.OpenAsync();
    }

    public static async Task<List<ProjectSelection>> LoadAsync(string connectionString)
    {
        await using SqlConnection connection = new(connectionString: Normalise(connectionString: connectionString, database: "DBTrackGeometry"));
        await connection.OpenAsync();
        using SqlCommand command = new(cmdText: "SELECT Project_ID, ProjectName, TimeZoneId FROM dbo.Project WHERE IsDeleted=0 ORDER BY ProjectName, Project_ID;", connection: connection) { CommandTimeout = 30 };
        using SqlDataReader reader = await command.ExecuteReaderAsync();
        List<ProjectSelection> projects = new();
        while (await reader.ReadAsync())
            projects.Add(item: new(Id: reader.GetInt32(i: 0), Name: reader.GetString(i: 1), TimeZoneId: reader.IsDBNull(i: 2) ? string.Empty : reader.GetString(i: 2)));
        return projects;
    }

    // Use the DLR Report lock resource so an active project cannot be deleted there.
    public static async Task<SqlConnection> SelectAsync(string connectionString, ProjectSelection project)
    {
        SqlConnection connection = new(connectionString: Normalise(connectionString: connectionString, database: "DBTrackGeometry"));
        try
        {
            await connection.OpenAsync();
            using SqlCommand command = new(cmdText: """
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource=@resource, @LockMode=N'Shared',
                    @LockOwner=N'Session', @LockTimeout=0;
                IF @result<0 THROW 51063, 'Project is being changed. Refresh and try again.', 1;
                SELECT COUNT(*) FROM dbo.Project WHERE Project_ID=@id AND IsDeleted=0
                    AND ProjectName=@name AND ISNULL(TimeZoneId,N'')=@zone;
                """, connection: connection) { CommandTimeout = 30 };
            command.Parameters.Add(parameterName: "@resource", sqlDbType: SqlDbType.NVarChar, size: 255).Value = $"GNA_DLRreport:Project:{project.Id}";
            command.Parameters.Add(parameterName: "@id", sqlDbType: SqlDbType.Int).Value = project.Id;
            command.Parameters.Add(parameterName: "@name", sqlDbType: SqlDbType.NVarChar, size: 200).Value = project.Name;
            command.Parameters.Add(parameterName: "@zone", sqlDbType: SqlDbType.NVarChar, size: 200).Value = project.TimeZoneId;
            if ((int)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException(message: "Cannot validate project.")) != 1)
                throw new InvalidOperationException(message: "Project details changed. Refresh the project list and select again.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
    #endregion
}
#endregion
