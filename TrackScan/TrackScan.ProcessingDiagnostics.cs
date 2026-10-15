#region Temporary Track AB Secondary 025 Filtering Audit
using System.Globalization;
using System.Text;
namespace TrackScan.Core;

public static partial class TrackScan
{
    private sealed partial class ProcessingTarget
    {
        public ScanHeightHistogram? DiagnosticHistogram { get; }

        public string DescribeDecisions(string sourcePath, long total, long corridorCount)
        {
            ScanHeightHistogram original = DebugHistogram ?? DiagnosticHistogram ?? throw new InvalidOperationException(message: "No diagnostic distribution was collected.");
            StringBuilder text = new();
            void Line(string value) => text.AppendLine(value: value);
            string Height(double? value) => value.HasValue ? value.Value.ToString(format: "F6", provider: CultureInfo.InvariantCulture) + " m" : "unavailable";
            Line(value: $"FILTERING DECISIONS: {PointId}");
            Line(value: $"LAS: {sourcePath}");
            Line(value: $"Track: {_rail.TrackName}; rail: {_rail.Rail}; polygon ID: {_head.PolygonId}; run: {_rail.RunId}; batch: {_rail.BatchId}");
            Line(value: "Reference railhead height: " + Height(value: (double)_head.ReferenceHeight));
            Line(value: "Previously stored offset: " + Height(value: _head.ScanOffset));
            Line(value: $"Height filter: {_rail.HeightFilterMillimetres} mm; rejection count: {_surface.RejectionCount}; band width: 1 mm.");
            Line(value: "");
            Line(value: "1. SPATIAL SELECTION");
            Line(value: $"LAS records: {total:N0}; within this rail corridor: {corridorCount:N0}; outside this corridor: {total - corridorCount:N0}.");
            Line(value: $"Within corridor but outside this railhead polygon: {corridorCount - original.Count:N0}; inside both: {original.Count:N0}.");
            Line(value: "Decoded E/N = LAS integer * header scale + header offset. One-metre index cells shortlist candidates; saved corridor containment (0.00015 m allowance), then saved railhead polygon containment (zero requested tolerance) decide membership. Scan footprint is not used.");
            Line(value: "Railhead polygon vertices (E, N):");
            foreach (var vertex in _head.Polygon.Vertices)
                Line(value: string.Format(provider: CultureInfo.InvariantCulture, format: "  {0:F4}, {1:F4}", arg0: vertex.Easting, arg1: vertex.Northing));
            Line(value: "");
            Line(value: "2. REFERENCE HEIGHT CHECK");
            Line(value: $"Reject absolute difference from reference > 1.000 m + 0.000000001 m arithmetic allowance. Rejected: {_surface.ReferenceRejectedCount:N0}; eligible: {_surface.Count - _surface.ReferenceRejectedCount:N0}.");
            Line(value: "3. INITIAL BAND REJECTION AND Hmax");
            Line(value: "Band = floor(height / 0.001 + 0.00000001). Bands are lower-inclusive, upper-exclusive. Reject bands with eligible count LESS THAN rejection count; equality passes.");
            Line(value: "Require at least 3 adjacent 1 mm bands, each meeting the selected rejection count after the reference check. Missing or sparse bands break continuity. Select the highest qualifying group; lower groups are excluded. This check precedes the height window; the final subset need not span 3 bands.");
            Line(value: "Hmax = highest actual point in the selected qualifying group: " + Height(value: _surface.Maximum));
            Line(value: "4. HEIGHT WINDOW");
            Line(value: _surface.Maximum.HasValue ? "Keep supported points from " + Height(value: _lower) + " through " + Height(value: _upper) + " inclusive (1e-9 m arithmetic allowance)." : "No supported maximum: no window or final mean can be formed.");
            Line(value: "5. FINAL BAND REJECTION");
            Line(value: "Recount each band inside the height window; reject bands with fewer than the rejection count. A partially clipped lower band can therefore fail after initially passing.");
            Line(value: "");
            Line(value: "FINAL BAND SELECTION — after all rejection stages");
            Line(value: "Accepted = retained points from this band contributed to the final mean. Rejected = no points from this band contributed. In a partially retained band, only the Retained count was used.");
            Line(value: "Decision | Lower m | Upper m | Reason | Original | Reference rejected | Eligible | Initial sparse rejected | Continuity rejected | Lower group rejected | Window rejected | In window | Final sparse rejected | Retained");
            long referenceTotal = 0, initialTotal = 0, continuityTotal = 0, groupTotal = 0, windowTotal = 0, finalTotal = 0, retainedTotal = 0;
            foreach (var band in original.Bands)
            {
                _surface.Bands.TryGetValue(key: band.Key, value: out var eligible);
                _finalBands.TryGetValue(key: band.Key, value: out long insideWindow);
                long reference = band.Value - eligible.Count;
                long initial = eligible.Count < _surface.RejectionCount ? eligible.Count : 0;
                long continuity = initial == 0 && !_surface.HasContinuity(band: band.Key) ? eligible.Count : 0;
                long group = initial == 0 && continuity == 0 && !_surface.IsSelectedBand(band: band.Key) ? eligible.Count : 0;
                long window = eligible.Count - initial - continuity - group - insideWindow;
                long final = insideWindow < _surface.RejectionCount ? insideWindow : 0;
                long kept = insideWindow - final;
                referenceTotal += reference; initialTotal += initial; continuityTotal += continuity; groupTotal += group; windowTotal += window; finalTotal += final; retainedTotal += kept;
                List<string> reasons = new();
                if (kept > 0) reasons.Add(item: "Passed continuity, selected group, window and final count; retained points used in mean");
                if (reference > 0) reasons.Add(item: $"{reference} outside reference +/-1 m");
                if (initial > 0) reasons.Add(item: $"{initial} in band below rejection count {_surface.RejectionCount}");
                if (continuity > 0) reasons.Add(item: $"{continuity} lack 3 adjacent qualifying bands");
                if (group > 0) reasons.Add(item: $"{group} in lower qualifying group");
                if (window > 0) reasons.Add(item: $"{window} outside Hmax height window");
                if (final > 0) reasons.Add(item: $"{final} in final band below rejection count {_surface.RejectionCount}");
                Line(value: string.Format(provider: CultureInfo.InvariantCulture,
                    format: "{10,-8} | {0:F3} | {1:F3} | {13} | {2} | {3} | {4} | {5} | {11} | {12} | {6} | {7} | {8} | {9}",
                    args: new object[] { band.Key * 0.001d, (band.Key + 1d) * 0.001d, band.Value, reference, eligible.Count, initial, window, insideWindow, final, kept, kept > 0 ? "Accepted" : "Rejected", continuity, group, string.Join(separator: "; ", values: reasons) }));
            }
            Line(value: $"Totals: reference rejected {referenceTotal}; initial sparse rejected {initialTotal}; continuity rejected {continuityTotal}; lower group rejected {groupTotal}; window rejected {windowTotal}; final sparse rejected {finalTotal}; retained {retainedTotal}.");
            Line(value: $"Reconciliation: {referenceTotal + initialTotal + continuityTotal + groupTotal + windowTotal + finalTotal + retainedTotal} = {original.Count} original points; retained {retainedTotal} = {_accepted} computed points.");
            Line(value: "");
            Line(value: "6. RESULT (additional decimals expose boundary decisions)");
            Line(value: $"Retained: {_accepted:N0}; rejected: {_rejected:N0}.");
            Line(value: "Mean uncorrected scan height: " + Height(value: _accepted > 0 ? _mean : null));
            Line(value: "Retained minimum: " + Height(value: _accepted > 0 ? _minimum : null) + "; maximum: " + Height(value: _accepted > 0 ? _maximum : null));
            Line(value: "SE = sqrt(sample variance / retained count): " + Height(value: Error));
            Line(value: "Computed offset = reference height - mean uncorrected scan height: " + Height(value: _accepted > 0 ? (double)_head.ReferenceHeight - _mean : null));
            Line(value: Warning is null ? "Result available for calibration review; not yet adopted." : "Warning: " + Warning + " No new offset; earlier offset retained where available.");
            return text.ToString();
        }
    }
}
#endregion
