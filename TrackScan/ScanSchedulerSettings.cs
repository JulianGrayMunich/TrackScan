#region Scheduler Configuration And Status
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
namespace TrackScan;
public sealed record ScanSchedulerSettings
{
    public Guid Revision { get; init; } = Guid.NewGuid();
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public Guid ScanDatabaseIdentity { get; init; }
    public string ProtectedConnection { get; init; } = string.Empty;
    public string OwnerSid { get; init; } = string.Empty;
    public string OwnerName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string TimeZoneId { get; init; } = string.Empty;
    public string ServerTimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    public DateTime StartUtc { get; init; }
    public int IntervalMinutes { get; init; } = 60;
    public int RejectionCount { get; init; } = 50;
    public string InputFolder { get; init; } = string.Empty;
    public string ProcessedFolder { get; init; } = string.Empty;
    public string LogPath { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Connection() => Encoding.UTF8.GetString(bytes: ProtectedData.Unprotect(encryptedData: Convert.FromBase64String(s: ProtectedConnection), optionalEntropy: null, scope: DataProtectionScope.LocalMachine));
    public static string Protect(string connection) => Convert.ToBase64String(inArray: ProtectedData.Protect(userData: Encoding.UTF8.GetBytes(s: connection), optionalEntropy: null, scope: DataProtectionScope.LocalMachine));
    public void Validate()
    {
        if (ProjectId <= 0 || ScanDatabaseIdentity == Guid.Empty || StartUtc.Kind != DateTimeKind.Utc || IntervalMinutes < 1 || IntervalMinutes > 10080 || RejectionCount is not (10 or 25 or 50 or 75 or 100))
            throw new InvalidOperationException(message: "Select a project, UTC start, frequency of 1–10080 minutes and valid rejection count.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(id: TimeZoneId);
        _ = TimeZoneInfo.FindSystemTimeZoneById(id: ServerTimeZoneId);
        if (!Path.IsPathFullyQualified(path: InputFolder) || !Path.IsPathFullyQualified(path: ProcessedFolder) || !Path.IsPathFullyQualified(path: LogPath))
            throw new InvalidOperationException(message: "Use absolute paths for the incoming folder, processed folder and log file.");
        if (string.Equals(a: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: InputFolder)), b: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: ProcessedFolder)), comparisonType: StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(message: "Incoming and processed folders must be different.");
        _ = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString: Connection());
    }
    public static DateTime LocalStartToUtc(DateTime local, string zoneId)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: zoneId); local = DateTime.SpecifyKind(value: local, kind: DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(dateTime: local) || zone.IsAmbiguousTime(dateTime: local)) throw new InvalidOperationException(message: "Start time is invalid or ambiguous at a daylight-saving transition. Choose another time.");
        return TimeZoneInfo.ConvertTimeToUtc(dateTime: local, sourceTimeZone: zone);
    }
    public DateTime NextAfter(DateTime utc)
    {
        if (utc < StartUtc) return StartUtc;
        long interval = TimeSpan.FromMinutes(value: IntervalMinutes).Ticks;
        return new DateTime(ticks: checked(StartUtc.Ticks + ((utc.Ticks - StartUtc.Ticks) / interval + 1) * interval), kind: DateTimeKind.Utc);
    }
}
public sealed record ScanSchedulerStatus(DateTime HeartbeatUtc, Guid Configuration, string State, string Message, DateTime? NextUtc, DateTime? LastRunUtc);
public static class ScanSchedulerStore
{
    
    public static string Root => Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.CommonApplicationData), path2: "GNA Software", path3: "TrackScan Scheduler");
    public static string WorkerLockPath => Path.Combine(path1: Root, path2: "worker.lock");
    public static string StatusPath => Path.Combine(path1: Root, path2: "status.json");
    public static T? Read<T>(string path) => File.Exists(path: path) ? JsonSerializer.Deserialize<T>(json: File.ReadAllText(path: path)) : default;
    public static void Write<T>(string path, T value)
    {
        string temp = path + "." + Guid.NewGuid().ToString(format: "N") + ".tmp";
        try { File.WriteAllText(path: temp, contents: JsonSerializer.Serialize(value: value)); File.Move(sourceFileName: temp, destFileName: path, overwrite: true); }
        finally { if (File.Exists(path: temp)) File.Delete(path: temp); }
    }
    public static void SavePrivileged(ScanSchedulerSettings settings)
    {
        settings.Validate(); DirectoryInfo dir = Directory.CreateDirectory(path: Root);
        DirectorySecurity acl = new(); acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(rule: new(identity: new SecurityIdentifier(sidType: sid, domainSid: null), fileSystemRights: FileSystemRights.FullControl, inheritanceFlags: InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));
        acl.AddAccessRule(rule: new(identity: new SecurityIdentifier(sidType: WellKnownSidType.AuthenticatedUserSid, domainSid: null), fileSystemRights: FileSystemRights.ReadAndExecute, inheritanceFlags: InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));
        acl.AddAccessRule(rule: new(identity: new SecurityIdentifier(sddlForm: settings.OwnerSid), fileSystemRights: FileSystemRights.Modify, inheritanceFlags: InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));
        dir.SetAccessControl(directorySecurity: acl);
        // Private subdirectory prevents newly-created config temp files inheriting public read access.
        string privatePath = Path.Combine(path1: Root, path2: "private"); DirectoryInfo privateDir = Directory.CreateDirectory(path: privatePath);
        DirectorySecurity privateAcl = new(); privateAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            privateAcl.AddAccessRule(rule: new(identity: new SecurityIdentifier(sidType: sid, domainSid: null), fileSystemRights: FileSystemRights.FullControl, inheritanceFlags: InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));
        privateAcl.AddAccessRule(rule: new(identity: new SecurityIdentifier(sddlForm: settings.OwnerSid), fileSystemRights: FileSystemRights.ReadAndExecute, inheritanceFlags: InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));
        privateDir.SetAccessControl(directorySecurity: privateAcl); Write(path: PrivateConfigPath, value: settings);
    }
    public static string PrivateConfigPath => Path.Combine(path1: Root, path2: "private", path3: "configuration.json");
}
#endregion
