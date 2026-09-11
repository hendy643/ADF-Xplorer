using System.Windows.Forms;

namespace AdfXplorer.WinFsp;

/// <summary>
/// Marshals dialog display onto the Windows Forms UI thread and blocks the calling (background) thread
/// until it's done - safe to call from any thread except the UI thread itself, since the caller is
/// always a background <see cref="Task"/> (tray menu actions, WinFsp callbacks), never the UI thread, so
/// this can't deadlock. Also exposes the hidden <see cref="Owner"/> window every dialog is parented to
/// (a real, never-shown <see cref="Form"/> - see <see cref="TrayApplicationContext"/> - since Windows
/// Forms dialogs need a Win32 owner to get correct taskbar/z-order/modality behavior).
/// </summary>
internal static class UiThread
{
    private static Control? _marshal;

    /// <summary>The hidden window every dialog is parented to. Throws if <see cref="Initialize"/> hasn't run yet.</summary>
    public static IWin32Window Owner =>
        _marshal ?? throw new InvalidOperationException($"{nameof(UiThread)}.{nameof(Initialize)} was never called.");

    /// <summary>Must be called once, on the UI thread, before any background thread calls <see cref="Invoke{T}"/>.</summary>
    public static void Initialize(Control marshalControl) => _marshal = marshalControl;

    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it to finish.</summary>
    public static void Invoke(Action action) => Invoke<object?>(() =>
    {
        action();
        return null;
    });

    /// <summary>Same as <see cref="Invoke(Action)"/> but returns a value (e.g. a dialog's <see cref="ChecksumDecision"/> result).</summary>
    public static T Invoke<T>(Func<T> action)
    {
        if (_marshal is null)
        {
            throw new InvalidOperationException($"{nameof(UiThread)}.{nameof(Initialize)} was never called.");
        }

        return (T)_marshal.Invoke(action)!;
    }
}
