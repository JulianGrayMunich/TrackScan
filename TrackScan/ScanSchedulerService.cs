#region TrackScan Windows Task Setup
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
namespace TrackScan;
// TrackScan owns its own task and settings; DLR Report registration is never modified.
public static class ScanSchedulerSetup
{
    public static int Configure(string stagedPath, string action)
    {
        try
        {
            if (action is not ("save" or "start" or "stop")) throw new ArgumentException(message: "Unknown scheduler action.");
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(ntIdentity: identity).IsInRole(role: WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException(message: "Administrator approval is required to configure the scheduled task.");
            ScanSchedulerSettings settings = ScanSchedulerStore.Read<ScanSchedulerSettings>(path: stagedPath)
                ?? throw new InvalidOperationException(message: "No staged scheduler settings.");
            if (settings.OwnerSid != identity.User?.Value) throw new InvalidOperationException(message: "Use the same Windows account to configure and run the schedule.");
            settings.Validate();
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException(message: "Executable path unavailable.");
            if (!string.Equals(a: Path.GetFullPath(path: settings.ExecutablePath), b: Path.GetFullPath(path: executable), comparisonType: StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(message: "The schedule must launch this application executable.");
            settings = settings with { OwnerName = identity.Name };
            using ScanWindowsScheduler scheduler = new();
            if (action == "start") scheduler.EnsureLegacyScheduleStopped();
            ScanTaskSnapshot prior = scheduler.Read(sid: settings.OwnerSid);
            if (action != "stop" && prior.Running) throw new InvalidOperationException(message: "Stop the schedule and allow its current scan to finish before saving or starting again.");
            ScanSchedulerPasswordWindow? prompt = null;
            if (action == "start")
            {
                prompt = new(account: settings.OwnerName);
                if (prompt.ShowDialog() != true) return 2;
            }
            try
            {
                scheduler.Disable(sid: settings.OwnerSid);
                if (action == "stop")
                {
                    ScanSchedulerStore.SavePrivileged(settings: settings with { Enabled = false });
                    return 0;
                }
                Directory.CreateDirectory(path: ScanSchedulerStore.Root);
                using FileStream guard = new(path: ScanSchedulerStore.WorkerLockPath, mode: FileMode.OpenOrCreate, access: FileAccess.ReadWrite, share: FileShare.None);
                ScanSchedulerStore.SavePrivileged(settings: settings with { Enabled = false });
                if (action == "save") return 0;
                try
                {
                    using System.Security.SecureString password = prompt!.Password;
                    scheduler.Register(settings: settings, password: password, utcNow: DateTime.UtcNow);
                    ScanSchedulerStore.SavePrivileged(settings: settings with { Enabled = true });
                }
                catch
                {
                    scheduler.Disable(sid: settings.OwnerSid);
                    ScanSchedulerStore.SavePrivileged(settings: settings with { Enabled = false });
                    throw;
                }
                guard.Dispose();
                if (settings.StartUtc <= DateTime.UtcNow) scheduler.RunNow(sid: settings.OwnerSid);
                return 0;
            }
            finally { prompt?.ClearPassword(); }
        }
        catch (Exception ex) { MessageBox.Show(messageBoxText: ex.Message, caption: "Scan Scheduler setup", button: MessageBoxButton.OK, icon: MessageBoxImage.Error); return 1; }
    }
}
#endregion
