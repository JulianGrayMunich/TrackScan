using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Data;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;
#endregion

namespace TrackScan;

#region Scan Preparation Models
public sealed record ScanSurveyPreview(long Record, string PointName, string Easting, string Northing, string Height, string Status);
public sealed record ScanSurveyPoint(long Record, string PointName, decimal Easting, decimal Northing, decimal Height);
public sealed record ScanTrack(int TrackId, int Slot, string Name,
    decimal LeftStartE, decimal LeftStartN, decimal RightStartE, decimal RightStartN,
    decimal LeftEndE, decimal LeftEndN, decimal RightEndE, decimal RightEndN,
    int GaugeMillimetres = ScanEngine.BroadGaugeMillimetres, decimal PointSpacing = 3m);
public sealed record ScanProjectState(int ScanProjectId, long Revision, string ProjectName,
    string SurveyDescription, IReadOnlyList<ScanTrack> Tracks)
{
    // Reject cached edits after DBTrackScan has been recreated, even if IDs are reused.
    public Guid DatabaseIdentity { get; init; }
    public bool HasSurvey { get; init; }
}
#endregion

#region Scan Coordinate Validation
public static class ScanCoordinates
{
    // Keep full input precision until the gauge calculation or survey storage boundary.
    private const decimal MaximumCoordinate = 999999999999999.9999m;

    public static decimal Parse(string text, string label)
    {
        if (!decimal.TryParse(s: text, style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture, result: out decimal value) ||
            value < -MaximumCoordinate || value > MaximumCoordinate)
        {
            throw new FormatException(message: $"{label}: enter a finite coordinate in metres using a decimal point and at most fifteen integer digits.");
        }
        return value;
    }

    public static void ValidateTrack(ScanTrack track, bool roundedCoordinates = false)
    {
        ArgumentNullException.ThrowIfNull(argument: track);
        ScanEngine.ValidateGauge(gaugeMillimetres: track.GaugeMillimetres);
        if (string.IsNullOrWhiteSpace(value: track.Name) || track.Name.Trim().Length > 200)
            throw new ArgumentException(message: "Enter a track name of 1 to 200 characters.");
        if (track.LeftStartE == track.RightStartE && track.LeftStartN == track.RightStartN)
            throw new ArgumentException(message: "Secondary and Primary start coordinates must be different.");
        if (track.LeftEndE == track.RightEndE && track.LeftEndN == track.RightEndN)
            throw new ArgumentException(message: "Secondary and Primary end coordinates must be different.");
        if (track.LeftStartE == track.LeftEndE && track.LeftStartN == track.LeftEndN)
            throw new ArgumentException(message: "The Secondary rail start and end must be different.");
        if (track.RightStartE == track.RightEndE && track.RightStartN == track.RightEndN)
            throw new ArgumentException(message: "The Primary rail start and end must be different.");
        ScanEngine.ValidateRailDefinition(track: track, roundedCoordinates: roundedCoordinates);
    }

    public static string Display(decimal value) => decimal.Round(d: value, decimals: 3,
        mode: MidpointRounding.AwayFromZero).ToString(format: "F3", provider: CultureInfo.InvariantCulture);
}
#endregion

#region Validated Reference Survey Snapshot
public sealed class ScanSurveySnapshot : IDisposable
{
    public const int BulkBatchSize = 5000;
    private readonly string _snapshotPath;
    public string SourceName { get; }
    public bool IncludesHeader { get; }
    public string Sha256 { get; private set; } = string.Empty;
    public long ValidCount { get; private set; }
    public long InvalidCount { get; private set; }
    public IReadOnlyList<ScanSurveyPreview> Preview => _preview;
    public IReadOnlyList<string> Errors => _errors;
    private readonly List<ScanSurveyPreview> _preview = new();
    private readonly List<string> _errors = new();

    private ScanSurveySnapshot(string snapshotPath, string sourceName, bool includesHeader)
    {
        _snapshotPath = snapshotPath;
        SourceName = sourceName;
        IncludesHeader = includesHeader;
    }

    public static ScanSurveySnapshot Create(string sourcePath, bool includesHeader)
    {
        string snapshotPath = Path.Combine(path1: Path.GetTempPath(), path2: $"GNA_Scan_{Guid.NewGuid():N}.csv");
        ScanSurveySnapshot snapshot = new(snapshotPath: snapshotPath,
            sourceName: Path.GetFileName(path: sourcePath), includesHeader: includesHeader);
        try
        {
            // Copy under a read lock: preview and import always use the same immutable input.
            using (FileStream source = new(path: sourcePath, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read))
            using (FileStream target = new(path: snapshotPath, mode: FileMode.CreateNew, access: FileAccess.Write, share: FileShare.None))
                source.CopyTo(destination: target);
            using (FileStream stream = File.OpenRead(path: snapshotPath))
                snapshot.Sha256 = Convert.ToHexString(inArray: SHA256.HashData(source: stream));
            snapshot.Validate();
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private TextFieldParser OpenParser()
    {
        TextFieldParser parser = new(path: _snapshotPath, defaultEncoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncoding: true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true
        };
        parser.SetDelimiters(delimiters: new[] { "," });
        return parser;
    }

    private void Validate()
    {
        using TextFieldParser parser = OpenParser();
        if (IncludesHeader && !parser.EndOfData)
        {
            string[] header = parser.ReadFields() ?? throw new InvalidDataException(message: "The header cannot be read.");
            if (header.Length != 4)
                throw new InvalidDataException(message: "The header must contain four comma-separated fields.");
        }
        long record = 0;
        while (!parser.EndOfData)
        {
            record++;
            string[] fields = Array.Empty<string>();
            string status = "Valid";
            try
            {
                fields = parser.ReadFields() ?? throw new FormatException(message: "Empty record.");
                ScanSurveyPoint point = ParsePoint(fields: fields, record: record);
                fields[1] = ScanCoordinates.Display(value: point.Easting);
                fields[2] = ScanCoordinates.Display(value: point.Northing);
                fields[3] = ScanCoordinates.Display(value: point.Height);
                ValidCount++;
            }
            catch (Exception ex) when (ex is FormatException or MalformedLineException)
            {
                status = ex.Message;
                InvalidCount++;
                if (_errors.Count < 20) _errors.Add(item: $"Record {record}: {status}");
            }
                _preview.Add(item: new ScanSurveyPreview(Record: record,
                    PointName: Field(fields: fields, index: 0), Easting: Field(fields: fields, index: 1),
                    Northing: Field(fields: fields, index: 2), Height: Field(fields: fields, index: 3), Status: status));
        }
        if (record == 0) throw new InvalidDataException(message: "The file contains no survey records.");
    }

    private static string Field(string[] fields, int index) => index < fields.Length ? fields[index] : string.Empty;

    private static ScanSurveyPoint ParsePoint(string[] fields, long record)
    {
        if (fields.Length != 4) throw new FormatException(message: "Expected four fields: point_name,E,N,Railhead height.");
        if (fields[0].Length > 256) throw new FormatException(message: "Point name exceeds 256 characters.");
        return new ScanSurveyPoint(Record: record, PointName: fields[0],
            Easting: ScanEngine.RoundCoordinate(value: ScanCoordinates.Parse(text: fields[1], label: "Easting")),
            Northing: ScanEngine.RoundCoordinate(value: ScanCoordinates.Parse(text: fields[2], label: "Northing")),
            Height: ScanEngine.RoundCoordinate(value: ScanCoordinates.Parse(text: fields[3], label: "Railhead height")));
    }

    public IEnumerable<ScanSurveyPoint> ReadPoints()
    {
        if (InvalidCount != 0 || ValidCount == 0)
            throw new InvalidOperationException(message: "Correct the CSV errors and preview again before importing.");
        using FileStream guard = new(path: _snapshotPath, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read);
        if (!string.Equals(a: Sha256, b: Convert.ToHexString(inArray: SHA256.HashData(source: guard)), comparisonType: StringComparison.Ordinal))
            throw new InvalidDataException(message: "The preview snapshot has changed. Select the CSV again.");
        using TextFieldParser parser = OpenParser();
        if (IncludesHeader) _ = parser.ReadFields();
        long record = 0;
        while (!parser.EndOfData)
        {
            record++;
            yield return ParsePoint(fields: parser.ReadFields() ?? throw new InvalidDataException(message: "Unable to read survey record."), record: record);
        }
        if (record != ValidCount) throw new InvalidDataException(message: "Survey record count changed after preview.");
    }

    public void Dispose()
    {
        try { File.Delete(path: _snapshotPath); }
        catch (IOException) { /* Temporary snapshot can be removed by operating-system cleanup. */ }
        catch (UnauthorizedAccessException) { /* Do not mask a completed import with a cleanup failure. */ }
    }
}
#endregion
