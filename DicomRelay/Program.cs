using System;
using System.Threading;
using System.Windows.Forms;

namespace DicomRelay
{
    /// <summary>
    /// Application entry point. Enforces single-instance execution via named Mutex
    /// and initializes the system tray application.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Single-instance guard
            using var mutex = new Mutex(true, "DicomRelay_SingleInstance", out bool isNew);
            if (!isNew)
            {
                MessageBox.Show(
                    "DICOM Relay is already running.\nCheck the system tray.",
                    "Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var tray = new TrayApp();
            Application.Run();
        }
    }
}
