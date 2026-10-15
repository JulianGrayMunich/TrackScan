#region Database And Configuration Interface
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;

namespace TrackScan;

public partial class MainWindow
{
    #region Context And Lifecycle
    private string? _validatedConnection;
    private SqlConnection? _projectLock;
    private ProjectSelection? _activeProject;
    private int? _preferredProjectId;
    private bool _busy;
    private TextBox? _operationComments;
    private string _lastConnectionText = string.Empty;
    private bool _restoringConnectionText;

    private void InitialiseConnectionPage()
    {
        try
        {
            ConnectionPreferences preferences = ConnectionPreferenceStore.Load();
            txtConnection.Text = preferences.ConnectionString;
            _preferredProjectId = preferences.ProjectId;
        }
        catch (Exception)
        {
            txtDatabaseComments.Text = "Saved connection settings could not be read. Enter and test the connection again.";
        }
        Closing += WindowClosing;
        Closed += WindowClosed;
    }

    private async void StartupConnection_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= StartupConnection_Loaded;
        if (string.IsNullOrWhiteSpace(value: txtConnection.Text)) return;
        await RunAsync(comments: txtDatabaseComments, action: async () =>
        {
            await TestConnectionAsync();
            if (_activeProjectId.HasValue)
            {
                try { await RefreshScanAsync(); }
                catch (Exception ex) { txtScanStatus.Text = "Scan preparation could not be loaded: " + DescribeError(exception: ex); }
            }
        });
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_busy || _scanBusy)
        {
            e.Cancel = true;
            (_operationComments ?? txtDatabaseComments).Text = "Please wait for the current database operation to finish before closing.";
        }
    }

    private void WindowClosed(object? sender, EventArgs e) => ClearProject();

    private void ClearProject()
    {
        _projectLock?.Dispose();
        _projectLock = null;
        _activeProject = null;
        InvalidateScanProjectContext();
        if (txtActiveProject is not null) txtActiveProject.Text = "No active project";
    }

    private void ConnectionChanged(object sender, TextChangedEventArgs e)
    {
        if (_restoringConnectionText) return;
        if (!ConfirmDiscardScanEdit())
        {
            _restoringConnectionText = true;
            try { txtConnection.Text = _lastConnectionText; }
            finally { _restoringConnectionText = false; }
            return;
        }
        _lastConnectionText = txtConnection.Text;
        _preferredProjectId = null;
        InvalidateConnection();
    }

    private void InvalidateConnection()
    {
        _validatedConnection = null;
        ClearProject();
        
        if (btnCreateScanDb is not null) btnCreateScanDb.IsEnabled = false;
        if (pnlProjectSelection is not null) pnlProjectSelection.IsEnabled = false;
        if (txtDatabaseComments is not null) txtDatabaseComments.Text = "Test the connection before continuing.";
        if (txtConfigurationComments is not null) txtConfigurationComments.Text = "Test the database connection before selecting a project.";
    }

    private async Task RunAsync(TextBox comments, Func<Task> action)
    {
        if (_busy || _scanBusy) return;
        _busy = true;
        _operationComments = comments;
        tabApplication.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            string message = DescribeError(exception: ex);
            comments.Text = message;
            MessageBox.Show(owner: this, messageBoxText: message, caption: "TrackScan", button: MessageBoxButton.OK, icon: MessageBoxImage.Error);
        }
        finally { _busy = false; _operationComments = null; tabApplication.IsEnabled = true; }
    }

    private void SavePreferences(TextBox comments)
    {
        try { ConnectionPreferenceStore.Save(preferences: new(ConnectionString: _validatedConnection ?? string.Empty, ProjectId: _activeProject?.Id)); }
        catch (Exception) { comments.Text += " Connection settings could not be saved for the next session."; }
    }
    #endregion

    #region Database Connection
    private static string DescribeError(Exception exception)
        => exception is SqlException sql ? $"SQL error {sql.Number}: {sql.Message}" : exception.Message;

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
        => await RunAsync(comments: txtDatabaseComments, action: TestConnectionAsync);

    private async Task TestConnectionAsync()
    {
        InvalidateConnection();
        txtDatabaseComments.Text = "Testing database connection...";
        try
        {
            string connection = ProjectConnection.Normalise(connectionString: txtConnection.Text.Trim(), database: "master");
            await ProjectConnection.TestAsync(connectionString: connection);
            _validatedConnection = connection;
        }
        catch (Exception ex)
        {
            txtDatabaseComments.Text = $"Database connection failed.\n{DescribeError(exception: ex)}";
            txtConfigurationComments.Text = "Database connection failed. See the Database comments for the error message.";
            return;
        }

        btnCreateScanDb.IsEnabled = true;
        pnlProjectSelection.IsEnabled = true;
        txtDatabaseComments.Text = "Database connection successful.";
        SavePreferences(comments: txtDatabaseComments);
        try { await RefreshProjectsAsync(); }
        catch (Exception ex)
        {
            // The server connection succeeded; project loading is a separate result.
            txtConfigurationComments.Text = $"Project list loading failed.\n{DescribeError(exception: ex)}";
            txtDatabaseComments.Text += "\nProject list loading failed. See the Project Selection comments for details.";
        }
    }
    #endregion

    #region Existing Project Selection
    private async Task RefreshProjectsAsync()
    {
        string connection = _validatedConnection ?? throw new InvalidOperationException(message: "Test the connection first.");
        List<ManagedProject> projects = await new ProjectManagementRepository(connectionString: connection).LoadAsync();
        await SynchroniseManagedProjectsAsync(projects: projects);
        if (_activeProject is null && _preferredProjectId.HasValue)
        {
            int savedId = _preferredProjectId.Value;
            _preferredProjectId = null;
            foreach (ManagedProject project in projects)
                if (project.Id == savedId && !project.IsDeleted) { await ActivateManagedProjectAsync(project: project); break; }
        }
        txtConfigurationComments.Text = projects.Count == 0
            ? "No projects exist. Click Manage Projects to add a project."
            : "Click Manage Projects to select or maintain a project.";
    }

    private async void ManageProjects_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _scanBusy) return;
        try
        {
            ProjectManagementWindow manager = new(connectionString: _validatedConnection
                ?? throw new InvalidOperationException(message: "Test the connection first."), activeId: _activeProject?.Id,
                activate: ActivateManagedProjectAsync, synchronise: SynchroniseManagedProjectsAsync) { Owner = this };
            manager.ShowDialog();
            await RunAsync(comments: txtConfigurationComments, action: RefreshProjectsAsync);
        }
        catch (Exception ex) { txtConfigurationComments.Text = DescribeError(exception: ex); }
    }

    private async Task ActivateManagedProjectAsync(ManagedProject project)
    {
        if (!ConfirmDiscardScanEdit()) throw new InvalidOperationException(message: "Project selection cancelled; unsaved track edits retained.");
        ProjectSelection selected = new(Id: project.Id, Name: project.Name, TimeZoneId: project.TimeZoneId);
        SqlConnection newLock = await ProjectConnection.SelectAsync(connectionString: _validatedConnection
            ?? throw new InvalidOperationException(message: "Test the connection first."), project: selected);
        ClearProject();
        _projectLock = newLock; _activeProject = selected; _preferredProjectId = project.Id;
        InvalidateScanProjectContext();
        txtActiveProject.Text = project.Name;
        txtConfigurationComments.Text = $"Active project: {project.Name}. Time zone: {project.TimeZoneId}.";
        SavePreferences(comments: txtConfigurationComments);
    }

    private Task SynchroniseManagedProjectsAsync(IReadOnlyList<ManagedProject> projects)
    {
        if (_activeProject is null) return Task.CompletedTask;
        foreach (ManagedProject project in projects)
        {
            if (project.Id != _activeProject.Id) continue;
            if (project.IsDeleted) break;
            _activeProject = new(Id: project.Id, Name: project.Name, TimeZoneId: project.TimeZoneId);
            txtActiveProject.Text = project.Name;
            InvalidateScanProjectContext();
            SavePreferences(comments: txtConfigurationComments);
            return Task.CompletedTask;
        }
        ClearProject();
        SavePreferences(comments: txtConfigurationComments);
        return Task.CompletedTask;
    }
    #endregion

    #region Database Creation
    private async void CreateScanDb_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardScanEdit()) return;
        await RunAsync(comments: txtDatabaseComments, action: async () =>
        {
            ScanRepository repository = new(baseConnectionString: _validatedConnection
                ?? throw new InvalidOperationException(message: "Test the connection first."), geometryProjectId: 0);
            bool exists = await repository.DatabaseExistsAsync();
            if (exists && !ConfirmDatabaseRecreation(databaseName: ScanRepository.DatabaseName))
            {
                txtDatabaseComments.Text = "Database 'DBTrackScan' was not changed.";
                return;
            }
            txtDatabaseComments.Text = exists ? "Recreating DBTrackScan..." : "Creating DBTrackScan...";
            ResetScanPreparationAfterRecreation();
            await repository.CreateDatabaseAsync(recreateExistingDatabase: exists);
            txtDatabaseComments.Text = "DBTrackScan and its preparation, epoch, hourly and daily tables are ready.";
        });
    }

    private bool ConfirmDatabaseRecreation(string databaseName)
    {
        MessageBoxResult confirmation = MessageBox.Show(owner: this,
            messageBoxText: $"Database '{databaseName}' already exists.\n\n" +
                "Recreating the database will permanently delete ALL existing data and completely recreate the database and all table structures.\n\n" +
                "THIS OPERATION CANNOT BE UNDONE.\n\nDo you want to permanently delete and recreate the database?",
            caption: "Recreate Database Warning", button: MessageBoxButton.YesNo,
            icon: MessageBoxImage.Warning, defaultResult: MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return false;
        ConfirmDatabaseRecreationWindow warning = new(databaseName: databaseName) { Owner = this };
        return warning.ShowDialog() == true;
    }
    #endregion
}
#endregion
