#region Scan Outer Boundary CSV
using System.Globalization;
using System.IO;
using System.Text;
namespace TrackScan.Core;

public static partial class TrackScan
{
    public static void ExportCoverageBoundaryCsv(string path, ScanCoverageResult result)
    {
        ArgumentNullException.ThrowIfNull(argument: result);
        string destination = Path.GetFullPath(path: path);
        string directory = Path.GetDirectoryName(path: destination) ?? throw new IOException(message: "Select a destination folder.");
        string temporary = Path.Combine(path1: directory, path2: ".ScanBoundary-" + Guid.NewGuid().ToString(format: "N") + ".tmp");
        try
        {
            using (StreamWriter writer = new(path: temporary, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine(value: "Identifier,Ring,Vertex,E,N");
                foreach (var ring in result.Rings)
                {
                    if (ring.Count < 3) continue;
                    // The traced footprint keeps occupied cells to the left: outer rings are anticlockwise.
                    // Translate to a local origin to avoid cancellation with large survey coordinates.
                    ScanFenceVertex origin = ring[0];
                    double twiceArea = 0;
                    for (int i = 0; i < ring.Count; i++)
                    {
                        ScanFenceVertex a = ring[i], b = ring[(i + 1) % ring.Count];
                        twiceArea += (a.Easting - origin.Easting) * (b.Northing - origin.Northing) -
                            (b.Easting - origin.Easting) * (a.Northing - origin.Northing);
                    }
                    if (twiceArea <= 0) continue; // Interior holes are not outer boundary polygons.
                    for (int i = 0; i <= ring.Count; i++)
                    {
                        ScanFenceVertex vertex = ring[i % ring.Count];
                        writer.WriteLine(value: string.Format(provider: CultureInfo.InvariantCulture,
                            format: "ScanBoundary_R{0:D3}_V{1:D4},{0},{1},{2:F4},{3:F4}",
                            args: new object[] { origin.Ring, i + 1, vertex.Easting, vertex.Northing }));
                    }
                }
            }
            File.Move(sourceFileName: temporary, destFileName: destination, overwrite: true);
        }
        finally { if (File.Exists(path: temporary)) File.Delete(path: temporary); }
    }
}
#endregion
