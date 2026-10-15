#region Coordinated Chart And Details Windows
using System.Windows;
namespace TrackScan;

public static class ScanChartReviewPair
{
    public static async Task<bool> ShowAsync(Window owner, ScanHeightHistogramWindow chart,
        ScanDecisionWindow details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument: owner);
        ArgumentNullException.ThrowIfNull(argument: chart);
        ArgumentNullException.ThrowIfNull(argument: details);
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<bool> completion = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        bool ownerEnabled = owner.IsEnabled;
        void Decide(bool proceed) => completion.TrySetResult(result: proceed);
        void Closed(object? sender, EventArgs e) => Decide(proceed: false);
        chart.IsPairedReview = true;
        chart.Owner = owner; details.Owner = owner;
        chart.ReviewDecision += Decide;
        chart.Closed += Closed; details.Closed += Closed;
        Rect area = SystemParameters.WorkArea;
        chart.WindowStartupLocation = WindowStartupLocation.Manual;
        details.WindowStartupLocation = WindowStartupLocation.Manual;
        chart.Left = area.Left; chart.Top = area.Top;
        details.Left = Math.Max(val1: area.Left, val2: area.Right - details.Width);
        details.Top = area.Top;
        details.Height = Math.Min(val1: details.Height, val2: area.Height);
        // Both windows are modeless so the details remain scrollable and selectable.
        // Disable only the owner while this pair is being reviewed.
        owner.IsEnabled = false;
        using CancellationTokenRegistration registration = cancellationToken.Register(callback: () =>
            completion.TrySetResult(result: false));
        try
        {
            details.Show(); chart.Show();
            return await completion.Task;
        }
        finally
        {
            chart.ReviewDecision -= Decide;
            chart.Closed -= Closed; details.Closed -= Closed;
            if (chart.IsVisible) chart.Close();
            if (details.IsVisible) details.Close();
            owner.IsEnabled = ownerEnabled;
        }
    }
}
#endregion
