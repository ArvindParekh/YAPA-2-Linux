using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace YAPA.Avalonia.SingleInstance;

/// <summary>
/// Cross-platform single-instance enforcement using a named Mutex for detection
/// and a NamedPipeServerStream (Unix domain socket on Linux) for forwarding
/// command-line arguments from subsequent instances to the first.
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    // Pipe name ends up at /tmp/CoreFxPipe_YAPA2-<username> on Linux.
    private static string PipeName => $"YAPA2-{Environment.UserName}";
    private static string MutexName => $"YAPA2-SingleInstance-{Environment.UserName}";

    private Mutex? _mutex;
    private Thread? _listenerThread;
    private NamedPipeServerStream? _currentPipeServer;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public event Action<string[]>? ArgsReceived;

    /// <summary>
    /// Tries to claim the single-instance mutex.
    /// Returns true if this process is the first instance.
    /// If false, call <see cref="SendCommandLineToFirst"/> and exit.
    /// </summary>
    public bool Acquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance already holds the mutex.
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        StartPipeListener();
        return true;
    }

    /// <summary>
    /// Sends the current process's command-line arguments to the already-running first instance.
    /// </summary>
    public static void SendCommandLineToFirst(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1_000);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true);
            writer.Write(string.Join('\n', args));
            writer.Flush();
        }
        catch
        {
            // Best-effort; if IPC fails the user simply won't see the existing window.
        }
    }

    private void StartPipeListener()
    {
        _listenerThread = new Thread(ListenerLoop)
        {
            IsBackground = true,
            Name = "YAPA2-SingleInstancePipeListener"
        };
        _listenerThread.Start();
    }

    private void ListenerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
            }
            catch
            {
                // Pipe creation failed (e.g., another server is still binding) – retry briefly.
                Thread.Sleep(200);
                continue;
            }

            lock (this)
                _currentPipeServer = server;

            try
            {
                server.WaitForConnection();
            }
            catch
            {
                // Interrupted by Dispose() closing the server, or OS error.
                server.Dispose();
                lock (this) _currentPipeServer = null;
                break;
            }

            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8);
                var content = reader.ReadToEnd();
                var args = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                ArgsReceived?.Invoke(args);
            }
            catch { /* ignore parse errors */ }
            finally
            {
                server.Dispose();
                lock (this) _currentPipeServer = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        // Closing the current server stream interrupts WaitForConnection().
        lock (this)
            _currentPipeServer?.Dispose();

        try { _mutex?.ReleaseMutex(); } catch { /* already released */ }
        _mutex?.Dispose();
        _cts.Dispose();
    }
}
