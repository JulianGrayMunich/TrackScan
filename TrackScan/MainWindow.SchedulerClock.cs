#region Compact Scheduler Clock And Time Conversion Preview
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
namespace TrackScan;
public partial class MainWindow
{
    private bool _schedulerClockSynchronizing;
    private void SchedulerClockTime_Changed(object? sender, EventArgs e)
    {
        if (_schedulerClockSynchronizing || txtSchedulerStartTime is null || SchedulerAm is null || SchedulerPm is null) return;
        _schedulerClockSynchronizing = true;
        try
        {
            txtSchedulerStartTime.Text = SchedulerClock.Time.ToString(format: "HH:mm", provider: CultureInfo.InvariantCulture);
            SchedulerAm.IsChecked = SchedulerClock.Time.Hour < 12; SchedulerPm.IsChecked = SchedulerClock.Time.Hour >= 12;
        }
        finally { _schedulerClockSynchronizing = false; }
        UpdateSchedulerTimePreview();
    }
    private void SchedulerTimeText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_schedulerClockSynchronizing || SchedulerClock is null || SchedulerAm is null || SchedulerPm is null) return;
        if (TimeOnly.TryParseExact(s: txtSchedulerStartTime.Text.Trim(), format: "HH:mm", provider: CultureInfo.InvariantCulture,
            style: DateTimeStyles.None, result: out TimeOnly time))
        {
            _schedulerClockSynchronizing = true;
            try { SchedulerClock.Time = time; SchedulerAm.IsChecked = time.Hour < 12; SchedulerPm.IsChecked = time.Hour >= 12; }
            finally { _schedulerClockSynchronizing = false; }
        }
        UpdateSchedulerTimePreview();
    }
    private void SchedulerPeriod_Changed(object sender, RoutedEventArgs e)
    {
        if (_schedulerClockSynchronizing || SchedulerClock is null || SchedulerPm is null) return;
        SchedulerClock.Time = new TimeOnly(hour: SchedulerClock.Time.Hour % 12 + (SchedulerPm.IsChecked == true ? 12 : 0), minute: SchedulerClock.Time.Minute);
    }
    private void SchedulerDate_Changed(object sender, SelectionChangedEventArgs e) => UpdateSchedulerTimePreview();
    private void SchedulerServerZone_Changed(object sender, SelectionChangedEventArgs e) => UpdateSchedulerTimePreview();
    private void UpdateSchedulerTimePreview()
    {
        if (txtSchedulerTimeConversion is null || txtSchedulerStartTime is null || dpSchedulerStart is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(value: _schedulerZone)) { txtSchedulerTimeConversion.Text = "Select a project to display its time zone and UTC start time."; return; }
            if (!dpSchedulerStart.SelectedDate.HasValue || !TimeOnly.TryParseExact(s: txtSchedulerStartTime.Text.Trim(), format: "HH:mm",
                provider: CultureInfo.InvariantCulture, style: DateTimeStyles.None, result: out TimeOnly time))
            { txtSchedulerTimeConversion.Text = $"Project time zone: {_schedulerZone}. Enter a start date and a 24-hour time (HH:mm)."; return; }
            DateTime local = dpSchedulerStart.SelectedDate.Value.Date + time.ToTimeSpan();
            DateTime utc = ScanSchedulerSettings.LocalStartToUtc(local: local, zoneId: _schedulerZone);
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id: _schedulerZone);
            double hours = zone.GetUtcOffset(dateTime: DateTime.SpecifyKind(value: local, kind: DateTimeKind.Unspecified)).TotalHours;
            TimeZoneInfo? serverZone = cmbSchedulerServerZone?.SelectedItem as TimeZoneInfo;
            string server = serverZone is null ? "Select the server time zone." : $" Server: {TimeZoneInfo.ConvertTimeFromUtc(dateTime: utc, destinationTimeZone: serverZone):yyyy-MM-dd HH:mm} ({serverZone.DisplayName}).";
            txtSchedulerTimeConversion.Text = FormattableString.Invariant(formattable: $"Project time zone: {_schedulerZone}. Start {local:yyyy-MM-dd HH:mm} local = {utc:yyyy-MM-dd HH:mm} UTC. UTC offset {hours:+0.##;-0.##;0} hours; daylight saving {(zone.IsDaylightSavingTime(dateTime: local) ? "active" : "inactive")}.") + server;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { txtSchedulerTimeConversion.Text = $"Project time zone: {_schedulerZone}. {ex.Message}"; }
    }
}
#endregion
