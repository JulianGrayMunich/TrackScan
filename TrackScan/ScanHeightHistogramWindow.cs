#region Combined Railhead Data Chart
using global::TrackScan.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
namespace TrackScan;

public sealed class ScanHeightHistogramWindow : Window
{
    #region Paired Review Navigation
    public bool IsPairedReview { get; set; }
    public event Action<bool>? ReviewDecision;
    private void CompleteReview(bool proceed)
    {
        if (IsPairedReview) ReviewDecision?.Invoke(obj: proceed);
        else DialogResult = proceed;
    }
    #endregion

    #region Chart Layout And Navigation
    public ScanHeightHistogramWindow(ScanHeightHistogram histogram, ScanHeightHistogram retained,
        double? adoptedMean, decimal referenceHeight, double? scanOffset, string fileName, int chartNumber, int chartCount, double? previousOffsetMean = null, long previousOffsetCount = 0)
    {
        ArgumentNullException.ThrowIfNull(argument: histogram);
        ArgumentNullException.ThrowIfNull(argument: retained);
        if (histogram.PointId != retained.PointId || histogram.BandWidthMetres != retained.BandWidthMetres)
            throw new ArgumentException(message: "The chart distributions must describe the same railhead point and bands.");
        foreach (var band in retained.Bands)
            if (!histogram.Bands.TryGetValue(key: band.Key, value: out long total) || band.Value > total)
                throw new ArgumentException(message: "Retained chart points exceed the original distribution.");
        Title = "Railhead scan data — " + histogram.PointId;
        Width = 950; Height = 500; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = Brushes.White; FontFamily = new FontFamily(familyName: "Arial"); FontSize = 12;
        DockPanel layout = new() { Margin = new Thickness(uniformLength: 12) }; Content = layout;
        TextBlock heading = new() { Text = $"{histogram.PointId} — {fileName}     |     Chart {chartNumber:N0} of {chartCount:N0}\nEach bar represents a 1 mm elevation band. Hover over a bar for its counts.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left: 0, top: 0, right: 0, bottom: 10) };
        DockPanel.SetDock(element: heading, dock: Dock.Top); layout.Children.Add(element: heading);
        WrapPanel legend = new() { Margin = new Thickness(left: 0, top: 0, right: 0, bottom: 8) };
        legend.Children.Add(element: new TextBlock { Text = $"■ Retained: {retained.Count:N0}   ", Foreground = Brushes.ForestGreen });
        legend.Children.Add(element: new TextBlock { Text = $"■ Rejected: {histogram.Count - retained.Count:N0}   ", Foreground = Brushes.Gray });
        legend.Children.Add(element: new TextBlock { Text = "Adopted subset mean: " + (adoptedMean.HasValue ? adoptedMean.Value.ToString(format: "F3", provider: CultureInfo.InvariantCulture) + " m" : "unavailable (no retained points)"), Foreground = Brushes.DarkBlue, FontWeight = FontWeights.Bold });
        DockPanel.SetDock(element: legend, dock: Dock.Top); layout.Children.Add(element: legend);
        TextBlock elevations = new() { Text = "Reference railhead elevation: " + referenceHeight.ToString(format: "F3", provider: CultureInfo.InvariantCulture)
            + " m     |     Computed scan offset: " + (adoptedMean.HasValue && retained.Count > 0 && scanOffset.HasValue
                ? scanOffset.Value.ToString(format: "F3", provider: CultureInfo.InvariantCulture) + " m"
                : "unavailable (no retained points)")
            + "     |     Previous offsets mean: " + (previousOffsetMean.HasValue
                ? previousOffsetMean.Value.ToString(format: "F3", provider: CultureInfo.InvariantCulture) + $" m (n={previousOffsetCount:N0})"
                : "no previous offsets"), TextWrapping = TextWrapping.Wrap,
            ToolTip = "Unweighted mean of valid offsets earlier in this operation, across selected tracks and files. Excludes the current point and previously stored offsets.", Margin = new Thickness(left: 0, top: 0, right: 0, bottom: 8) };
        DockPanel.SetDock(element: elevations, dock: Dock.Top); layout.Children.Add(element: elevations);
        DockPanel footer = new() { Margin = new Thickness(left: 0, top: 8, right: 0, bottom: 0) };
        Button next = new() { Content = "Next", Width = 100, Height = 30, IsDefault = true, Margin = new Thickness(left: 10, top: 0, right: 0, bottom: 0) };
        next.Click += (_, _) => CompleteReview(proceed: true);
        Button cancel = new() { Content = "Cancel", Width = 100, Height = 30, IsCancel = true };
        cancel.Click += (_, _) => CompleteReview(proceed: false);
        PreviewKeyDown += (_, e) =>
        {
            if (IsPairedReview && e.Key == System.Windows.Input.Key.Escape)
            { e.Handled = true; CompleteReview(proceed: false); }
        };
        DockPanel.SetDock(element: next, dock: Dock.Right); footer.Children.Add(element: next);
        DockPanel.SetDock(element: cancel, dock: Dock.Right); footer.Children.Add(element: cancel);
        footer.Children.Add(element: new TextBlock { Text = "Cancel stops calibration without saving offsets. Next advances.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(element: footer, dock: Dock.Bottom); layout.Children.Add(element: footer);
        if (histogram.Count == 0)
        {
            layout.Children.Add(element: new TextBlock { Text = "No scan points inside this railhead polygon. No subset mean is available.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            return;
        }
        layout.Children.Add(element: CreateChart(histogram: histogram, retained: retained, adoptedMean: adoptedMean));
    }
    #endregion

    #region One Millimetre Stacked Bars And Adopted Mean
    private static Viewbox CreateChart(ScanHeightHistogram histogram, ScanHeightHistogram retained, double? adoptedMean)
    {
        long first = histogram.Bands.Keys.Min(), last = histogram.Bands.Keys.Max();
        long maximum = histogram.Bands.Values.Max();
        double span = (double)last - first + 1;
        const double width = 760d;
        double countStep = Math.Max(val1: 1d, val2: Math.Ceiling(a: maximum / 4d));
        double axisMaximum = countStep * 4d;
        const double left = 65d, top = 25d, plotHeight = 220d;
        Canvas chart = new() { Width = width + 100, Height = 300, Background = Brushes.White };
        void Label(string text, double x, double y)
        {
            TextBlock label = new() { Text = text, FontSize = 12 };
            Canvas.SetLeft(element: label, length: x); Canvas.SetTop(element: label, length: y); chart.Children.Add(element: label);
        }
        Label(text: "Number of scan points", x: 0, y: 0);
        for (int tick = 0; tick <= 4; tick++)
        {
            double y = top + plotHeight * (1 - tick / 4d);
            chart.Children.Add(element: new Line { X1 = left, X2 = left + width, Y1 = y, Y2 = y, Stroke = Brushes.Gainsboro });
            Label(text: (countStep * tick).ToString(format: "N0", provider: CultureInfo.InvariantCulture), x: 0, y: y - 8);
        }
        foreach (var band in histogram.Bands)
        {
            retained.Bands.TryGetValue(key: band.Key, value: out long accepted);
            long rejected = band.Value - accepted;
            double acceptedHeight = plotHeight * accepted / axisMaximum;
            double rejectedHeight = plotHeight * rejected / axisMaximum;
            double x = left + ((double)band.Key - first) * width / span;
            double barWidth = width / span * 0.94d;
            string tooltip = string.Format(provider: CultureInfo.InvariantCulture,
                format: "{0:F3} to {1:F3} m (upper excluded)\nRetained: {2:N0}; rejected: {3:N0}; total: {4:N0}",
                args: new object[] { band.Key * histogram.BandWidthMetres, (band.Key + 1d) * histogram.BandWidthMetres, accepted, rejected, band.Value });
            void Bar(double height, double y, Brush fill)
            {
                if (height <= 0d) return;
                Rectangle bar = new() { Width = barWidth, Height = height, Fill = fill, ToolTip = tooltip };
                Canvas.SetLeft(element: bar, length: x); Canvas.SetTop(element: bar, length: y); chart.Children.Add(element: bar);
            }
            Bar(height: acceptedHeight, y: top + plotHeight - acceptedHeight, fill: Brushes.ForestGreen);
            Bar(height: rejectedHeight, y: top + plotHeight - acceptedHeight - rejectedHeight, fill: Brushes.LightGray);
        }
        if (adoptedMean.HasValue)
        {
            double x = left + (adoptedMean.Value / histogram.BandWidthMetres - first) * width / span;
            chart.Children.Add(element: new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + plotHeight,
                Stroke = Brushes.DarkBlue, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 2 },
                ToolTip = "Adopted subset mean: " + adoptedMean.Value.ToString(format: "F3", provider: CultureInfo.InvariantCulture) + " m" });
        }
        // Tick labels sit on exact millimetre boundaries rather than rounded fractional bands.
        double tickStep = Math.Max(val1: 1d, val2: Math.Ceiling(a: span / 5d));
        for (double bandOffset = 0d; bandOffset < span; bandOffset += tickStep)
            Label(text: ((first + bandOffset) * histogram.BandWidthMetres).ToString(format: "F3", provider: CultureInfo.InvariantCulture), x: left + width * bandOffset / span - 20, y: 252);
        Label(text: ((first + span) * histogram.BandWidthMetres).ToString(format: "F3", provider: CultureInfo.InvariantCulture), x: left + width - 20, y: 252);
        Label(text: "Elevation (m) — 1 mm bands", x: left + width / 2 - 80, y: 278);
        return new Viewbox { Child = chart, Stretch = Stretch.Uniform };
    }
    #endregion
}
#endregion
