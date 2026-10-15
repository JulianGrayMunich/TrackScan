using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.Globalization;
using System.IO;
using System.Text;
#endregion
namespace TrackScan;

internal static class ScanPolygonCsv
{
    #region Four Column Polygon Checking Export
    public static void Export(string path, ScanPolygonSet set)
    {
        string fullPath = Path.GetFullPath(path: path);
        string folder = Path.GetDirectoryName(path: fullPath) ?? throw new IOException(message: "Select a CSV folder.");
        string temporary = Path.Combine(path1: folder, path2: $".polygons-{Guid.NewGuid():N}.tmp");
        try
        {
            using (StreamWriter writer = new(path: temporary, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine(value: "Identifier,E,N,Ht");
                foreach (ScanPolygon polygon in set.Polygons)
                    foreach (ScanPolygonVertex v in polygon.Vertices)
                        writer.WriteLine(value: "\"" + v.Identifier.Replace(oldValue: "\"", newValue: "\"\"") + "\"," +
                            v.Easting.ToString(format: "F4", provider: CultureInfo.InvariantCulture) + "," +
                            v.Northing.ToString(format: "F4", provider: CultureInfo.InvariantCulture) + "," +
                            v.Height.ToString(format: "F4", provider: CultureInfo.InvariantCulture));
            }
            File.Move(sourceFileName: temporary, destFileName: fullPath, overwrite: true);
        }
        finally { if (File.Exists(path: temporary)) File.Delete(path: temporary); }
    }
    #endregion
}
