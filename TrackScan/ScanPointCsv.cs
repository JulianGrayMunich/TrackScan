using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Globalization;
using System.IO;
using System.Text;
#endregion

namespace TrackScan;

#region Temporary Computed Point Checking Export
internal static class ScanPointCsv
{
    private static string Quote(string text) => "\"" + text.Replace(oldValue: "\"", newValue: "\"\"") + "\"";
    private static string Number(decimal value) => value.ToString(format: "F4", provider: CultureInfo.InvariantCulture);

    // Stage in the destination directory, then rename. A failed write does not
    // leave a truncated CSV or overwrite an existing checking file.
    public static void Export(string path, ScanComputationResult result)
    {
        ArgumentNullException.ThrowIfNull(argument: result);
        string fullPath = Path.GetFullPath(path: path);
        string directory = Path.GetDirectoryName(path: fullPath) ?? throw new IOException(message: "Select a CSV destination directory.");
        string temporary = Path.Combine(path1: directory, path2: $".scan-{Guid.NewGuid():N}.tmp");
        try
        {
            using (StreamWriter writer = new(path: temporary, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine(value: "point_name,E,N,Railhead_height,rail,sequence,distance_along_rail,chainage,survey_point_count,height_source");
                foreach (ScanComputedPoint point in result.Points)
                    writer.WriteLine(value: string.Join(separator: ",", value: new[]
                    {
                        Quote(text: point.PointName), Number(value: point.Easting), Number(value: point.Northing), Number(value: point.Height),
                        point.Rail == "R" ? "Primary" : "Secondary", point.Sequence.ToString(provider: CultureInfo.InvariantCulture), Number(value: point.DistanceAlongRail), string.Empty,
                        point.SurveyPointCount.ToString(provider: CultureInfo.InvariantCulture), point.HeightInterpolated ? "Interpolated" : "Nearest survey"
                    }));
            }
            File.Move(sourceFileName: temporary, destFileName: fullPath, overwrite: true);
        }
        finally { if (File.Exists(path: temporary)) File.Delete(path: temporary); }
    }
}
#endregion
