#region Validated Scan Polygon Inclusion
using System.IO;
namespace TrackScan.Core;
public static partial class TrackScan
{
    internal sealed class PreparedBoundary
    {
        private readonly double[] _e, _n;
        private readonly double _originE, _originN;
        public double MinE { get; }
        public double MaxE { get; }
        public double MinN { get; }
        public double MaxN { get; }

        public PreparedBoundary(ScanPolygon polygon)
        {
            List<ScanPolygonVertex> vertices = new();
            int previousSequence = 0;
            foreach (ScanPolygonVertex vertex in polygon.Vertices)
            {
                if (vertex.Sequence <= previousSequence)
                    throw new InvalidDataException(message: $"Polygon '{polygon.Identifier}' has unordered or duplicate vertex sequence numbers. Recompute its polygons.");
                previousSequence = vertex.Sequence;
                if (vertices.Count == 0 || vertex.Easting != vertices[^1].Easting || vertex.Northing != vertices[^1].Northing)
                    vertices.Add(item: vertex);
            }
            if (vertices.Count > 1 && vertices[0].Easting == vertices[^1].Easting && vertices[0].Northing == vertices[^1].Northing)
                vertices.RemoveAt(index: vertices.Count - 1);
            if (vertices.Count < 3) throw new InvalidDataException(message: $"Polygon '{polygon.Identifier}' is incomplete.");
            _originE = (double)vertices[0].Easting; _originN = (double)vertices[0].Northing;
            _e = new double[vertices.Count]; _n = new double[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                // Subtract in decimal before converting: preserve the stored four-decimal geometry.
                _e[i] = (double)(vertices[i].Easting - vertices[0].Easting);
                _n[i] = (double)(vertices[i].Northing - vertices[0].Northing);
            }
            MinE = _originE + _e.Min(); MaxE = _originE + _e.Max();
            MinN = _originN + _n.Min(); MaxN = _originN + _n.Max();
            if (Math.Max(val1: Math.Abs(value: MinE), val2: Math.Abs(value: MaxE)) > 1e9 ||
                Math.Max(val1: Math.Abs(value: MinN), val2: Math.Abs(value: MaxN)) > 1e9)
                throw new InvalidDataException(message: "Saved polygon coordinates exceed the supported metre-coordinate range.");
            double area = 0;
            for (int i = 0; i < _e.Length; i++)
            {
                int next = (i + 1) % _e.Length;
                area += _e[i] * _n[next] - _e[next] * _n[i];
                for (int j = i + 1; j < _e.Length; j++)
                {
                    int after = (j + 1) % _e.Length;
                    if (j == next || after == i) continue;
                    if (SegmentsMeet(ax: _e[i], ay: _n[i], bx: _e[next], by: _n[next], cx: _e[j], cy: _n[j], dx: _e[after], dy: _n[after]))
                        throw new InvalidDataException(message: $"Polygon '{polygon.Identifier}' crosses or touches itself. Recompute its polygons.");
                }
            }
            if (Math.Abs(value: area) < 1e-12) throw new InvalidDataException(message: $"Polygon '{polygon.Identifier}' has no usable area.");
        }

        public bool Contains(double e, double n, double tolerance)
        {
            tolerance = Math.Max(val1: tolerance, val2: BoundaryArithmeticAllowance);
            if (!double.IsFinite(d: e) || !double.IsFinite(d: n) ||
                e < MinE - tolerance || e > MaxE + tolerance || n < MinN - tolerance || n > MaxN + tolerance) return false;
            double x = e - _originE, y = n - _originN;
            bool inside = false;
            for (int i = 0, j = _e.Length - 1; i < _e.Length; j = i++)
            {
                double dx = _e[i] - _e[j], dy = _n[i] - _n[j];
                double px = x - _e[j], py = y - _n[j];
                if (x >= Math.Min(val1: _e[i], val2: _e[j]) - tolerance && x <= Math.Max(val1: _e[i], val2: _e[j]) + tolerance &&
                    y >= Math.Min(val1: _n[i], val2: _n[j]) - tolerance && y <= Math.Max(val1: _n[i], val2: _n[j]) + tolerance)
                {
                    double t = Math.Clamp(value: (px * dx + py * dy) / (dx * dx + dy * dy), min: 0d, max: 1d);
                    double distanceE = px - t * dx, distanceN = py - t * dy;
                    if (distanceE * distanceE + distanceN * distanceN <= tolerance * tolerance) return true;
                }
                // A half-open straddle counts a shared vertex once; horizontal edges do not cross the ray.
                if ((_n[i] > y) != (_n[j] > y) && px < py * dx / dy) inside = !inside;
            }
            return inside;
        }

        public bool IntersectsCell(long e, long n, double tolerance)
        {
            // Conservative indexing only: it may nominate an outside point, but must not omit an inside point.
            double left = e - tolerance, right = e + 1d + tolerance, bottom = n - tolerance, top = n + 1d + tolerance;
            if (Contains(e: left, n: bottom, tolerance: 0d) || Contains(e: right, n: bottom, tolerance: 0d) ||
                Contains(e: right, n: top, tolerance: 0d) || Contains(e: left, n: top, tolerance: 0d)) return true;
            left -= _originE; right -= _originE; bottom -= _originN; top -= _originN;
            for (int i = 0, j = _e.Length - 1; i < _e.Length; j = i++)
            {
                double lo = 0d, hi = 1d;
                if (ClipAxis(start: _e[j], delta: _e[i] - _e[j], min: left, max: right, lo: ref lo, hi: ref hi) &&
                    ClipAxis(start: _n[j], delta: _n[i] - _n[j], min: bottom, max: top, lo: ref lo, hi: ref hi)) return true;
            }
            return false;
        }

        private static bool ClipAxis(double start, double delta, double min, double max, ref double lo, ref double hi)
        {
            if (delta == 0d) return start >= min && start <= max;
            double a = (min - start) / delta, b = (max - start) / delta;
            lo = Math.Max(val1: lo, val2: Math.Min(val1: a, val2: b));
            hi = Math.Min(val1: hi, val2: Math.Max(val1: a, val2: b));
            return lo <= hi;
        }

        private static bool SegmentsMeet(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            if (Math.Max(val1: ax, val2: bx) < Math.Min(val1: cx, val2: dx) || Math.Max(val1: cx, val2: dx) < Math.Min(val1: ax, val2: bx) ||
                Math.Max(val1: ay, val2: by) < Math.Min(val1: cy, val2: dy) || Math.Max(val1: cy, val2: dy) < Math.Min(val1: ay, val2: by)) return false;
            double a = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax), b = (bx - ax) * (dy - ay) - (by - ay) * (dx - ax);
            double c = (dx - cx) * (ay - cy) - (dy - cy) * (ax - cx), d = (dx - cx) * (by - cy) - (dy - cy) * (bx - cx);
            return (a == 0 || b == 0 || Math.Sign(value: a) != Math.Sign(value: b)) &&
                (c == 0 || d == 0 || Math.Sign(value: c) != Math.Sign(value: d));
        }
    }
}
#endregion
