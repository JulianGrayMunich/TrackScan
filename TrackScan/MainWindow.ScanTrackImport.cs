using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region System Preparation
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
#endregion

namespace TrackScan;

public partial class MainWindow
{
    #region Import Track Endpoint Coordinates Into The Editor
    private void btnScanImportEndpoints_Click(object sender, RoutedEventArgs e)
    {
        if (_scanBusy || _scanState is null || _scanGeometryProject != _activeProjectId) return;
        OpenFileDialog dialog = new()
        {
            Title = "Import track endpoints: Primary Start, Primary End, Secondary Start, Secondary End",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(owner: this) != true) return;
        try
        {
            _ = CurrentScanRepository();
            ScanTrackEndpoints endpoints = ScanTrackCsv.Read(path: dialog.FileName);
            ApplyScanEndpointImport(endpoints: endpoints);
            txtScanStatus.Text = $"Endpoint coordinates imported from '{Path.GetFileName(path: dialog.FileName)}'. Review the coordinates, then Save track. Point names and Ht were ignored.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidOperationException)
        {
            txtScanStatus.Text = $"Endpoint CSV import failed: {ex.Message}";
            MessageBox.Show(owner: this, messageBoxText: ex.Message, caption: "Import track coordinates", button: MessageBoxButton.OK, icon: MessageBoxImage.Warning);
        }
    }

    private void ApplyScanEndpointImport(ScanTrackEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(argument: endpoints);
        _scanFillingEditor = true;
        try
        {
            SetScanImportedCoordinate(box: txtScanRightStartE, value: endpoints.RightStart.Easting);
            SetScanImportedCoordinate(box: txtScanRightStartN, value: endpoints.RightStart.Northing);
            SetScanImportedCoordinate(box: txtScanRightEndE, value: endpoints.RightEnd.Easting);
            SetScanImportedCoordinate(box: txtScanRightEndN, value: endpoints.RightEnd.Northing);
            SetScanImportedCoordinate(box: txtScanLeftStartE, value: endpoints.LeftStart.Easting);
            SetScanImportedCoordinate(box: txtScanLeftStartN, value: endpoints.LeftStart.Northing);
            SetScanImportedCoordinate(box: txtScanLeftEndE, value: endpoints.LeftEnd.Easting);
            SetScanImportedCoordinate(box: txtScanLeftEndN, value: endpoints.LeftEnd.Northing);
        }
        finally { _scanFillingEditor = false; }
        _scanEditorDirty = true;
    }

    private static void SetScanImportedCoordinate(TextBox box, decimal value)
    {
        box.Tag = value;
        box.Text = ScanCoordinates.Display(value: value);
    }
    #endregion
}
