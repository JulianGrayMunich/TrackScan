using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;
#endregion

namespace TrackScan;

#region Track Endpoint CSV Import
public sealed record ScanTrackEndpoints(ScanEngine.RailCoordinate RightStart, ScanEngine.RailCoordinate RightEnd,
    ScanEngine.RailCoordinate LeftStart, ScanEngine.RailCoordinate LeftEnd);

public static class ScanTrackCsv
{
    private const int RequiredRows = 4;
    private const int RequiredFields = 4;

    /// <summary>
    /// Reads four data rows without a header: point name,E,N,Ht.
    /// Point names and heights are ignored. All rows are validated before returning;
    /// coordinate precision is retained for the existing gauge calculation on Save.
    /// </summary>
    public static ScanTrackEndpoints Read(string path)
    {
        using FileStream stream = new(path: path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read);
        using TextFieldParser parser = new(stream: stream,
            defaultEncoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncoding: true)
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(delimiters: new[] { "," });
        ScanEngine.RailCoordinate[] coordinates = new ScanEngine.RailCoordinate[RequiredRows];
        int row = 0;
        while (!parser.EndOfData)
        {
            if (row == RequiredRows)
                throw new FormatException(message: "The endpoint CSV must contain exactly four data rows: Primary Start, Primary End, Secondary Start, Secondary End. No header row.");
            string[] fields;
            try { fields = parser.ReadFields() ?? throw new FormatException(message: $"CSV row {row + 1} cannot be read."); }
            catch (MalformedLineException ex)
            { throw new FormatException(message: $"CSV row {row + 1} contains invalid CSV quoting.", innerException: ex); }
            if (fields.Length != RequiredFields)
                throw new FormatException(message: $"CSV row {row + 1}: expected four fields: point name, E, N, Ht. No header row.");
            coordinates[row] = new ScanEngine.RailCoordinate(
                Easting: ScanCoordinates.Parse(text: fields[1], label: $"CSV row {row + 1} Easting (no header row)"),
                Northing: ScanCoordinates.Parse(text: fields[2], label: $"CSV row {row + 1} Northing (no header row)"));
            row++;
        }
        if (row != RequiredRows)
            throw new FormatException(message: "The endpoint CSV must contain exactly four data rows: Primary Start, Primary End, Secondary Start, Secondary End. No header row.");
        return new ScanTrackEndpoints(RightStart: coordinates[0], RightEnd: coordinates[1], LeftStart: coordinates[2], LeftEnd: coordinates[3]);
    }
}
#endregion
