using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using YAPA.Avalonia.Audio;

namespace YAPA.Avalonia.Tests.Audio;

internal sealed class ManualTime : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

internal sealed class FakeProcess : IPlayerProcess
{
    private readonly Action<IPlayerProcess> _onExited;

    public FakeProcess(PlayerCommand command, Action<IPlayerProcess> onExited)
    {
        Command = command;
        _onExited = onExited;
    }

    public PlayerCommand Command { get; }
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public string? LastErrorLine { get; set; }
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Simulates the process ending on its own; the notification arrives synchronously.</summary>
    public void Exit(int code)
    {
        HasExited = true;
        ExitCode = code;
        _onExited(this);
    }

    public void Kill()
    {
        if (HasExited) return;
        Killed = true;
        HasExited = true;
        ExitCode = 137;
        // A real kill also produces an exit notification, which the player must ignore.
        _onExited(this);
    }

    public bool WaitForExit(TimeSpan timeout) => HasExited;
    public void Dispose() => Disposed = true;
}

internal sealed class FakeLauncher : IProcessLauncher
{
    public List<FakeProcess> Started { get; } = new();
    public HashSet<string> Unstartable { get; } = new();
    public FakeProcess Last => Started[^1];

    public IPlayerProcess Start(PlayerCommand command, Action<IPlayerProcess> onExited)
    {
        if (Unstartable.Contains(command.Name))
            throw new System.ComponentModel.Win32Exception(2, "No such file or directory");
        var process = new FakeProcess(command, onExited);
        Started.Add(process);
        return process;
    }
}

/// <summary>Records what a cue stream is asked to do. Writes can be slowed to simulate real-time playback.</summary>
internal sealed class FakeCueStream : ICueOutputStream
{
    private readonly object _gate = new();
    private readonly List<string> _events = new();

    public FakeCueStream(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public TimeSpan WriteDelay { get; set; }
    public TimeSpan? Latency { get; set; } = TimeSpan.FromMilliseconds(5);
    public List<float> Written { get; } = new();
    public bool Disposed { get; private set; }

    public IReadOnlyList<string> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public bool Write(ReadOnlySpan<float> interleaved, out string error)
    {
        lock (_gate)
        {
            Written.AddRange(interleaved.ToArray());
            if (_events.Count == 0 || _events[^1] != "write") _events.Add("write");
        }
        if (WriteDelay > TimeSpan.Zero) Thread.Sleep(WriteDelay);
        error = "";
        return true;
    }

    public bool Flush() { lock (_gate) _events.Add("flush"); return true; }
    public bool Drain() { lock (_gate) _events.Add("drain"); return true; }
    public void Dispose() { lock (_gate) _events.Add("dispose"); Disposed = true; }
}

internal static class Wait
{
    public static bool Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < (timeout ?? TimeSpan.FromSeconds(5)))
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}
