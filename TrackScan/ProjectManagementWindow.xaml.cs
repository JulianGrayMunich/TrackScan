#region Project Management Window
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace TrackScan;

public partial class ProjectManagementWindow : Window
{
    #region State And Lifecycle
    private readonly ProjectManagementRepository _repository;
    private readonly Func<ManagedProject, Task> _activate;
    private readonly Func<IReadOnlyList<ManagedProject>, Task> _synchronise;
    private int? _activeId;
    private List<ManagedProject> _projects = new();
    private ManagedProject? _calendarTarget;
    private bool _busy;
    private bool _loading;

    internal ProjectManagementWindow(string connectionString, int? activeId, Func<ManagedProject, Task> activate, Func<IReadOnlyList<ManagedProject>, Task> synchronise)
    {
        _repository = new(connectionString: connectionString);
        _activeId = activeId;
        _activate = activate ?? throw new ArgumentNullException(paramName: nameof(activate));
        _synchronise = synchronise ?? throw new ArgumentNullException(paramName: nameof(synchronise));
        InitializeComponent();
        cmbZone.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        cmbZone.SelectedValue = TimeZoneInfo.Utc.Id;
        dpStart.SelectedDate = DateTime.Today;
        Loaded += WindowLoaded;
        Closing += WindowClosing;
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
        => await RunAsync(action: async () => { await ReloadAsync(selectId: _activeId); txtStatus.Text = $"{_projects.Count} project(s) loaded."; });
    private void WindowClosing(object? sender, CancelEventArgs e) { if (_busy) e.Cancel = true; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RunAsync(action: async () => { await ReloadAsync(selectId: (dgProjects.SelectedItem as ManagedProject)?.Id); txtStatus.Text = "Projects refreshed."; });
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; pnlManagement.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            string error = ex.Message;
            try { await ReloadAsync(selectId: null); } catch (Exception refreshError) { error += $"\nRefresh failed: {refreshError.Message}"; }
            txtStatus.Text = error;
        }
        finally { _busy = false; pnlManagement.IsEnabled = true; UpdateAddButton(); }
    }
    private async Task ReloadAsync(int? selectId)
    {
        List<ManagedProject> stored = await _repository.LoadAsync();
        await _synchronise(arg: stored);
        _loading = true;
        try
        {
            _projects = new();
            foreach (ManagedProject project in stored) _projects.Add(item: project with { IsActive = project.Id == _activeId });
            dgProjects.ItemsSource = _projects;
            foreach (ManagedProject project in _projects) if (project.Id == selectId) { dgProjects.SelectedItem = project; FillSettings(project: project); break; }
        }
        finally { _loading = false; }
        UpdateAddButton();
    }
    #endregion

    #region Settings And Start Date
    private void ProjectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && dgProjects.SelectedItem is ManagedProject project) FillSettings(project: project);
    }
    private void FillSettings(ManagedProject project)
    {
        dpStart.SelectedDate = project.StartDate; cmbZone.SelectedValue = project.TimeZoneId; txtOutput.Text = project.OutputPath;
    }
    private void RequiredChanged(object sender, RoutedEventArgs e) => UpdateAddButton();
    private void UpdateAddButton()
    {
        if (btnAdd is null || cmbZone is null || txtOutput is null) return;
        bool match = false;
        foreach (ManagedProject project in _projects)
            if (string.Equals(a: project.Name, b: txtName.Text.Trim(), comparisonType: StringComparison.OrdinalIgnoreCase)) { match = true; break; }
        btnAdd.Content = match ? "Overwrite" : "Add Project";
        btnAdd.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(value: txtName.Text) && dpStart.SelectedDate.HasValue && cmbZone.SelectedItem is TimeZoneInfo && !string.IsNullOrWhiteSpace(value: txtOutput.Text);
    }
    private void Output_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new() { Title = "Select Default Report Output Folder", Multiselect = false };
        if (Directory.Exists(path: txtOutput.Text)) dialog.InitialDirectory = txtOutput.Text;
        if (dialog.ShowDialog(owner: this) == true) txtOutput.Text = dialog.FolderName;
    }
    private void Start_Click(object sender, RoutedEventArgs e) => dpStart.IsDropDownOpen = true;
    private void CalendarOpened(object? sender, RoutedEventArgs e)
        => _calendarTarget = string.IsNullOrWhiteSpace(value: txtName.Text) ? dgProjects.SelectedItem as ManagedProject : null;
    private async void CalendarClosed(object? sender, RoutedEventArgs e)
    {
        ManagedProject? target = _calendarTarget; _calendarTarget = null;
        if (!dpStart.SelectedDate.HasValue) return;
        DateTime date = dpStart.SelectedDate.Value.Date;
        if (target is null) { txtStatus.Text = $"Project start date for new project: {date:yyyy-MM-dd}."; return; }
        await RunAsync(action: async () =>
        {
            await _repository.SetStartDateAsync(expected: target, startDate: date);
            await ReloadAsync(selectId: target.Id);
            txtStatus.Text = $"Project '{target.Name}' start date set to {date:yyyy-MM-dd}.";
        });
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(action: async () =>
        {
            ManagedProject project = dgProjects.SelectedItem as ManagedProject ?? throw new InvalidOperationException(message: "Select a project before saving settings.");
            string zone = (cmbZone.SelectedItem as TimeZoneInfo)?.Id ?? throw new InvalidOperationException(message: "Select a project time zone.");
            await _repository.SaveSettingsAsync(expected: project, timeZoneId: zone, outputPath: txtOutput.Text);
            await ReloadAsync(selectId: project.Id);
            txtStatus.Text = "Project time zone and default report output path saved.";
        });
    }
    #endregion

    #region Add Overwrite Restore Rename And Select
    private bool Confirm(string message, string title) => MessageBox.Show(owner: this, messageBoxText: message,
        caption: title, button: MessageBoxButton.YesNo, icon: MessageBoxImage.Question, defaultResult: MessageBoxResult.No) == MessageBoxResult.Yes;
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(action: async () =>
        {
            string name = txtName.Text.Trim();
            ManagedProject? existing = await _repository.FindAsync(name: name);
            if (existing is not null)
            {
                string prompt = existing.IsDeleted ? $"Project '{existing.Name}' is marked as deleted. Restore this project?"
                    : $"Overwrite the configuration values for project '{existing.Name}'?\n\nThe existing Project_ID and all associated project data will be retained.";
                if (!Confirm(message: prompt, title: existing.IsDeleted ? "Restore Project" : "Confirm Project Overwrite")) { txtStatus.Text = "Project change cancelled."; return; }
            }
            await _repository.SaveProjectAsync(name: name, startDate: dpStart.SelectedDate ?? throw new InvalidOperationException(message: "Select a start date."),
                timeZoneId: (cmbZone.SelectedItem as TimeZoneInfo)?.Id ?? throw new InvalidOperationException(message: "Select a time zone."), outputPath: txtOutput.Text, expected: existing);
            txtName.Clear(); txtOutput.Clear(); cmbZone.SelectedValue = TimeZoneInfo.Utc.Id; dpStart.SelectedDate = DateTime.Today;
            await ReloadAsync(selectId: null);
            txtStatus.Text = $"Project '{name}' {(existing is null ? "added" : existing.IsDeleted ? "restored" : "overwritten")} successfully.";
        });
    }
    private async void Rename_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_busy || _loading || sender is not TextBox editor || editor.DataContext is not ManagedProject project) return;
        string name = editor.Text.Trim();
        if (name == project.Name) return;
        await RunAsync(action: async () =>
        {
            await _repository.RenameAsync(expected: project, name: name);
            await ReloadAsync(selectId: project.Id);
            txtStatus.Text = $"Project renamed to '{name}'.";
        });
    }
    private async void Active_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button || button.DataContext is not ManagedProject project) return;
        await RunAsync(action: async () =>
        {
            if (project.IsDeleted) throw new InvalidOperationException(message: "A deleted project cannot be active.");
            await _activate(arg: project); _activeId = project.Id;
            await ReloadAsync(selectId: project.Id);
            txtStatus.Text = $"Active project: {project.Name}.";
        });
    }
    private async void Deleted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not ManagedProject project) return;
        bool deleted = box.IsChecked == true;
        await RunAsync(action: async () =>
        {
            await _repository.SetDeletedAsync(expected: project, deleted: deleted, activeProjectId: _activeId);
            await ReloadAsync(selectId: project.Id);
            txtStatus.Text = $"Project '{project.Name}' {(deleted ? "deleted" : "restored")}.";
        });
    }
    #endregion
}
#endregion
