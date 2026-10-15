using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Unattended Scan Engine
using System.IO;
using System.Security.Cryptography;
namespace TrackScan;
public sealed class ScanSchedulerEngine
{
    public static IReadOnlyList<ScanProcessingRail> EligibleRails(IReadOnlyList<ScanProcessingRail> rails)
    {
        List<ScanProcessingRail> selected = new();
        foreach (var track in rails.GroupBy(keySelector: rail => rail.TrackId))
        {
            var pair = track.ToArray();
            if (pair.Length != 2 || !pair.Any(predicate: rail => rail.Rail == "R") || !pair.Any(predicate: rail => rail.Rail == "L")) continue;
            if (pair.Any(predicate: rail => rail.Corridor is null || rail.Heads.Count == 0 || rail.Heads.Any(predicate: head => !head.ScanOffset.HasValue || !double.IsFinite(d: head.ScanOffset.Value)))) continue;
            selected.Add(item: pair.Single(predicate: rail => rail.Rail == "R")); selected.Add(item: pair.Single(predicate: rail => rail.Rail == "L"));
        }
        return selected.AsReadOnly();
    }
    public static string Fingerprint(string path)
    {
        using FileStream stream = new(path: path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read, bufferSize: 1024 * 1024, options: FileOptions.SequentialScan);
        return Convert.ToHexString(inArray: SHA256.HashData(source: stream));
    }
    public static void Log(ScanSchedulerSettings settings, string message)
    {
        string directory = Path.GetDirectoryName(path: settings.LogPath) ?? throw new IOException(message: "No log directory.");
        Directory.CreateDirectory(path: directory);
        string password = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString: settings.Connection()).Password;
        if (!string.IsNullOrEmpty(value: password)) message = message.Replace(oldValue: password, newValue: "[redacted]", comparisonType: StringComparison.Ordinal);
        File.AppendAllText(path: settings.LogPath, contents: $"{DateTime.UtcNow:O} | Project {settings.ProjectId} | {message.Replace(oldValue: "\r", newValue: " ").Replace(oldValue: "\n", newValue: " ")}{Environment.NewLine}");
    }
    public async Task RunCycleAsync(ScanSchedulerSettings settings, IProgress<string>? progress, CancellationToken cancellation)
    {
        settings.Validate();
        if (!Directory.Exists(path: settings.InputFolder)) throw new DirectoryNotFoundException(message: "Incoming scan folder does not exist or is inaccessible to the scheduled Windows account.");
        Directory.CreateDirectory(path: settings.ProcessedFolder);
        Log(settings: settings, message: "Search started (selected folder only)."); // Require working logging before any data processing.
        ScanRepository repository = new(baseConnectionString: settings.Connection(), geometryProjectId: settings.ProjectId);
        ScanProjectState state = await repository.LoadAsync();
        if (state.DatabaseIdentity != settings.ScanDatabaseIdentity) throw new InvalidOperationException(message: "DBTrackScan was recreated. Review and save scheduler configuration again.");
        string zone = await repository.LoadProcessingTimeZoneAsync();
        if (zone != settings.TimeZoneId) throw new InvalidOperationException(message: "Project time zone changed. Review and save the schedule again.");
        IReadOnlyList<ScheduledScanFile> committed = await repository.ScheduledFilesAsync(expected: state, pendingOnly: true);
        Dictionary<string, ScheduledScanFile> byHash = committed.ToDictionary(keySelector: file => file.Hash, comparer: StringComparer.OrdinalIgnoreCase);
        foreach (ScheduledScanFile file in committed)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!file.Logged)
            {
                try { await FinishArchiveAsync(settings: settings, repository: repository, state: state, file: file); }
                catch (Exception ex) { Log(settings: settings, message: $"Unsuccessful archive/log recovery: {file.SourcePath}: {ex.Message}"); }
            }
        }
        IReadOnlyList<ScanProcessingRail> available = await repository.LoadProcessingRailsAsync(expected: state);
        IReadOnlyList<ScanProcessingRail> rails = EligibleRails(rails: available);
        if (rails.Count == 0) throw new InvalidOperationException(message: "No tracks have both current rail polygons and adopted offsets for every railhead point.");
        if (rails.Count != available.Count) Log(settings: settings, message: "Warning: tracks without both current rail polygons and complete adopted offsets are excluded.");
        string[] paths = Directory.GetFiles(path: settings.InputFolder, searchPattern: "*.las", searchOption: SearchOption.TopDirectoryOnly);
        Array.Sort(array: paths, comparer: StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(value: "Checking " + Path.GetFileName(path: path));
                FileInfo before = new(fileName: path); long size = before.Length; DateTime write = before.LastWriteTimeUtc;
                await Task.Delay(delay: TimeSpan.FromSeconds(value: 2), cancellationToken: cancellation);
                before.Refresh(); if (!before.Exists || size != before.Length || write != before.LastWriteTimeUtc) { Log(settings: settings, message: "Deferred; file still changing: " + path); continue; }
                ScheduledScanFile saved;
                // This handle denies concurrent writers/deletion while hashing, processing and committing.
                using (FileStream guard = new(path: path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read, bufferSize: 1024 * 1024, options: FileOptions.SequentialScan))
                {
                    string hash = Convert.ToHexString(inArray: SHA256.HashData(source: guard));
                    cancellation.ThrowIfCancellationRequested();
                    if (!byHash.ContainsKey(key: hash))
                    {
                        var prior = await repository.ScheduledFilesAsync(expected: state, hash: hash);
                        if (prior.Count > 0) byHash.Add(key: hash, value: prior[0]);
                    }
                    if (byHash.TryGetValue(key: hash, value: out ScheduledScanFile? existing))
                    {
                        string duplicateArchive = ArchiveName(settings: settings, source: path, hash: hash);
                        saved = existing with { SourcePath = path, ArchivePath = duplicateArchive };
                    }
                    else
                    {
                        // Refresh preparation immediately before each file, so a stale baseline cannot slip through.
                        ScanProjectState current = await repository.LoadAsync();
                        if (current.Revision != state.Revision || current.DatabaseIdentity != state.DatabaseIdentity) throw new InvalidOperationException(message: "Preparation changed during the search. Retry on the next scheduled check.");
                        rails = EligibleRails(rails: await repository.LoadProcessingRailsAsync(expected: current));
                        if (rails.Count == 0) throw new InvalidOperationException(message: "No eligible tracks remain.");
                        ScanTimestamp stamp = ScanEngine.ResolveScanTimestamp(path: path, timeZoneId: zone, utcNow: DateTime.UtcNow);
                        ScanProcessingResult result = await Task.Run(function: () => ScanEngine.ProcessLas(path: path, utcTimestamp: stamp.Utc, rails: rails,
                            progress: progress, calculateOffsets: false, cancellationToken: cancellation, rejectionCount: settings.RejectionCount), cancellationToken: cancellation);
                        if (!string.Equals(a: result.Sha256, b: hash, comparisonType: StringComparison.Ordinal)) throw new IOException(message: "File content changed during processing.");
                        result = result with { TimeZoneId = zone, TimestampSource = stamp.Source };
                        cancellation.ThrowIfCancellationRequested();
                        string archive = ArchiveName(settings: settings, source: path, hash: hash);
                        await repository.SaveScanEpochAsync(expected: current, result: result, scheduled: new(ArchivePath: archive));
                        saved = new(Hash: hash, SourcePath: path, ArchivePath: archive, EpochUtc: stamp.Utc, MissingCount: result.Readings.Count(predicate: reading => !reading.ScanHt.HasValue), Archived: false, Logged: false);
                        byHash.Add(key: hash, value: saved);
                    }
                }
                await FinishArchiveAsync(settings: settings, repository: repository, state: state, file: saved);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log(settings: settings, message: $"Unsuccessful: {path}: {ex.Message}"); progress?.Report(value: "Unsuccessful: " + Path.GetFileName(path: path)); }
        }
        Log(settings: settings, message: "Search completed.");
    }
    private static string ArchiveName(ScanSchedulerSettings settings, string source, string hash)
    {
        string name = Path.GetFileNameWithoutExtension(path: source);
        if (name.Length > 120) name = name.Substring(startIndex: 0, length: 120);
        return Path.Combine(path1: settings.ProcessedFolder, path2: name + "_" + hash + ".las");
    }
    public static void ArchiveCommitted(ScheduledScanFile file)
    {
        if (string.Equals(a: Path.GetFullPath(path: file.SourcePath), b: Path.GetFullPath(path: file.ArchivePath), comparisonType: StringComparison.OrdinalIgnoreCase)) throw new IOException(message: "Archive path equals source path.");
        using FileStream? sourceGuard = File.Exists(path: file.SourcePath)
            ? new FileStream(path: file.SourcePath, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read | FileShare.Delete) : null;
        if (File.Exists(path: file.ArchivePath))
        {
            if (Fingerprint(path: file.ArchivePath) != file.Hash) throw new IOException(message: "Archive destination contains different data; nothing overwritten.");
            if (File.Exists(path: file.SourcePath))
            {
                if (Fingerprint(path: file.SourcePath) != file.Hash) throw new IOException(message: "Source file changed after database commit; retained for investigation.");
                File.Delete(path: file.SourcePath);
            }
            return;
        }
        if (!File.Exists(path: file.SourcePath) || Fingerprint(path: file.SourcePath) != file.Hash) throw new IOException(message: "Committed source file missing or changed; cannot archive it.");
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: file.ArchivePath) ?? throw new IOException(message: "No archive directory."));
        File.Move(sourceFileName: file.SourcePath, destFileName: file.ArchivePath, overwrite: false);
    }
    private static async Task FinishArchiveAsync(ScanSchedulerSettings settings, ScanRepository repository, ScanProjectState state, ScheduledScanFile file)
    {
        ArchiveCommitted(file: file);
        await repository.MarkScheduledFileAsync(expected: state, hash: file.Hash, logged: false);
        Log(settings: settings, message: $"Successful: {file.SourcePath}; UTC {file.EpochUtc:O}; {file.MissingCount} missing readings stored as NULL; archived to {file.ArchivePath}; SHA256 {file.Hash}.");
        await repository.MarkScheduledFileAsync(expected: state, hash: file.Hash, logged: true);
    }
}
#endregion

