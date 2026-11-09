#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Statistics;

/// <summary>Everything that makes a statistics computation deterministic.</summary>
/// <param name="TimeZone">Zone whose calendar defines "a day". Pass it explicitly; never read the system zone in here.</param>
/// <param name="NowUtc">The current instant. Its local date is "today".</param>
public sealed record StatisticsQuery(TimeZoneInfo TimeZone, DateTime NowUtc)
{
    /// <summary>Exact (ordinal) profile name; null or empty means all profiles.</summary>
    public string? Profile { get; init; }

    public DayOfWeek FirstDayOfWeek { get; init; } = DayOfWeek.Monday;

    /// <summary>Number of week columns in the heatmap, ending with the current week.</summary>
    public int HeatmapWeeks { get; init; } = 26;
}

public sealed class StatisticsReport
{
    public required DateOnly Today { get; init; }

    /// <summary>Per-local-day totals after the profile filter.</summary>
    public required DailyTotals Daily { get; init; }

    public required PeriodTotal TodayTotal { get; init; }
    public required PeriodTotal ThisWeek { get; init; }
    public required PeriodTotal ThisMonth { get; init; }
    public required PeriodTotal AllTime { get; init; }

    /// <summary>
    /// Consecutive active days ending today, or ending yesterday when today has none yet
    /// (the streak is still alive until today is over).
    /// </summary>
    public required Streak CurrentStreak { get; init; }

    /// <summary>Longest run ever. On a tie, the most recent run.</summary>
    public required Streak BestStreak { get; init; }

    /// <summary>Pomodoros per active day over all time; 0 when there are none.</summary>
    public required double AverageCountPerActiveDay { get; init; }

    public required double AverageMinutesPerActiveDay { get; init; }

    /// <summary>Day with the highest count; on a tie, the most recent. Null without history.</summary>
    public required DayTotal? BestDay { get; init; }

    public required Heatmap Heatmap { get; init; }

    /// <summary>Distinct non-empty profile names across <em>all</em> records (ignores the filter), sorted for display.</summary>
    public required IReadOnlyList<string> Profiles { get; init; }
}

public static class PomodoroStatistics
{
    public static StatisticsReport Compute(IEnumerable<PomodoroEntity> records, StatisticsQuery query)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.TimeZone);

        // Materialise once: we make two passes (profiles, totals) and callers may hand us a lazy query.
        var all = records as IReadOnlyCollection<PomodoroEntity> ?? records.ToList();

        var today = LocalCalendar.DateOf(query.NowUtc, query.TimeZone);
        var daily = DailyTotals.From(all, query.TimeZone, query.Profile);

        var weekStart = LocalCalendar.StartOfWeek(today, query.FirstDayOfWeek);
        var monthStart = LocalCalendar.StartOfMonth(today);
        var allTime = daily.Sum(DateOnly.MinValue, DateOnly.MaxValue);

        return new StatisticsReport
        {
            Today = today,
            Daily = daily,
            TodayTotal = daily.Sum(today, today),
            ThisWeek = daily.Sum(weekStart, weekStart.AddDays(6)),
            ThisMonth = daily.Sum(monthStart, monthStart.AddMonths(1).AddDays(-1)),
            AllTime = allTime,
            CurrentStreak = CurrentStreak(daily, today),
            BestStreak = BestStreak(daily),
            AverageCountPerActiveDay = allTime.ActiveDays == 0 ? 0 : (double)allTime.Count / allTime.ActiveDays,
            AverageMinutesPerActiveDay = allTime.ActiveDays == 0 ? 0 : (double)allTime.FocusMinutes / allTime.ActiveDays,
            BestDay = BestDay(daily),
            Heatmap = Heatmap.Build(daily, today, query.HeatmapWeeks, query.FirstDayOfWeek),
            Profiles = DistinctProfiles(all),
        };
    }

    public static Streak CurrentStreak(DailyTotals daily, DateOnly today)
    {
        var end = daily[today].IsActive ? today : today.AddDays(-1);
        var start = end;
        while (daily[start].IsActive)
            start = start.AddDays(-1);

        var length = end.DayNumber - start.DayNumber;
        return length == 0 ? Streak.None : new Streak(start.AddDays(1), length);
    }

    public static Streak BestStreak(DailyTotals daily)
    {
        var best = Streak.None;
        var run = Streak.None;
        foreach (var day in daily.Days)
        {
            if (!day.IsActive)
                continue;

            run = !run.IsEmpty && run.End.AddDays(1) == day.Date
                ? run with { Length = run.Length + 1 }
                : new Streak(day.Date, 1);

            // >= so that a tie goes to the later run (Days is in date order).
            if (run.Length >= best.Length)
                best = run;
        }
        return best;
    }

    private static DayTotal? BestDay(DailyTotals daily)
    {
        DayTotal? best = null;
        foreach (var day in daily.Days)
        {
            if (day.IsActive && (best is null || day.Count >= best.Value.Count))
                best = day;
        }
        return best;
    }

    private static IReadOnlyList<string> DistinctProfiles(IEnumerable<PomodoroEntity> records)
        => records
            .Select(r => r.ProfileName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToArray()!;
}
