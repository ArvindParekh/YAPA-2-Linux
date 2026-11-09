using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.SingleInstance;

/// <summary>
/// Single-instance enforcement and command channel over a Unix domain socket.
///
/// Whoever binds the socket is the primary instance. Later launches connect,
/// send their arguments, read one reply (e.g. the status text for <c>--status</c>)
/// and exit. A socket file left by a crashed instance is detected because
/// connecting to it fails, and is replaced.
/// </summary>
public sealed class InstanceChannel : IDisposable
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);

    private readonly Socket _listener;
    private readonly string _path;
    private readonly Thread _thread;
    private volatile bool _disposed;

    private InstanceChannel(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "YAPA instance channel" };
    }

    /// <summary>
    /// Handles a request from another launch and returns the reply text.
    /// Called on the channel's background thread.
    /// </summary>
    public Func<string[], string>? RequestHandler { get; set; }

    /// <summary>sun_path is 108 bytes on Linux, including the terminating NUL.</summary>
    private const int MaxSocketPathBytes = 107;

    public static string DefaultSocketPath => ChooseSocketPath(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Path.GetTempPath(), AppPaths.InstanceKey);

    /// <summary>
    /// XDG_RUNTIME_DIR is per-user, 0700, and cleared at logout: the right
    /// lifetime and privacy for an IPC socket. A runtime dir can be long enough
    /// to overflow sun_path, which used to crash startup, so fall back to the
    /// temp dir and finally /tmp.
    /// </summary>
    internal static string ChooseSocketPath(string? runtimeDir, string tempDir, string instanceKey)
    {
        var name = $"yapa2-{instanceKey}.sock";
        foreach (var dir in new[] { runtimeDir, tempDir, "/tmp" })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var path = Path.Combine(dir, name);
            if (System.Text.Encoding.UTF8.GetByteCount(path) <= MaxSocketPathBytes) return path;
        }
        return Path.Combine("/tmp", name);
    }

    public static InstanceChannel? TryBecomePrimary(string? path = null)
    {
        path ??= DefaultSocketPath;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(path));
                socket.Listen(8);
                var channel = new InstanceChannel(socket, path);
                channel._thread.Start();
                return channel;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                socket.Dispose();
                if (IsListening(path)) return null;
                TryDelete(path); // stale socket from a crashed instance
            }
        }
        return null;
    }

    /// <summary>Sends arguments to the primary instance. Returns its reply, or null if it didn't answer.</summary>
    public static string? Send(string[] args, string? path = null)
    {
        path ??= DefaultSocketPath;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                ReceiveTimeout = (int)IoTimeout.TotalMilliseconds,
                SendTimeout = (int)IoTimeout.TotalMilliseconds,
            };
            socket.Connect(new UnixDomainSocketEndPoint(path));
            using var stream = new NetworkStream(socket, ownsSocket: false);
            stream.Write(Encoding.UTF8.GetBytes(string.Join('\n', args)));
            socket.Shutdown(SocketShutdown.Send);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            return null;
        }
    }

    private static bool IsListening(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            Socket client;
            try
            {
                client = _listener.Accept();
            }
            catch (Exception) when (_disposed)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            try
            {
                client.ReceiveTimeout = (int)IoTimeout.TotalMilliseconds;
                client.SendTimeout = (int)IoTimeout.TotalMilliseconds;
                using var stream = new NetworkStream(client, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var args = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                // Liveness probes from TryBecomePrimary connect and hang up without a request.
                if (args.Length == 0) continue;

                var reply = RequestHandler?.Invoke(args) ?? string.Empty;
                stream.Write(Encoding.UTF8.GetBytes(reply));
            }
            catch (Exception e)
            {
                System.Diagnostics.Trace.WriteLine($"[ipc] request failed: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Dispose(); } catch { /* shutting down */ }
        TryDelete(_path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
