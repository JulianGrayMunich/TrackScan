#region Main Window
using System.IO;
using System.Windows;

namespace TrackScan;

public partial class MainWindow : Window
{
    #region Application Footer
    private const string ApplicationRevision = "029";

    public string CopyrightText =>
        $"© GNA Software {GetBuildDate():yyyyMMdd} Revision {ApplicationRevision}";

    private static DateTime GetBuildDate()
    {
        string assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(value: assemblyLocation))
        {
            return DateTime.Today;
        }

        return File.GetLastWriteTime(path: assemblyLocation).Date;
    }
    #endregion

    #region Construction
    public MainWindow()
    {
        InitializeComponent();
        InitialiseConnectionPage();
        InitialiseScanPreparation();
        Loaded += StartupConnection_Loaded;
    }
    #endregion
}
#endregion
