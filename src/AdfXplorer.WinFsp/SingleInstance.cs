using System.IO;
using System.IO.Pipes;

namespace AdfXplorer.WinFsp;

/// <summary>
/// Ensures only one AdfXplorer tray instance runs at a time. A second invocation - e.g. Explorer
/// double-click launching <c>adfxplorer.exe mount "X"</c>, or the "Unmount (AdfXplorer)" drive verb
/// launching <c>adfxplorer.exe unmount-drive "D:\"</c> - forwards its arguments to the already-running
/// instance over a named pipe and exits immediately instead of starting a second tray icon.
/// </summary>
internal static class SingleInstance
{
    /// <summary>"Local\" scopes this to the current session, matching a per-user tray app (no cross-session single-instance needed).</summary>
    private const string MutexName = "Local\\AdfXplorer_SingleInstance";
    private const string PipeName = "AdfXplorer_Pipe";

    /// <summary>
    /// Named-mutex ownership doubles as the single-instance check: the OS guarantees only one process can
    /// hold it, and <paramref name="mutex"/> must be kept alive (not GC'd/disposed) for the app's whole
    /// lifetime or another process could acquire it out from under a still-running instance.
    /// </summary>
    public static bool TryAcquire(out Mutex mutex)
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        return createdNew;
    }

    /// <summary>Best-effort - if the running instance can't be reached, the request is just dropped.</summary>
    public static void ForwardAndExit(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            foreach (var arg in args)
            {
                writer.WriteLine(arg);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Runs for the life of the process, invoking <paramref name="onRequest"/> (on this background
    /// thread - callers marshal onto the UI thread themselves) for each forwarded request.
    /// </summary>
    public static void StartListening(Action<string[]> onRequest)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var args = new List<string>();
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        args.Add(line);
                    }

                    if (args.Count > 0)
                    {
                        onRequest([.. args]);
                    }
                }
                catch (IOException)
                {
                    // a client disconnected mid-read, or similar - just listen for the next one.
                }
            }
        })
        {
            IsBackground = true,
            Name = "AdfXplorer pipe listener",
        };
        thread.Start();
    }
}
