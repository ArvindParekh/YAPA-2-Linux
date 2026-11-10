using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace YAPA.Avalonia.Audio.Ambience;

/// <summary>
/// Plays a recording (any format a decoder on the system understands) as an ambience
/// bed, inside the ambience stream. That is what gives a recording the same treatment
/// as generated rain: live volume, fade in and out, crossfades. Handing the file to an
/// external player can't do that, because its volume is fixed when it starts.
///
/// A decoder process (ffmpeg, else GStreamer) writes raw 48 kHz stereo floats to a pipe.
/// A reader thread keeps a ~2 s ring buffer full; the audio thread only copies from it.
/// At the end of the file the decoder is simply started again while the buffer still
/// holds audio, so the loop has no gap. (A file whose end matches its start, like the
/// crossfaded rain, then loops seamlessly.)
/// </summary>
internal sealed class RecordingGenerator : IAmbienceGenerator, IDisposable
{
    public const int SampleRate = 48_000;
    private const int Channels = 2;

    /// <summary>A decode that yields nothing this many times in a row means the file can't be played.</summary>
    private const int MaxEmptyRuns = 3;

    private readonly Func<Stream?> _startDecoder;
    private readonly string _name;
    private readonly float[] _ring;
    private readonly object _gate = new();
    private int _read, _count;
    private volatile bool _disposed;
    private Stream? _decoder;

    /// <param name="startDecoder">Starts one decode of the file, as interleaved f32le stereo. Disposing it ends the decode.</param>
    public RecordingGenerator(Func<Stream?> startDecoder, string name, TimeSpan? buffer = null)
    {
        _startDecoder = startDecoder;
        _name = name;
        _ring = new float[(int)(SampleRate * (buffer ?? TimeSpan.FromSeconds(2)).TotalSeconds) * Channels];
        new Thread(Fill) { IsBackground = true, Name = "YAPA recording decoder" }.Start();
    }

    /// <summary>Null when no decoder is installed: the caller falls back to an external player.</summary>
    public static RecordingGenerator? TryCreate(string path, ExecutableResolver resolver)
    {
        var command = DecoderFor(Path.GetFullPath(path), resolver);
        if (command == null) return null;
        return new RecordingGenerator(() => Start(command), Path.GetFileName(path));
    }

    public static bool CanDecode(ExecutableResolver resolver) => DecoderFor("/x", resolver) != null;

    internal static PlayerCommand? DecoderFor(string fullPath, ExecutableResolver resolver)
    {
        if (resolver.Resolve("ffmpeg") is { } ffmpeg)
            return new PlayerCommand(ffmpeg, "ffmpeg",
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-i", fullPath, "-vn",
                "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1",
            ]);
        if (resolver.Resolve("gst-launch-1.0") is { } gst)
            return new PlayerCommand(gst, "gst-launch-1.0",
            [
                // gst-launch parses its arguments as one pipeline description, so the path is quoted.
                "-q", "filesrc", $"location=\"{fullPath.Replace("\"", "\\\"")}\"", "!", "decodebin", "!",
                "audioconvert", "!", "audioresample", "!", "audio/x-raw,format=F32LE,channels=2,rate=48000",
                "!", "fdsink", "fd=1",
            ]);
        return null;
    }

    private static Stream? Start(PlayerCommand command)
    {
        var info = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };
        foreach (var arg in command.Arguments) info.ArgumentList.Add(arg);
        try
        {
            return Process.Start(info) is { } process ? new DecoderOutput(process) : null;
        }
        catch (Exception e)
        {
            AudioLog.Info($"ambience: can't start {command.Name}: {e.Message}");
            return null;
        }
    }

    private void Fill()
    {
        var bytes = new byte[16 * 1024];
        var carry = 0; // bytes of a float split across two reads
        var emptyRuns = 0;
        while (!_disposed)
        {
            var decoder = _startDecoder();
            if (decoder == null) return;
            lock (_gate)
            {
                if (_disposed) { decoder.Dispose(); return; }
                _decoder = decoder;
            }

            long produced = 0;
            try
            {
                int n;
                while (!_disposed && (n = decoder.Read(bytes, carry, bytes.Length - carry)) > 0)
                {
                    var total = carry + n;
                    var floats = total / sizeof(float);
                    Write(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, floats * sizeof(float))));
                    produced += floats;
                    carry = total - floats * sizeof(float);
                    if (carry > 0) Array.Copy(bytes, floats * sizeof(float), bytes, 0, carry);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Killed on dispose, or the decoder died; either way this run is over.
            }
            finally
            {
                lock (_gate) _decoder = null;
                decoder.Dispose();
            }

            carry = 0;
            emptyRuns = produced == 0 ? emptyRuns + 1 : 0;
            if (emptyRuns >= MaxEmptyRuns)
            {
                AudioLog.Info($"ambience: {_name} can't be decoded; playing nothing");
                return;
            }
        }
    }

    /// <summary>Copies into the ring, waiting for room. Runs on the reader thread.</summary>
    private void Write(ReadOnlySpan<float> samples)
    {
        while (samples.Length > 0)
        {
            lock (_gate)
            {
                while (_count == _ring.Length && !_disposed) Monitor.Wait(_gate);
                if (_disposed) return;
                var write = (_read + _count) % _ring.Length;
                var n = Math.Min(samples.Length, Math.Min(_ring.Length - _count, _ring.Length - write));
                samples[..n].CopyTo(_ring.AsSpan(write, n));
                _count += n;
                samples = samples[n..];
            }
        }
    }

    /// <summary>Audio thread. Anything the decoder hasn't delivered yet (the very start) is silence.</summary>
    public void MixInto(Span<float> stereo, float gain)
    {
        lock (_gate)
        {
            var done = 0;
            while (done < stereo.Length && _count > 0)
            {
                var n = Math.Min(stereo.Length - done, Math.Min(_count, _ring.Length - _read));
                var source = _ring.AsSpan(_read, n);
                var target = stereo.Slice(done, n);
                for (var i = 0; i < n; i++) target[i] += source[i] * gain;
                _read = (_read + n) % _ring.Length;
                _count -= n;
                done += n;
            }
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Non-blocking, because the mixer calls it on the audio thread.</summary>
    public void Dispose()
    {
        Stream? decoder;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            decoder = _decoder;
            Monitor.PulseAll(_gate);
        }
        decoder?.Dispose(); // the reader thread's Read fails and it exits
    }

    /// <summary>A decoder process's stdout; disposing it kills the process.</summary>
    private sealed class DecoderOutput(Process process) : Stream
    {
        private readonly Stream _out = process.StandardOutput.BaseStream;
        private int _disposed;

        public override int Read(byte[] buffer, int offset, int count) => _out.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
                // Reap it before letting go: a Process disposed before it's been waited for
                // is never reaped, and lingers as a zombie. Off the caller's thread, because
                // that can be the audio thread.
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
                    process.Dispose();
                });
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
