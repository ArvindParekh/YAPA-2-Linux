#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Statistics;

/// <summary>
/// Focus minutes by hour of the local day, over the last <see cref="Days"/> days.
///
/// A pomodoro is recorded when it ends, so each one is spread back over the minutes it
/// actually ran: a 50-minute pomodoro ending at 10:05 counts 45 minutes at 9 and 5 at 10.
/// Bucketing by the end time alone would shift the whole day an hour late.
/// </summary>
public sealed class HourOfDay
{
    /// <summary>Records from before duration was stored count as a classic pomodoro.</summary>
    private const int FallbackMinutes = 25;

    private HourOfDay(double[] minutes, int days)
    {
        Minutes = minutes;
        Days = days;
    }

    /// <summary>24 entries, midnight first.</summary>
    public IReadOnlyList<double> Minutes { get; }

    public int Days { get; }

    public bool IsEmpty => Minutes.All(m => m <= 0);

    /// <summary>The hour with the most focus; on a tie, the earliest. Null when empty.</summary>
    public int? PeakHour
    {
        get
        {
            if (IsEmpty) return null;
            var best = 0;
            for (var h = 1; h < 24; h++)
                if (Minutes[h] > Minutes[best]) best = h;
            return best;
        }
    }

    /// <param name="today">The last day of the window, in <paramref name="timeZone"/>.</param>
    public static HourOfDay Build(IEnumerable<PomodoroEntity> records, TimeZoneInfo timeZone, DateOnly today, int days, string? profile = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));

        var first = today.AddDays(-(days - 1));
        var minutes = new double[24];
        foreach (var record in records)
        {
            if (!DailyTotals.MatchesProfile(record, profile))
                continue;

            var endUtc = DateTime.SpecifyKind(record.DateTime, DateTimeKind.Utc);
            var duration = record.DurationMin > 0 ? record.DurationMin : FallbackMinutes * Math.Max(1, record.Count);

            // Minute by minute in UTC, each placed by its own local time: across a DST change,
            // arithmetic on local times would count an hour that didn't exist or lose one that
            // happened twice. A pomodoro is a few dozen minutes, so this is cheap.
            for (var m = duration; m > 0; m--)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(endUtc.AddMinutes(-m + 0.5), timeZone);
                var date = DateOnly.FromDateTime(local);
                // Clipped to the window, so a pomodoro across its first midnight counts only its part inside.
                if (date < first || date > today) continue;
                minutes[local.Hour] += 1;
            }
        }

        return new HourOfDay(minutes, days);
    }
}
