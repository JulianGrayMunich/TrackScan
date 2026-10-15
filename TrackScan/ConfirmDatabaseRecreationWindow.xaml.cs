using System.Windows;

namespace TrackScan
{
    public partial class ConfirmDatabaseRecreationWindow : Window
    {
        #region Constructor

        public ConfirmDatabaseRecreationWindow(string databaseName = "DBTrackGeometry")
        {
            InitializeComponent();

            if (databaseName != "DBTrackGeometry" && databaseName != "DBTrackScan")
            {
                throw new System.ArgumentException(
                    message: "The recreation warning requires a supported database name.",
                    paramName: nameof(databaseName));
            }

            txtRecreationWarning.Text =
                $"Proceeding will permanently delete {databaseName} and all data contained within it. " +
                "The database and empty table structure will then be recreated.";
        }

        #endregion


        #region Proceed

        private void btnProceed_Click(
            object sender,
            RoutedEventArgs e)
        {
            DialogResult = true;
        }

        #endregion


        #region Abort

        private void btnAbort_Click(
            object sender,
            RoutedEventArgs e)
        {
            DialogResult = false;
        }

        #endregion
    }
}
