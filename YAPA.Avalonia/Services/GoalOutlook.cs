using System;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Services;

/// <summary>
/// Today against the daily goal, in words: how many are done and, from the
/// <see cref="GoalForecast"/>, the time of day the goal would be reached.
/// </summary>
public sealed record GoalOutlook(int Goal, int Done, DateTime? Finish, DateTime? FinishWithoutLongBreaks, DateTime Now)
{
    public static readonly GoalOutlook None = new(0, 0, null, null, default);

    public static GoalOutlook Compute(IPomodoroEngine engine, PomodoroEngineSettings settings, int goal, int done, DateTime nowUtc)
    {
        if (goal <= 0) return None;
        var now = nowUtc.ToLocalTime();
        if (GoalForecast.For(engine, settings, done, goal) is not { } forecast) return new GoalOutlook(goal, done, null, null, now);

        var finish = RoundUp(now + forecast.Left);
        var withoutLongBreaks = RoundUp(now + forecast.LeftWithoutLongBreaks);
        // Compared as shown: two figures that read the same would only puzzle.
        return new GoalOutlook(goal, done, finish, withoutLongBreaks == finish ? null : withoutLongBreaks, now);
    }

    /// <summary>"by 18:40" must not be read as earlier than it is, so seconds round up.</summary>
    private static DateTime RoundUp(DateTime t)
    {
        var minute = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
        return minute == t ? t : minute.AddMinutes(1);
    }

    public bool HasGoal => Goal > 0;

    /// <summary>For the timer: "12 by 18:40". Empty without a forecast.</summary>
    public string Short => Finish switch
    {
        null => "",
        { } f when f.Date > Now.Date => $"{Goal} after midnight",
        { } f => $"{Goal} by {ClockFormat.ShortTime(f)}",
    };

    /// <summary>"5 of 12 today · done by 18:40". Empty without a goal.</summary>
    public string Summary => !HasGoal ? ""
        : Finish is { } f ? $"{Done} of {Goal} today · done by {Time(f)}"
        : $"{Done} of {Goal} today";

    /// <summary>"18:10 without long breaks", only when skipping them would change anything.</summary>
    public string WithoutLongBreaks => FinishWithoutLongBreaks is { } f ? $"{Time(f)} without long breaks" : "";

    private string Time(DateTime t) => ClockFormat.ShortTime(t) + (t.Date > Now.Date ? " tomorrow" : "");
}
