using System.Windows.Forms;

namespace AdfXplorer.WinFsp;

/// <summary>Process entry point: single-instance hand-off, then starts the tray app.</summary>
public static class Program
{
    /// <summary>
    /// STAThread is required for Windows Forms (COM interop for common dialogs/clipboard needs an STA
    /// apartment). The .adf/.hdf file association (registered declaratively by the installer - see
    /// Package.wxs - not by this exe) always launches with a single <c>mount "&lt;path&gt;"</c> argument;
    /// there's no other CLI surface.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!SingleInstance.TryAcquire(out _))
        {
            // A tray instance is already running - hand it the request (a double-clicked .adf/.hdf)
            // instead of starting a second tray icon.
            SingleInstance.ForwardAndExit(args);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayApplicationContext(args));
        return 0;
    }
}
