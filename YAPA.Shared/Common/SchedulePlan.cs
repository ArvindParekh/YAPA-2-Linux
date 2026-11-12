#nullable enable
using System;
using System.Collections.Generic;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Common;

public enum PlannedKind
{
    Pomodoro,
    ShortBreak,
    LongBreak,
}

/// <param name="Number">For a pomodoro: which one of the day it would be (today's count so far + n).</param>
/// <param name="InProgress">The interval is already under way (running or paused) at the start of the plan.</param>
/// <param name="CutOff">It would still be going at the plan's end time.</param>
public sealed record PlannedInterval(PlannedKind Kind, int? Number, DateTime Start, DateTime End, bool InProgress, bool CutOff);

/// <summary>
/// The day ahead if every interval is started the moment the previous one ends, on the
/// timer's own rules (cycle position, which break is long, the interval under way).
/// Same assumptions as <see cref="GoalForecast"/>: a pause is taken as resumed now, and
/// nothing predicts the waiting between intervals.
/// </summary>
public static class SchedulePlan
{
    /// <summary>More than any real day; a guard for "until next week".</summary>
    public const int MaxIntervals = 96;

    /// <param name="now">Local wall-clock time the plan starts from.</param>
    /// <param name="until">Stop once an interval would end after this (local time). At least one of this and <paramref name="pomodoros"/>.</param>
    /// <param name="pomodoros">Stop after this many pomodoros, the one under way included.</param>
    /// <param name="startAt">When nothing is under way: start the first pomodoro then instead of now.</param>
    /// <param name="longBreaks">False: every long break is taken as a short one.</param>
    /// <param name="doneToday">Pomodoros already done today, to number the planned ones.</param>
    public static IReadOnlyList<PlannedInterval> Build(IPomodoroEngine engine, PomodoroEngineSettings settings, DateTime now,
        DateTime? until, int? pomodoros, DateTime? startAt = null, bool longBreaks = true, int doneToday = 0)
    {
        if (until == null && pomodoros == null) throw new ArgumentException("Give an end time or a number of pomodoros.");

        var work = TimeSpan.FromSeconds(engine.WorkTime);
        var shortBreak = TimeSpan.FromSeconds(Math.Max(1, settings.BreakTime));
        var longBreak = TimeSpan.FromSeconds(Math.Max(1, settings.LongBreakTime));
        var cycleLength = Math.Max(1, settings.PomodorosBeforeLongBreak);

        var result = new List<PlannedInterval>();
        var number = doneToday;
        var remainingPomodoros = pomodoros ?? int.MaxValue;
        var position = engine.Index;
        var longDue = engine.IsLongBreak;
        var t = now;
        bool breakDue;

        // Adds an interval; false once the plan is over.
        bool Add(PlannedKind kind, DateTime start, TimeSpan length, bool inProgress)
        {
            var end = start + length;
            var cutOff = until is { } u && end > u;
            int? n = kind == PlannedKind.Pomodoro ? ++number : null;
            result.Add(new PlannedInterval(kind, n, start, end, inProgress, cutOff));
            t = end;
            if (kind == PlannedKind.Pomodoro) remainingPomodoros--;
            return !cutOff && remainingPomodoros > 0 && result.Count < MaxIntervals;
        }

        PlannedKind BreakKind(bool isLong) => isLong && longBreaks ? PlannedKind.LongBreak : PlannedKind.ShortBreak;

        var phase = engine.Phase == PomodoroPhase.Pause ? engine.PausedPhase : engine.Phase;
        var going = true;
        switch (phase)
        {
            case PomodoroPhase.Work:
                going = Add(PlannedKind.Pomodoro, now - TimeSpan.FromSeconds(engine.Elapsed), TimeSpan.FromSeconds(engine.CurrentIntervalLength), true);
                breakDue = true;
                break;
            case PomodoroPhase.WorkEnded:
                breakDue = true;
                break;
            case PomodoroPhase.Break:
                // A long break cut to a short one ends when a short break would have.
                var length = longDue && !longBreaks
                    ? TimeSpan.FromSeconds(Math.Max(engine.Elapsed, shortBreak.TotalSeconds))
                    : TimeSpan.FromSeconds(engine.CurrentIntervalLength);
                going = Add(BreakKind(longDue), now - TimeSpan.FromSeconds(engine.Elapsed), length, true);
                position = longDue ? 1 : position + 1;
                breakDue = false;
                break;
            case PomodoroPhase.BreakEnded:
                position = longDue ? 1 : position + 1;
                breakDue = false;
                break;
            default:
                if (startAt is { } s && s > now) t = s;
                breakDue = false;
                break;
        }

        while (going)
        {
            if (breakDue)
            {
                if (!Add(BreakKind(longDue), t, longDue && longBreaks ? longBreak : shortBreak, false)) break;
                position = longDue ? 1 : position + 1;
            }
            longDue = position >= cycleLength;
            going = Add(PlannedKind.Pomodoro, t, work, false);
            breakDue = true;
        }

        return result;
    }
}
