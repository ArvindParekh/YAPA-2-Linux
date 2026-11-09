#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Statistics;

/// <summary>
/// History bucketed by local calendar day. Only days that have records are stored;
/// lookups for any other day return a zero total.
/// </summary>
public sealed class DailyTotals
{
    private readonly Dictionary<DateOnly, DayTotal> _byDate;

    private DailyTotals(Dictionary<DateOnly, DayTotal> byDate)
    {
        _byDate = byDate;
        Days = byDate.Values.OrderBy(d => d.Date).ToArray();
    }

    /// <summary>Days that have at least one record, oldest first.</summary>
    public IReadOnlyList<DayTotal> Days { get; }

    /// <param name="profile">Exact (ordinal) profile name to keep; null or empty keeps every record.</param>
    public static DailyTotals From(IEnumerable<PomodoroEntity> records, TimeZoneInfo timeZone, string? profile = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(timeZone);

        var byDate = new Dictionary<DateOnly, DayTotal>();
        foreach (var record in records)
        {
            if (!MatchesProfile(record, profile))
                continue;

            var date = LocalCalendar.DateOf(record.DateTime, timeZone);
            byDate.TryGetValue(date, out var total);
            byDate[date] = new DayTotal(date, total.Count + record.Count, total.FocusMinutes + record.DurationMin);
        }

        return new DailyTotals(byDate);
    }

    public static bool MatchesProfile(PomodoroEntity record, string? profile)
        => string.IsNullOrEmpty(profile) || string.Equals(record.ProfileName, profile, StringComparison.Ordinal);

    public DayTotal this[DateOnly date] => _byDate.TryGetValue(date, out var total) ? total : new DayTotal(date, 0, 0);

    /// <summary>Every day from <paramref name="first"/> to <paramref name="last"/> inclusive, zero-filled.</summary>
    public IReadOnlyList<DayTotal> Range(DateOnly first, DateOnly last)
    {
        if (last < first)
            return Array.Empty<DayTotal>();

        var days = new DayTotal[last.DayNumber - first.DayNumber + 1];
        for (var i = 0; i < days.Length; i++)
            days[i] = this[first.AddDays(i)];
        return days;
    }

    public PeriodTotal Sum(DateOnly first, DateOnly last)
    {
        int count = 0, minutes = 0, active = 0;
        // Iterate the stored days rather than the range: ranges can be "all time".
        foreach (var day in Days)
        {
            if (day.Date < first || day.Date > last)
                continue;
            count += day.Count;
            minutes += day.FocusMinutes;
            if (day.IsActive)
                active++;
        }
        return new PeriodTotal(first, last, count, minutes, active);
    }
}
