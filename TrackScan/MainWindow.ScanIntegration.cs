using global::TrackScan.Core;
using ScanEngine = global::TrackScan.Core.TrackScan;
#region Scan Host Integration
namespace TrackScan;

public partial class MainWindow
{
    private int? _activeProjectId => _activeProject?.Id;
    private string _activeProjectName => _activeProject?.Name ?? string.Empty;
    private string? _validatedDatabaseConnectionString => _validatedConnection;
    private bool DebugCsvEnabled => chkDebugging.IsChecked == true && chkScanExportCsv.IsChecked == true;
    private string GetValidatedDatabaseConnectionString() => _validatedConnection
        ?? throw new InvalidOperationException(message: "Test the database connection before continuing.");
}
#endregion
