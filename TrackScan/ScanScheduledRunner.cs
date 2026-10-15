#region One Scheduled Scan Search
using System.IO;
using System.Security.Principal;
namespace TrackScan;
public static class ScanScheduledRunner
{
    public static async Task<int> RunAsync()
    {
        ScanSchedulerSettings? settings = null;
        DateTime started = DateTime.UtcNow;
        FileStream? guard = null;
        try
        {
            settings = ScanSchedulerStore.Read<ScanSchedulerSettings>(path: ScanSchedulerStore.PrivateConfigPath)
                ?? throw new InvalidOperationException(message: "No scan schedule is configured.");
            if (settings.OwnerSid != WindowsIdentity.GetCurrent().User?.Value) throw new InvalidOperationException(message: "The scheduled Windows account does not match the saved configuration.");
            settings.Validate();
            using (ScanWindowsScheduler scheduler = new())
                {
                    if (!settings.Enabled || !scheduler.Read(sid: settings.OwnerSid).Enabled) return 0;
                    scheduler.EnsureLegacyScheduleStopped();
                }
            try { guard = new(path: ScanSchedulerStore.WorkerLockPath, mode: FileMode.OpenOrCreate, access: FileAccess.ReadWrite, share: FileShare.None); }
            catch (IOException) { return 3; }
            ScanSchedulerSettings? current = ScanSchedulerStore.Read<ScanSchedulerSettings>(path: ScanSchedulerStore.PrivateConfigPath);
            if (current?.Revision != settings.Revision || !current.Enabled) return 0;
            WriteStatus(settings: settings, state: "Placeholder", message: "LAS processing placeholder - no LAS data is processed.", started: started);
            await RunPlaceholderAsync();
            WriteStatus(settings: settings, state: "Completed", message: "Five-second placeholder completed. No LAS files or readings were changed.", started: started);
            return 0;
        }
        catch (Exception ex)
        {
            if (settings is not null)
            {
                try { ScanSchedulerEngine.Log(settings: settings, message: "Unsuccessful scheduled placeholder: " + ex.Message); } catch { }
                WriteStatus(settings: settings, state: "Error", message: "Placeholder failed. Check the system log and account access.", started: started);
            }
            return 1;
        }
        finally { guard?.Dispose(); }
    }
    public static Task RunPlaceholderAsync() => Task.Delay(delay: TimeSpan.FromSeconds(value: 5));
    private static void WriteStatus(ScanSchedulerSettings settings, string state, string message, DateTime started)
    {
        try { ScanSchedulerStore.Write(path: ScanSchedulerStore.StatusPath, value: new ScanSchedulerStatus(HeartbeatUtc: DateTime.UtcNow,
            Configuration: settings.Revision, State: state, Message: message, NextUtc: null, LastRunUtc: started)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
#endregion
