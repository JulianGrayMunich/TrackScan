#region Scan Filename Time
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
namespace TrackScan.Core;

public sealed record ScanTimestamp(DateTime Utc, string Source);

public static partial class TrackScan
{
    public const string FilenameTimestampSource = "Filename local time";
    public const string FallbackTimestampSource = "Current UTC fallback";

    public static DateTime RoundScanSecond(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException(message: "The scan timestamp must be UTC.");
        long ticks = checked((utc.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
        return new DateTime(ticks: ticks, kind: DateTimeKind.Utc);
    }

    public static ScanTimestamp ResolveScanTimestamp(string path, string timeZoneId, DateTime utcNow)
    {
        bool hasToken = Regex.IsMatch(input: Path.GetFileNameWithoutExtension(path: path), pattern: @"(?<!\d)\d{8}_\d{6}(?!\d)",
            options: RegexOptions.CultureInvariant, matchTimeout: TimeSpan.FromSeconds(value: 1));
        return hasToken ? new(Utc: ScanEpochFromFilename(path: path, timeZoneId: timeZoneId), Source: FilenameTimestampSource)
            : new(Utc: RoundScanSecond(utc: utcNow), Source: FallbackTimestampSource);
    }

    public static DateTime ScanEpochFromFilename(string path, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(value: timeZoneId))
            throw new InvalidOperationException(message: "The active project has no TimeZoneId. Set its time zone before processing.");
        MatchCollection matches = Regex.Matches(input: Path.GetFileNameWithoutExtension(path: path),
            pattern: @"(?<!\d)\d{8}_\d{6}(?!\d)", options: RegexOptions.CultureInvariant,
            matchTimeout: TimeSpan.FromSeconds(value: 1));
        if (matches.Count != 1 || !DateTime.TryParseExact(s: matches[0].Value, format: "yyyyMMdd_HHmmss",
            provider: CultureInfo.InvariantCulture, style: DateTimeStyles.None, result: out DateTime local))
            throw new InvalidOperationException(message: "The LAS filename must contain exactly one valid YYYYMMDD_HHmmss scan timestamp.");
        local = DateTime.SpecifyKind(value: local, kind: DateTimeKind.Unspecified);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: timeZoneId);
        if (zone.IsInvalidTime(dateTime: local))
            throw new InvalidOperationException(message: "The filename time does not exist in the project time zone because of a daylight-saving change. No readings saved.");
        if (zone.IsAmbiguousTime(dateTime: local))
            throw new InvalidOperationException(message: "The filename time is ambiguous in the project time zone because of a daylight-saving change. No readings saved.");
        return RoundScanMinute(utc: TimeZoneInfo.ConvertTimeToUtc(dateTime: local, sourceTimeZone: zone));
    }
}
#endregion
