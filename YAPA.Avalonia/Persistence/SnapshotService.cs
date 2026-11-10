using System;
using System.Diagnostics;
using System.IO;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Persistence;

/// <summary>
/// Carries the timer across restarts: saved on quit, restored on the next launch.
/// </summary>
public sealed class SnapshotService : IDisposable
{
    /// <summary>Beyond this, the cycle position is stale (e.g. yesterday's) and the day starts fresh.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private readonly IPomodoroEngine _engine;
    private readonly IClock _clock;
    private readonly IJson _json;
    private readonly string _path;

    /// <summary>
    /// How often a running timer is checkpointed. A crash, kill -9 or power cut never
    /// reaches the save-on-quit path, so without this they lose the whole session.
    /// </summary>
    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(15);

    private readonly System.Threading.Timer _checkpoint;
    private int _restoring;

    public SnapshotService(IPomodoroEngine engine, IClock clock, IJson json)
    {
        _engine = engine;
        _clock = clock;
        _json = json;
        AppPaths.EnsureCreated();
        _path = Path.Combine(AppPaths.DataDirectory, "snapshot.json");

        // Every state change is saved at once; elapsed time in between, periodically.
        _engine.Transitioned += _ => { if (_restoring == 0) Save(); };
        _checkpoint = new System.Threading.Timer(_ => Checkpoint(), null, CheckpointInterval, CheckpointInterval);
    }

    private void Checkpoint()
    {
        // The engine belongs to the UI thread; read it there.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_engine.IsRunning) Save();
        });
    }

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(_path, _json.Serialize(_engine.GetSnapshot()));
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[snapshot] save failed: {e.Message}");
        }
    }

    /// <summary>
    /// Restores the previous session. An interval that was running comes back
    /// paused rather than running: the user decides with one click whether to
    /// carry on, and nothing ticks away while they weren't looking.
    /// </summary>
    /// <param name="resume">Resume immediately (the app was launched with <c>--start</c>).</param>
    /// <returns>True if something was restored.</returns>
    public bool Restore(bool resume)
    {
        PomodoroEngineSnapshot? snapshot;
        try
        {
            if (!File.Exists(_path)) return false;
            snapshot = _json.Deserialize<PomodoroEngineSnapshot>(File.ReadAllText(_path));
            File.Delete(_path);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[snapshot] unreadable, ignoring: {e.Message}");
            return false;
        }

        if (snapshot == null) return false;
        System.Threading.Interlocked.Exchange(ref _restoring, 1);
        try
        {
            return RestoreSnapshot(snapshot, resume);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _restoring, 0);
            Save(); // the file was consumed above; put the current state back on disk
        }
    }

    private bool RestoreSnapshot(PomodoroEngineSnapshot snapshot, bool resume)
    {
        var age = _clock.UtcNow - DateTime.SpecifyKind(snapshot.StartDate, DateTimeKind.Utc);
        if (age < TimeSpan.Zero || age > MaxAge) return false;
        if (snapshot.Phase == PomodoroPhase.NotStarted && snapshot.PomodoroIndex <= 1) return false;

        if (snapshot.Phase is PomodoroPhase.Work or PomodoroPhase.Break)
        {
            snapshot.PausedPhase = snapshot.Phase;
            snapshot.Phase = PomodoroPhase.Pause;
        }

        _engine.LoadSnapshot(snapshot);
        if (resume && _engine.Phase == PomodoroPhase.Pause) _engine.Start();

        Trace.WriteLine($"[snapshot] restored {snapshot.Phase} at {snapshot.PausedTime}s, pomodoro {snapshot.PomodoroIndex}");
        return true;
    }

    public void Dispose() => _checkpoint.Dispose();
}
