#nullable enable
using System;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Common;

/// <summary>
/// How long until today's goal is reached, if every interval is started the moment the
/// previous one ends. Time spent waiting (an overrun, a pause, not having started) is
/// not predicted; it pushes the forecast later as it happens, which is the point:
/// the forecast shows what each delay costs.
/// </summary>
/// <param name="Remaining">Pomodoros still to do, the current one included.</param>
/// <param name="Left">Time until the last of them ends, taking breaks as scheduled.</param>
/// <param name="LeftWithoutLongBreaks">The same, with every long break (the current one too) cut to a short one.</param>
public readonly record struct GoalForecast(int Remaining, TimeSpan Left, TimeSpan LeftWithoutLongBreaks)
{
    /// <summary>Whether a long break falls before the goal at all.</summary>
    public bool LongBreaksMatter => Left != LeftWithoutLongBreaks;

    /// <returns>Null when there's no goal or it has been reached.</returns>
    public static GoalForecast? For(IPomodoroEngine engine, PomodoroEngineSettings settings, int doneToday, int goal)
    {
        var remaining = goal - doneToday;
        if (goal <= 0 || remaining <= 0) return null;

        var plan = new Plan(
            Work: engine.WorkTime,
            ShortBreak: Math.Max(1, settings.BreakTime),
            LongBreak: Math.Max(1, settings.LongBreakTime),
            CycleLength: Math.Max(1, settings.PomodorosBeforeLongBreak));
        return new GoalForecast(remaining,
            Simulate(engine, plan, remaining, longBreaks: true),
            Simulate(engine, plan, remaining, longBreaks: false));
    }

    private readonly record struct Plan(int Work, int ShortBreak, int LongBreak, int CycleLength);

    private static TimeSpan Simulate(IPomodoroEngine engine, Plan plan, int needed, bool longBreaks)
    {
        long seconds = 0;
        var position = engine.Index; // the current pomodoro's place in the cycle, or the next one's
        var longDue = engine.IsLongBreak; // the break after it (or under way) is the long one
        bool breakDue; // a break comes before the next pomodoro

        // A paused interval is forecast as if resumed now; the refresh moves it on while it isn't.
        var phase = engine.Phase == PomodoroPhase.Pause ? engine.PausedPhase : engine.Phase;
        switch (phase)
        {
            case PomodoroPhase.Work:
                seconds += engine.Remaining;
                needed--;
                breakDue = true;
                break;
            case PomodoroPhase.WorkEnded:
                breakDue = true;
                break;
            case PomodoroPhase.Break:
                seconds += longDue && !longBreaks
                    ? Math.Max(0, plan.ShortBreak - engine.Elapsed) // cut short to a normal break
                    : engine.Remaining;
                position = longDue ? 1 : position + 1;
                breakDue = false;
                break;
            case PomodoroPhase.BreakEnded:
                position = longDue ? 1 : position + 1;
                breakDue = false;
                break;
            default:
                breakDue = false;
                break;
        }

        while (needed > 0)
        {
            if (breakDue)
            {
                seconds += longDue && longBreaks ? plan.LongBreak : plan.ShortBreak;
                position = longDue ? 1 : position + 1;
            }
            seconds += plan.Work;
            needed--;
            longDue = position >= plan.CycleLength;
            breakDue = true;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
