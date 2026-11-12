using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <param name="AtUtc">For a time: when it runs.</param>
/// <param name="When">For an event: "pomodoro_ends" or "break_ends".</param>
/// <param name="Message">For a reminder: what it says.</param>
public sealed record ScheduledAction(string Id, string Action, DateTime? AtUtc, string? When, string? Message, DateTime CreatedUtc)
{
    public const string PomodoroEnds = "pomodoro_ends";
    public const string BreakEnds = "break_ends";

    public static readonly string[] Actions = ["start", "pause", "resume", "stop", "skip_break", "take_long_break", "remind"];

    /// <summary>"Stop at 18:00", "Take the long break when this pomodoro ends".</summary>
    public string Describe(DateTime now)
    {
        var what = Action switch
        {
            "start" => "Start the next interval",
            "pause" => "Pause",
            "resume" => "Resume",
            "stop" => "Stop",
            "skip_break" => "Skip the break",
            "take_long_break" => "Take the long break",
            _ => $"Remind you: {Message}",
        };
        if (When == PomodoroEnds) return what + " when the pomodoro ends";
        if (When == BreakEnds) return what + " when the break ends";
        var at = AtUtc!.Value.ToLocalTime();
        return $"{what} at {ClockFormat.ShortTime(at)}{(at.Date > now.Date ? " tomorrow" : "")}";
    }
}

/// <summary>
/// Things to do later, asked of the assistant: "stop at 6", "start the next one at 2:30",
/// "when this pomodoro ends, take the long break", "remind me to stretch at 4". Each runs
/// once, on the engine directly (the conversation may be long gone), and says so in a
/// desktop notification. Kept on disk, so a restart doesn't forget them.
/// </summary>
public sealed class Scheduler : IDisposable
{
    /// <summary>A time missed by more than this while YAPA was closed is dropped, not run late.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly IPomodoroEngine _engine;
    private readonly IDesktopNotifier _notifier;
    private readonly IClock _clock;
    private readonly ITimer _timer;
    private readonly string _path;
    private readonly List<ScheduledAction> _items = [];

    public Scheduler(IPomodoroEngine engine, IDesktopNotifier notifier, IClock clock, ITimer timer, string? path = null)
    {
        _engine = engine;
        _notifier = notifier;
        _clock = clock;
        _timer = timer;
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "scheduled.json");
        _timer.Tick += CheckDue;
        _engine.Transitioned += OnTransition;
        Load();
    }

    /// <summary>Raised after the list changed (added, cancelled, run).</summary>
    public event Action? Changed;

    public IReadOnlyList<ScheduledAction> Items => _items;

    public ScheduledAction Add(string action, DateTime? atUtc, string? when, string? message)
    {
        var item = new ScheduledAction(Guid.NewGuid().ToString("N")[..6], action, atUtc, when, message, _clock.UtcNow);
        _items.Add(item);
        Saved();
        return item;
    }

    /// <summary>Puts back an item removed by mistake (undo).</summary>
    public void Restore(ScheduledAction item)
    {
        if (_items.Any(i => i.Id == item.Id)) return;
        _items.Add(item);
        Saved();
    }

    public bool Remove(string id)
    {
        var removed = _items.RemoveAll(i => i.Id == id) > 0;
        if (removed) Saved();
        return removed;
    }

    /// <summary>Runs everything whose time has come. The timer calls it; tests call it directly.</summary>
    public void CheckDue()
    {
        var now = _clock.UtcNow;
        var due = _items.Where(i => i.AtUtc is { } at && at <= now).OrderBy(i => i.AtUtc).ToList();
        foreach (var item in due)
        {
            _items.Remove(item);
            if (now - item.AtUtc!.Value > Grace)
                Trace.WriteLine($"[schedule] dropped \"{item.Describe(now.ToLocalTime())}\": missed while YAPA was closed");
            else
                Run(item);
        }
        if (due.Count > 0) Saved();
        else Reschedule();
    }

    private void OnTransition(PomodoroTransition t)
    {
        var when = t.Kind switch
        {
            PomodoroTransitionKind.WorkCompleted => ScheduledAction.PomodoroEnds,
            PomodoroTransitionKind.BreakCompleted => ScheduledAction.BreakEnds,
            _ => null,
        };
        if (when == null) return;
        var due = _items.Where(i => i.When == when).ToList();
        if (due.Count == 0) return;
        foreach (var item in due)
        {
            _items.Remove(item);
            Run(item);
        }
        Saved();
    }

    private void Run(ScheduledAction item)
    {
        var done = Perform(item);
        Trace.WriteLine($"[schedule] {item.Action}: {done ?? "nothing to do"}");
        if (done != null)
            _ = _notifier.ShowAsync(new DesktopNotification(item.Action == "remind" ? "Reminder" : "Done, as you asked", done, [],
                Persistent: item.Action == "remind", Category: DesktopNotification.ReminderCategory, Chime: true));
    }

    /// <returns>What happened, or null when the timer wasn't in a state the action applies to.</returns>
    private string? Perform(ScheduledAction item)
    {
        var e = _engine;
        var pausedBreak = e.Phase == PomodoroPhase.Pause && e.PausedPhase == PomodoroPhase.Break;
        switch (item.Action)
        {
            case "start" when !e.IsRunning:
                e.Start();
                return e.Phase == PomodoroPhase.Work ? $"Pomodoro {e.Index} started." : "The break started.";
            case "pause" when e.IsRunning:
                e.Pause();
                return "The timer is paused.";
            case "resume" when e.Phase == PomodoroPhase.Pause:
                e.Start();
                return "The timer is running again.";
            case "stop" when e.Phase != PomodoroPhase.NotStarted:
                var wasWork = e.Phase == PomodoroPhase.Work || (e.Phase == PomodoroPhase.Pause && !pausedBreak);
                e.Stop();
                return wasWork ? "Stopped. That pomodoro wasn't recorded." : "Stopped.";
            case "skip_break" when e.Phase is PomodoroPhase.WorkEnded or PomodoroPhase.Break or PomodoroPhase.BreakEnded || pausedBreak:
                e.Skip();
                return $"Break skipped; pomodoro {e.Index} started.";
            case "take_long_break" when e.CanStartLongBreak:
                e.StartLongBreak();
                return "The long break started.";
            case "remind":
                return item.Message ?? "You asked to be reminded.";
            default:
                return null;
        }
    }

    private void Saved()
    {
        Reschedule();
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_items));
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[schedule] can't save {_path}: {e.Message}");
        }
        Changed?.Invoke();
    }

    private void Reschedule()
    {
        // Only times need ticks; events come from the engine.
        if (_items.Any(i => i.AtUtc != null))
        {
            _timer.Start();
            var next = _items.Where(i => i.AtUtc != null).Min(i => i.AtUtc!.Value) - _clock.UtcNow;
            _timer.ScheduleNext(next < TimeSpan.Zero ? TimeSpan.Zero : next);
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<List<ScheduledAction>>(File.ReadAllText(_path)) is { } saved)
                _items.AddRange(saved.Where(i => ScheduledAction.Actions.Contains(i.Action)));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[schedule] can't read {_path}: {e.Message}");
        }
        // Event-triggered items belonged to an interval that ended with the last run.
        _items.RemoveAll(i => i.When != null);
        var now = _clock.UtcNow;
        _items.RemoveAll(i => i.AtUtc is { } at && now - at > Grace);
        Reschedule();
    }

    public void Dispose()
    {
        _timer.Tick -= CheckDue;
        _engine.Transitioned -= OnTransition;
        _timer.Stop();
    }
}
