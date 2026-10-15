#region Windows Task Scheduler Registration And Inspection
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml;
using System.Xml.Linq;
namespace TrackScan;

public sealed record ScanTaskSnapshot(bool Installed, bool Enabled, bool Running, DateTime? NextUtc, int LastResult);
public sealed class ScanWindowsScheduler : IDisposable
{
    public const string TaskName = "GNA_TrackScanScheduler";
    private const int TaskCreateOrUpdate = 6;
    private const int PasswordLogon = 1;
    private readonly List<object> _objects = new();
    private readonly dynamic _folder;
    public ScanWindowsScheduler()
    {
        Type type = Type.GetTypeFromProgID(progID: "Schedule.Service") ?? throw new InvalidOperationException(message: "Windows Task Scheduler is unavailable.");
        dynamic service = Keep(instance: Activator.CreateInstance(type: type) ?? throw new InvalidOperationException(message: "Cannot connect to Task Scheduler."));
        try { service.Connect(); _folder = Keep(instance: service.GetFolder(path: "\\")); }
        catch { Dispose(); throw; }
    }
    private dynamic Keep(object instance)
    {
        foreach (object existing in _objects) if (ReferenceEquals(objA: existing, objB: instance)) return instance;
        _objects.Add(item: instance); return instance;
    }
    private dynamic? Find()
    {
        try { return Keep(instance: _folder.GetTask(path: TaskName)); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x80070003)) { return null; }
    }
    internal static void VerifyOwnership(string xml, string sid)
    {
        XElement root = XDocument.Parse(text: xml).Root ?? throw new InvalidOperationException(message: "Invalid scan task.");
        if ((string?)root.Element(name: ScanTaskDefinition.Ns + "RegistrationInfo")?.Element(name: ScanTaskDefinition.Ns + "Source") != "TrackScan:" + sid)
            throw new InvalidOperationException(message: "The scan task belongs to another Windows account or application. No changes were made.");
    }
    public ScanTaskSnapshot Read(string sid)
    {
        dynamic? task = Find();
        if (task is null) return new(Installed: false, Enabled: false, Running: false, NextUtc: null, LastResult: 0);
        VerifyOwnership(xml: (string)task.Xml, sid: sid);
        DateTime next = (DateTime)task.NextRunTime;
        bool enabled = (bool)task.Enabled;
        return new(Installed: true, Enabled: enabled, Running: (int)task.State is 2 or 4,
            NextUtc: enabled && next.Year >= 2000 ? DateTime.SpecifyKind(value: next, kind: DateTimeKind.Local).ToUniversalTime() : null,
            LastResult: (int)task.LastTaskResult);
    }
    public void Disable(string sid)
    {
        dynamic? task = Find(); if (task is null) return;
        VerifyOwnership(xml: (string)task.Xml, sid: sid); task.Enabled = false;
    }
    public void EnsureLegacyScheduleStopped()
    {
        dynamic? legacy;
        try { legacy = Keep(instance: _folder.GetTask(path: "GNA_DLRScanScheduler")); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x80070003)) { return; }
        if ((bool)legacy.Enabled || (int)legacy.State is 2 or 4)
            throw new InvalidOperationException(message: "Stop the DLR Report scan schedule and allow its current run to finish before using the TrackScan schedule. The DLR task has not been changed.");
    }
    public void Register(ScanSchedulerSettings settings, SecureString password, DateTime utcNow)
    {
        EnsureLegacyScheduleStopped();
        ScanTaskSnapshot prior = Read(sid: settings.OwnerSid);
        if (prior.Enabled || prior.Running) throw new InvalidOperationException(message: "Stop the existing schedule and allow its current scan to finish before changing it.");
        string xml = ScanTaskDefinition.Build(settings: settings, utcNow: utcNow);
        IntPtr buffer = Marshal.SecureStringToBSTR(s: password);
        try
        {
            string clear = Marshal.PtrToStringBSTR(ptr: buffer);
            try { Keep(instance: _folder.RegisterTask(path: TaskName, xmlText: xml, flags: TaskCreateOrUpdate,
                userId: settings.OwnerName, password: clear, logonType: PasswordLogon, sddl: null)); }
            finally { clear = string.Empty; }
        }
        finally { Marshal.ZeroFreeBSTR(s: buffer); }
    }
    public void RunNow(string sid)
    {
        dynamic task = Find() ?? throw new InvalidOperationException(message: "Scan task is not installed.");
        VerifyOwnership(xml: (string)task.Xml, sid: sid);
        // Same verified COM correction as DBDayReductions: IRegisteredTask.Run
        // requires a positional argument; a named argument causes DISP_E_UNKNOWNNAME.
        if ((bool)task.Enabled && (int)task.State is not (2 or 4)) Keep(instance: task.Run(null));
    }
    public void Dispose()
    {
        for (int index = _objects.Count - 1; index >= 0; index--)
            if (Marshal.IsComObject(o: _objects[index])) Marshal.FinalReleaseComObject(o: _objects[index]);
        _objects.Clear();
    }
}
public static class ScanTaskDefinition
{
    public static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string Build(ScanSchedulerSettings settings, DateTime utcNow)
    {
        if (!Path.IsPathFullyQualified(path: settings.ExecutablePath) || string.IsNullOrWhiteSpace(value: settings.OwnerSid) || string.IsNullOrWhiteSpace(value: settings.OwnerName))
            throw new InvalidOperationException(message: "A full executable path and Windows account are required.");
        if (settings.IntervalMinutes < 1 || settings.IntervalMinutes > 10080 || settings.StartUtc.Kind != DateTimeKind.Utc || utcNow.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException(message: "Invalid scan interval or UTC schedule boundary.");
        XElement E(string name, object? value) => new(name: Ns + name, content: value);
        // Start performs one immediate catch-up separately; future repetition remains anchored.
        DateTime first = settings.StartUtc > utcNow ? settings.StartUtc : settings.NextAfter(utc: utcNow);
        XElement root = new(name: Ns + "Task", content: new object[] { new XAttribute(name: "version", value: "1.2"),
            E(name: "RegistrationInfo", value: new object[] { E(name: "Description", value: "GNA TrackScan temporary five-second processing placeholder"), E(name: "Source", value: "TrackScan:" + settings.OwnerSid) }),
            E(name: "Triggers", value: E(name: "TimeTrigger", value: new object[] {
                E(name: "Repetition", value: new object[] { E(name: "Interval", value: XmlConvert.ToString(value: TimeSpan.FromMinutes(value: settings.IntervalMinutes))), E(name: "StopAtDurationEnd", value: false) }),
                E(name: "StartBoundary", value: first.ToString(format: "yyyy-MM-ddTHH:mm:ss'Z'", provider: CultureInfo.InvariantCulture)), E(name: "Enabled", value: true) })),
            E(name: "Principals", value: new XElement(name: Ns + "Principal", content: new object[] { new XAttribute(name: "id", value: "Operator"), E(name: "UserId", value: settings.OwnerSid), E(name: "LogonType", value: "Password"), E(name: "RunLevel", value: "HighestAvailable") })),
            E(name: "Settings", value: new object[] {
                E(name: "MultipleInstancesPolicy", value: "IgnoreNew"), E(name: "DisallowStartIfOnBatteries", value: false), E(name: "StopIfGoingOnBatteries", value: false),
                E(name: "AllowHardTerminate", value: true), E(name: "StartWhenAvailable", value: true), E(name: "RunOnlyIfNetworkAvailable", value: false),
                E(name: "IdleSettings", value: new object[] { E(name: "StopOnIdleEnd", value: false), E(name: "RestartOnIdle", value: false) }),
                E(name: "AllowStartOnDemand", value: true), E(name: "Enabled", value: true), E(name: "Hidden", value: false), E(name: "RunOnlyIfIdle", value: false), E(name: "WakeToRun", value: false), E(name: "ExecutionTimeLimit", value: "PT0S"), E(name: "Priority", value: 7) }),
            E(name: "Actions", value: new XElement(name: Ns + "Exec", content: new object[] { E(name: "Command", value: settings.ExecutablePath), E(name: "Arguments", value: "--run-scan-schedule"), E(name: "WorkingDirectory", value: Path.GetDirectoryName(path: settings.ExecutablePath)) })) });
        root.Element(name: Ns + "Actions")!.SetAttributeValue(name: "Context", value: "Operator");
        return root.ToString();
    }
}
#endregion
