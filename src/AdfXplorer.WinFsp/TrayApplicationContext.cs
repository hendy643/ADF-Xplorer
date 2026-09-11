using System.Windows.Forms;

namespace AdfXplorer.WinFsp;

/// <summary>
/// Runs the tray-only app: no visible main form, just a <see cref="NotifyIcon"/>-backed message loop
/// (see <see cref="TrayController"/>). <see cref="OwnerWindow"/> is a real <see cref="Form"/> whose
/// native handle is created but which is never <see cref="Form.Show"/>n - it exists purely so dialogs
/// have a Win32 owner and so background threads have somewhere to marshal onto the UI thread (see
/// <see cref="UiThread"/>). Unlike parking an actually-shown window off-screen, a never-shown window
/// can't ever flash into view.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Form _ownerWindow = new() { ShowInTaskbar = false };
    private readonly TrayController _tray;

    public TrayApplicationContext(string[] args)
    {
        _ = _ownerWindow.Handle; // forces native handle creation without ever calling Show()
        UiThread.Initialize(_ownerWindow);

        _tray = new TrayController(ExitThread);

        SingleInstance.StartListening(a => _ownerWindow.BeginInvoke(() => _tray.HandleExternalRequest(a)));

        if (args.Length > 0)
        {
            _tray.HandleExternalRequest(args);
        }
    }

    /// <summary>Tears down the tray icon and hidden window once the message loop is asked to stop (see <see cref="TrayController.Exit"/>).</summary>
    protected override void ExitThreadCore()
    {
        _tray.Dispose();
        _ownerWindow.Dispose();
        base.ExitThreadCore();
    }
}
