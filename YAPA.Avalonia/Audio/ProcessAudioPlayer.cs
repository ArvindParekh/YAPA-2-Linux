using System;
using System.Collections.Generic;
using System.IO;

namespace YAPA.Avalonia.Audio;

/// <summary>Command candidates for a file, best first. Volume is the 0..1 slider value.</summary>
internal delegate IReadOnlyList<PlayerCommand> CommandSource(string path, double volume);

/// <summary>
/// Plays a file through an external player process and supervises it.
///
/// Looping is done by restarting the player when it exits, because the players disagree
/// on loop flags (pw-play and paplay have none). Restarting needs a guard: a player that
/// can't open the device exits instantly, and a naive loop would fork it hundreds of
/// times a second. If it exits within <see cref="QuickExitThreshold"/> three times in a
/// row the player gives up.
///
/// A quick non-zero exit usually means "this tool can't decode this file", so the next
/// candidate is tried before anything counts against the guard.
///
/// Thread-safe: Play/Stop come from the UI thread, exit notifications from the thread pool.
/// </summary>
internal sealed class ProcessAudioPlayer : IBackgroundMusicPlayer
{
    internal static readonly TimeSpan QuickExitThreshold = TimeSpan.FromSeconds(1);
    internal const int MaxConsecutiveQuickExits = 3;

    private readonly object _gate = new();
    private readonly string _role;
    private readonly CommandSource _commands;
    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private Session? _session;
    private bool _disposed;

    /// <param name="role">"music" or "cue", for log lines.</param>
    public ProcessAudioPlayer(string role, CommandSource commands, IProcessLauncher launcher, TimeProvider time)
    {
        _role = role;
        _commands = commands;
        _launcher = launcher;
        _time = time;
    }

    private sealed class Session
    {
        public required string Path { get; init; }
        public required bool Loop { get; init; }
        public required IReadOnlyList<PlayerCommand> Candidates { get; init; }
        public int CandidateIndex { get; set; }
        public IPlayerProcess? Process { get; set; }
        public long StartedAt { get; set; }
        public int ConsecutiveQuickExits { get; set; }
        public PlayerCommand Current => Candidates[CandidateIndex];
    }

    /// <summary>True from Play until the file finishes (not looping), Stop, or the guard gives up.</summary>
    public bool IsPlaying
    {
        get { lock (_gate) return _session is not null; }
    }

    /// <summary>Name of the tool currently playing, for diagnostics.</summary>
    public string? CurrentPlayerName
    {
        get { lock (_gate) return _session?.Process is not null ? _session.Current.Name : null; }
    }

    public void Play(string path, bool loop, double volume)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                KillLocked();

                if (string.IsNullOrWhiteSpace(path))
                    return;
                string fullPath;
                if (PlayerCommands.IsStream(path))
                {
                    fullPath = path; // internet radio
                }
                else
                {
                    // Absolute so a file named "-foo.mp3" can never be read as an option.
                    fullPath = System.IO.Path.GetFullPath(path);
                    if (!File.Exists(fullPath))
                    {
                        AudioLog.Info($"{_role}: file not found: {fullPath}");
                        return;
                    }
                }

                var candidates = _commands(fullPath, volume);
                if (candidates.Count == 0)
                {
                    AudioLog.Once($"{_role}-noplayer:{System.IO.Path.GetExtension(fullPath)}",
                        $"{_role}: no installed player can play {System.IO.Path.GetExtension(fullPath)} files " +
                        "(looked for pw-play, ffplay, gst-play-1.0, mpg123, paplay, aplay)");
                    return;
                }

                _session = new Session { Path = fullPath, Loop = loop, Candidates = candidates };
                StartLocked(_session);
            }
        }
        catch (Exception ex)
        {
            AudioLog.Info($"{_role}: play failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        try
        {
            lock (_gate)
                KillLocked();
        }
        catch (Exception ex)
        {
            AudioLog.Info($"{_role}: stop failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        IPlayerProcess? last;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            last = KillLocked();
        }

        // Kill is asynchronous (a signal). Waiting here, outside the lock, guarantees nothing
        // is still audible once Dispose returns, e.g. at app shutdown.
        last?.WaitForExit(TimeSpan.FromSeconds(1));
        last?.Dispose();
    }

    /// <summary>Ends the session and kills its process. Returns the killed process so Dispose can wait on it.</summary>
    private IPlayerProcess? KillLocked()
    {
        var process = _session?.Process;
        // Detach first: the exit notification for this process must find it stale and do nothing.
        _session = null;
        process?.Kill();
        return process;
    }

    private void StartLocked(Session session)
    {
        while (session.CandidateIndex < session.Candidates.Count)
        {
            var command = session.Current;
            try
            {
                session.StartedAt = _time.GetTimestamp();
                session.Process = _launcher.Start(command, p => OnExited(session, p));
                return;
            }
            catch (Exception ex)
            {
                AudioLog.Info($"{_role}: could not start {command.Executable}: {ex.Message}");
                session.CandidateIndex++;
                session.ConsecutiveQuickExits = 0;
            }
        }

        AudioLog.Info($"{_role}: no player could be started for {session.Path}");
        if (ReferenceEquals(_session, session))
            _session = null;
    }

    private void OnExited(Session session, IPlayerProcess process)
    {
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_session, session) || !ReferenceEquals(session.Process, process))
                {
                    process.Dispose(); // Stopped or replaced; the kill already did its job.
                    return;
                }

                var ranFor = _time.GetElapsedTime(session.StartedAt);
                var quick = ranFor < QuickExitThreshold;
                var exitCode = process.ExitCode;
                var lastError = process.LastErrorLine;
                session.Process = null;
                process.Dispose();

                if (quick && exitCode is not 0 && session.CandidateIndex + 1 < session.Candidates.Count)
                {
                    AudioLog.Info($"{_role}: {session.Current.Name} failed on {session.Path} " +
                                  $"(exit {Describe(exitCode)}{(lastError is null ? "" : ": " + lastError)}); " +
                                  $"trying {session.Candidates[session.CandidateIndex + 1].Name}");
                    session.CandidateIndex++;
                    session.ConsecutiveQuickExits = 0;
                    StartLocked(session);
                    return;
                }

                if (!session.Loop)
                {
                    if (quick && exitCode is not 0)
                        AudioLog.Info($"{_role}: {session.Current.Name} failed on {session.Path} (exit {Describe(exitCode)}{(lastError is null ? "" : ": " + lastError)})");
                    _session = null;
                    return;
                }

                session.ConsecutiveQuickExits = quick ? session.ConsecutiveQuickExits + 1 : 0;
                if (session.ConsecutiveQuickExits >= MaxConsecutiveQuickExits)
                {
                    AudioLog.Info($"{_role}: {session.Current.Name} exited within {QuickExitThreshold.TotalSeconds:0.#} s " +
                                  $"{MaxConsecutiveQuickExits} times in a row on {session.Path}; giving up on looping it");
                    _session = null;
                    return;
                }

                StartLocked(session);
            }
        }
        catch (Exception ex)
        {
            AudioLog.Info($"{_role}: restart failed: {ex.Message}");
        }
    }

    private static string Describe(int? exitCode) => exitCode?.ToString() ?? "unknown";
}
