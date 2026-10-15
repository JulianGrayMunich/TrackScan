using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Stored Offset CSV Export
using System.Globalization;
using System.IO;
using System.Text;
namespace TrackScan;

public static class ScanOffsetCsv
{
    public static void Export(string path, IReadOnlyList<ScanOffsetReading> offsets)
    {
        ArgumentNullException.ThrowIfNull(argument: offsets);
        string destination = Path.GetFullPath(path: path);
        string directory = Path.GetDirectoryName(path: destination) ?? throw new IOException(message: "Select a CSV destination.");
        string temporary = Path.Combine(path1: directory, path2: $".scan-offsets-{Guid.NewGuid():N}.tmp");
        try
        {
            using (StreamWriter writer = new(path: temporary, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine(value: "Railhead point,Uncorrected Scan Height (m),ToR height (m),ScanOffset (m),Points used,SE (m),Max (m),Min (m),Status");
                foreach (ScanOffsetReading row in offsets)
                    writer.WriteLine(value: string.Join(separator: ",", value: new[] {
                        Quote(text: row.PointId), Number(value: row.RawMean), row.ReferenceHeight.ToString(format: "F4", provider: CultureInfo.InvariantCulture),
                        Number(value: row.ScanOffset), row.ScanPointCount.ToString(provider: CultureInfo.InvariantCulture), Number(value: row.StandardError),
                        Number(value: row.MaximumHeight), Number(value: row.MinimumHeight), Quote(text: row.Status) }));
            }
            File.Move(sourceFileName: temporary, destFileName: destination, overwrite: true);
        }
        finally { if (File.Exists(path: temporary)) File.Delete(path: temporary); }
    }
    private static string Number(double? value) => value?.ToString(format: "F6", provider: CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Quote(string text) => "\"" + text.Replace(oldValue: "\"", newValue: "\"\"") + "\"";
}
#endregion
