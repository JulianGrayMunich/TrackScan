using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Scan Scheduler Interface
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
namespace TrackScan;
public partial class MainWindow
{
    private ScanRepository SchedulerRepository() => new(
        baseConnectionString: GetValidatedDatabaseConnectionString(),
        geometryProjectId: _activeProjectId ?? throw new InvalidOperationException(message: "Select an active project on Project Selection first."));
    private DispatcherTimer? _schedulerTimer;
    private bool _schedulerLoaded, _schedulerBusy;
    private string _schedulerZone = string.Empty;
    private async void ScanScheduler_Selected(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(objA: sender, objB: e.OriginalSource) || txtSchedulerInput is null) return;
        // Let the parent TabControl receive Selected so it can display this page.
        // The OriginalSource guard above already excludes nested control events.
        if (!_schedulerLoaded)
        {
            _schedulerLoaded = true;
            cmbSchedulerServerZone.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
            cmbSchedulerServerZone.SelectedValue = TimeZoneInfo.Local.Id;
            try
            {
                ScanSchedulerSettings? saved = ScanSchedulerStore.Read<ScanSchedulerSettings>(path: ScanSchedulerStore.PrivateConfigPath);
                if (saved is not null)
                {
                    _schedulerZone = saved.TimeZoneId;
                    cmbSchedulerServerZone.SelectedValue = saved.ServerTimeZoneId;
                    txtSchedulerInput.Text = saved.InputFolder; txtSchedulerProcessed.Text = saved.ProcessedFolder; txtSchedulerLog.Text = saved.LogPath;
                    DateTime local = TimeZoneInfo.ConvertTimeFromUtc(dateTime: saved.StartUtc, destinationTimeZone: TimeZoneInfo.FindSystemTimeZoneById(id: saved.TimeZoneId));
                    dpSchedulerStart.SelectedDate = local.Date; txtSchedulerStartTime.Text = local.ToString(format: "HH:mm", provider: CultureInfo.InvariantCulture); SelectSchedulerInterval(minutes: saved.IntervalMinutes);
                    foreach (ComboBoxItem item in cmbSchedulerRejection.Items) if (item.Content.ToString() == saved.RejectionCount.ToString(provider: CultureInfo.InvariantCulture)) cmbSchedulerRejection.SelectedItem = item;
                }
                else { string zone = await SchedulerRepository().LoadProcessingTimeZoneAsync(); DateTime local = TimeZoneInfo.ConvertTimeFromUtc(dateTime: DateTime.UtcNow, destinationTimeZone: TimeZoneInfo.FindSystemTimeZoneById(id: zone)); dpSchedulerStart.SelectedDate = local.Date; txtSchedulerStartTime.Text = local.ToString(format: "HH:mm", provider: CultureInfo.InvariantCulture); }
            }
            catch (Exception ex) { txtSchedulerServiceStatus.Text = "Cannot load saved configuration: " + ex.Message; }
            _schedulerTimer = new() { Interval = TimeSpan.FromSeconds(value: 1) }; _schedulerTimer.Tick += (_, _) => SchedulerTick(); _schedulerTimer.Start();
            Closed += (_, _) => _schedulerTimer.Stop();
        }
        SchedulerTick(); await SchedulerSummaryAsync();
    }
    private void SchedulerTick()
    {
        
        try
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException(message: "Windows identity unavailable.");
            using ScanWindowsScheduler scheduler = new();
            ScanTaskSnapshot task = scheduler.Read(sid: sid);
            ScanSchedulerStatus? progress = ScanSchedulerStore.Read<ScanSchedulerStatus>(path: ScanSchedulerStore.StatusPath);
            ScanSchedulerSettings? settings = ScanSchedulerStore.Read<ScanSchedulerSettings>(path: ScanSchedulerStore.PrivateConfigPath);
            if (progress?.Configuration != settings?.Revision) progress = null;
            PresentSchedulerPlaceholder(progress: progress);
            PresentTaskSnapshot(task: task, progress: progress, nowUtc: DateTime.UtcNow, settingsSaved: settings is not null);
        }
        catch (Exception ex) { txtSchedulerCountdown.Text = "Scheduler unavailable"; txtSchedulerServiceStatus.Text = ex.Message; }
    }
    private void SelectSchedulerInterval(int minutes)
    {
        cmbSchedulerInterval.SelectedIndex = -1;
        foreach (ComboBoxItem item in cmbSchedulerInterval.Items)
            if (int.TryParse(s: item.Tag?.ToString(), result: out int value) && value == minutes)
            { cmbSchedulerInterval.SelectedItem = item; return; }
        txtSchedulerComments.Text = $"The saved interval is {minutes} minutes. Select an interval from Every before saving. The existing schedule has not been changed.";
    }
    private void PresentTaskSnapshot(ScanTaskSnapshot task, ScanSchedulerStatus? progress, DateTime nowUtc, bool settingsSaved)
    {
        if (!task.Installed || !task.Enabled)
        {
            txtSchedulerCountdown.Text = "Scheduler Stopped";
            txtSchedulerServiceStatus.Text = task.Running
                ? "Future runs are stopped; the current placeholder is finishing."
                : (settingsSaved ? "Settings saved. " : "Scheduler not configured. ") + "Click Start to register and enable the Windows scheduled task.";
            return;
        }
        string message = $"Windows Task Scheduler enabled. Last result: 0x{task.LastResult:X8}.";
        if (task.Running)
            message += progress is not null && progress.State == "Running" && nowUtc - progress.HeartbeatUtc < TimeSpan.FromSeconds(value: 10)
                ? " " + progress.Message : " The placeholder task is queued or running.";
        else if (progress is not null) message += " " + progress.Message;
        PresentSchedulerStatus(status: new(HeartbeatUtc: nowUtc, Configuration: Guid.Empty, State: task.Running ? "Running" : "Waiting",
            Message: message, NextUtc: task.NextUtc, LastRunUtc: progress?.LastRunUtc), nowUtc: nowUtc);
    }
    private void PresentSchedulerStatus(ScanSchedulerStatus status, DateTime nowUtc)
    {
        txtSchedulerServiceStatus.Text = status.State + ": " + status.Message + (status.LastRunUtc.HasValue ? $" Last check: {status.LastRunUtc:yyyy-MM-dd HH:mm:ss} UTC." : string.Empty);
        if (status.NextUtc.HasValue)
        {
            TimeSpan remaining = status.NextUtc.Value - nowUtc; if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            txtSchedulerCountdown.Text = $"Next execution in {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
            txtSchedulerServiceStatus.Text += $" Next: {status.NextUtc:yyyy-MM-dd HH:mm:ss} UTC.";
        }
        else
        {
            txtSchedulerCountdown.Text = "Next execution in --:--:--";
            txtSchedulerServiceStatus.Text += " Windows has not yet reported the next execution time.";
        }
    }
    private async Task SchedulerSummaryAsync()
    {
        if (_schedulerBusy) return;
        try
        {
            ScanRepository repository = SchedulerRepository(); ScanProjectState state = await repository.LoadAsync();
            _schedulerZone = await repository.LoadProcessingTimeZoneAsync();
            UpdateSchedulerTimePreview();
            IReadOnlyList<ScanProcessingRail> rails = await repository.LoadProcessingRailsAsync(expected: state);
            IReadOnlyList<ScanProcessingRail> eligible = ScanSchedulerEngine.EligibleRails(rails: rails);
            List<string> lines = new() { $"Active project: {state.ProjectName}. Time zone: {_schedulerZone}. Eligible tracks: {eligible.Count / 2}." };
            foreach (var track in rails.GroupBy(keySelector: rail => rail.TrackId))
            {
                var first = track.First(); bool ready = eligible.Any(predicate: rail => rail.TrackId == first.TrackId);
                lines.Add(item: $"{first.TrackName}: {(ready ? "ready" : "requires both rail polygons and complete adopted offsets")}; {track.Sum(selector: rail => rail.Heads.Count)} railhead points; height filter {first.HeightFilterMillimetres} mm. LAS processing is temporarily disabled.");
            }
            ScanSchedulerSettings? saved = ScanSchedulerStore.Read<ScanSchedulerSettings>(path: ScanSchedulerStore.PrivateConfigPath);
            if (saved is not null) lines.Add(item: $"Saved schedule project: {saved.ProjectName}; every {saved.IntervalMinutes} minutes; rejection count {saved.RejectionCount}; {(saved.Enabled ? "enabled" : "disabled")}.");
            txtSchedulerSummary.Text = string.Join(separator: Environment.NewLine, values: lines);
        }
        catch (Exception ex) { txtSchedulerSummary.Text = "Settings summary unavailable: " + ex.Message; }
    }
    private void SchedulerBrowse_Click(object sender, RoutedEventArgs e)
    {
        string tag = ((Button)sender).Tag.ToString() ?? string.Empty;
        if (tag == "Log") { SaveFileDialog dialog = new() { Title = "System log file", Filter = "Log (*.log)|*.log|Text (*.txt)|*.txt", FileName = "ScanScheduler.log", OverwritePrompt = false }; if (dialog.ShowDialog(owner: this) == true) txtSchedulerLog.Text = dialog.FileName; }
        else { OpenFolderDialog dialog = new() { Title = tag == "Input" ? "Incoming LAS folder" : "Processed LAS folder" }; if (dialog.ShowDialog(owner: this) == true) { if (tag == "Input") txtSchedulerInput.Text = dialog.FolderName; else txtSchedulerProcessed.Text = dialog.FolderName; } }
    }
    private async Task<ScanSchedulerSettings> SchedulerSettingsAsync(bool enabled)
    {
        int projectId = _activeProjectId ?? throw new InvalidOperationException(message: "Select an active project.");
        string connection = GetValidatedDatabaseConnectionString();
        ScanRepository repository = new(baseConnectionString: connection, geometryProjectId: projectId); ScanProjectState state = await repository.LoadAsync();
        string zone = await repository.LoadProcessingTimeZoneAsync();
        if (_activeProjectId != projectId || _validatedDatabaseConnectionString != connection)
            throw new InvalidOperationException(message: "The active project or database connection changed. Review scheduler settings again.");
        _schedulerZone = zone; UpdateSchedulerTimePreview();
        if (!dpSchedulerStart.SelectedDate.HasValue || !TimeSpan.TryParseExact(input: txtSchedulerStartTime.Text.Trim(), format: @"hh\:mm", formatProvider: CultureInfo.InvariantCulture, result: out TimeSpan time) || time.TotalHours >= 24)
            throw new InvalidOperationException(message: "Enter a start date and time in HH:mm format.");
        if (cmbSchedulerInterval.SelectedItem is not ComboBoxItem intervalItem ||
            !int.TryParse(s: intervalItem.Tag?.ToString(), result: out int interval))
            throw new InvalidOperationException(message: "Select a scheduling interval from the Every dropdown.");
        ScanSchedulerSettings settings = new()
        {
            ProjectId = projectId, ProjectName = state.ProjectName, ScanDatabaseIdentity = state.DatabaseIdentity,
            ProtectedConnection = ScanSchedulerSettings.Protect(connection: connection),
            OwnerSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException(message: "Windows user identity unavailable."),
            OwnerName = WindowsIdentity.GetCurrent().Name, ExecutablePath = Environment.ProcessPath ?? throw new InvalidOperationException(message: "Executable path unavailable."),
            ServerTimeZoneId = (cmbSchedulerServerZone.SelectedItem as TimeZoneInfo)?.Id ?? throw new InvalidOperationException(message: "Select the server time zone."),
            TimeZoneId = zone, StartUtc = ScanSchedulerSettings.LocalStartToUtc(local: dpSchedulerStart.SelectedDate.Value.Date + time, zoneId: zone), IntervalMinutes = interval,
            RejectionCount = int.Parse(s: ((ComboBoxItem)cmbSchedulerRejection.SelectedItem).Content.ToString()!, provider: CultureInfo.InvariantCulture),
            InputFolder = Path.GetFullPath(path: txtSchedulerInput.Text.Trim()), ProcessedFolder = Path.GetFullPath(path: txtSchedulerProcessed.Text.Trim()), LogPath = Path.GetFullPath(path: txtSchedulerLog.Text.Trim()), Enabled = enabled
        };
        settings.Validate(); return settings;
    }
    private async Task ConfigureSchedulerAsync(string action)
    {
        if (_schedulerBusy) return; _schedulerBusy = true;
        string? staged = null;
        try
        {
            ScanSchedulerSettings settings = action == "stop"
                ? (ScanSchedulerStore.Read<ScanSchedulerSettings>(path: ScanSchedulerStore.PrivateConfigPath) ?? throw new InvalidOperationException(message: "Scheduler not configured.")) with { Enabled = false, Revision = Guid.NewGuid(), ExecutablePath = Environment.ProcessPath ?? throw new InvalidOperationException(message: "Executable path unavailable.") }
                : await SchedulerSettingsAsync(enabled: action == "start");
            string stagingDirectory = Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), path2: "GNA Software", path3: "Scheduler setup"); Directory.CreateDirectory(path: stagingDirectory);
            staged = Path.Combine(path1: stagingDirectory, path2: Guid.NewGuid().ToString(format: "N") + ".json"); ScanSchedulerStore.Write(path: staged, value: settings);
            ProcessStartInfo start = new(fileName: Environment.ProcessPath ?? throw new InvalidOperationException(message: "Executable path unavailable.")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add(item: "--configure-scheduler"); start.ArgumentList.Add(item: staged); start.ArgumentList.Add(item: action);
            using Process process = Process.Start(startInfo: start) ?? throw new InvalidOperationException(message: "Unable to open scheduler setup."); await process.WaitForExitAsync();
            if (process.ExitCode == 2) { txtSchedulerComments.Text = "Scheduler setup cancelled; no settings changed."; return; }
            if (process.ExitCode != 0) throw new InvalidOperationException(message: "Scheduler setup did not complete.");
            txtSchedulerComments.Text = action == "start" ? "Windows Task Scheduler enabled. Check the countdown and system log." : action == "stop" ? "Future scheduled runs stopped. Any running placeholder is allowed to finish." : "Settings saved. Click Start to register the task using your Windows password.";
        }
        catch (Exception ex) { MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Scan Scheduler", button: MessageBoxButton.OK, icon: MessageBoxImage.Error); }
        finally { if (staged is not null && File.Exists(path: staged)) File.Delete(path: staged); _schedulerBusy = false; SchedulerTick(); await SchedulerSummaryAsync(); }
    }
    private async void SchedulerSave_Click(object sender, RoutedEventArgs e) => await ConfigureSchedulerAsync(action: "save");
    private async void SchedulerStart_Click(object sender, RoutedEventArgs e) => await ConfigureSchedulerAsync(action: "start");
    private async void SchedulerStop_Click(object sender, RoutedEventArgs e) => await ConfigureSchedulerAsync(action: "stop");
    private async void SchedulerNames_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerBusy) return; _schedulerBusy = true;
        try { ScanRepository repository = SchedulerRepository(); ScanProjectState state = await repository.LoadAsync();
            ScanRailheadNamesConfirmationWindow confirmation = new(projectName: state.ProjectName) { Owner = this };
            if (confirmation.ShowDialog() != true) { txtSchedulerComments.Text = "Railhead name update cancelled. No names were changed."; return; }
            var report = await repository.UpdateRailheadNamesAsync(expected: state); txtSchedulerComments.Text = string.Join(separator: Environment.NewLine, values: report); MessageBox.Show(owner: this, messageBoxText: $"Added: {report.Count(predicate: line => line.StartsWith(value: "Added:", comparisonType: StringComparison.Ordinal))}. Existing: {report.Count(predicate: line => line.StartsWith(value: "Existing:", comparisonType: StringComparison.Ordinal))}. Conflicts: {report.Count(predicate: line => line.StartsWith(value: "CONFLICT", comparisonType: StringComparison.Ordinal))}. See Comments for the full report.", caption: "Railhead name check", button: MessageBoxButton.OK, icon: MessageBoxImage.Information); }
        catch (Exception ex) { MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Railhead names", button: MessageBoxButton.OK, icon: MessageBoxImage.Error); }
        finally { _schedulerBusy = false; }
    }
}
#endregion
