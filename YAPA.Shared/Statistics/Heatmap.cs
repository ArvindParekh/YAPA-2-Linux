#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace YAPA.Shared.Statistics;

/// <param name="Level">Intensity 0–4, see <see cref="HeatmapScale"/>.</param>
/// <param name="IsFuture">Later than today; present only so the last column is a full week.</param>
public readonly record struct HeatmapCell(DateOnly Date, int Count, int FocusMinutes, int Level, bool IsFuture);

/// <summary>One column. <see cref="Days"/> always has 7 cells; index 0 is the heatmap's first day of week.</summary>
public sealed record HeatmapWeek(DateOnly Start, IReadOnlyList<HeatmapCell> Days);

/// <summary>
/// GitHub-style activity grid: columns are weeks (oldest first, the last one is the week
/// containing today), rows are weekdays starting at <see cref="FirstDayOfWeek"/>.
/// </summary>
public sealed class Heatmap
{
    private Heatmap(DayOfWeek firstDayOfWeek, IReadOnlyList<HeatmapWeek> weeks, int fullScaleCount)
    {
        FirstDayOfWeek = firstDayOfWeek;
        Weeks = weeks;
        FullScaleCount = fullScaleCount;
        RowDays = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)firstDayOfWeek + i) % 7)).ToArray();
    }

    public DayOfWeek FirstDayOfWeek { get; }

    /// <summary>Weekday of each row, for labels.</summary>
    public IReadOnlyList<DayOfWeek> RowDays { get; }

    public IReadOnlyList<HeatmapWeek> Weeks { get; }

    /// <summary>The count that maps to level 4. Lets a legend or tooltip explain the scale.</summary>
    public int FullScaleCount { get; }

    public DateOnly Start => Weeks[0].Start;

    public DateOnly End => Weeks[^1].Days[^1].Date;

    public HeatmapCell this[int week, int row] => Weeks[week].Days[row];

    public int LevelFor(int count) => HeatmapScale.Level(count, FullScaleCount);

    public static Heatmap Build(DailyTotals daily, DateOnly today, int weeks, DayOfWeek firstDayOfWeek)
    {
        ArgumentNullException.ThrowIfNull(daily);
        if (weeks < 1)
            throw new ArgumentOutOfRangeException(nameof(weeks), weeks, "A heatmap needs at least one week.");

        var firstColumn = LocalCalendar.StartOfWeek(today, firstDayOfWeek).AddDays(-7 * (weeks - 1));
        var totals = daily.Range(firstColumn, firstColumn.AddDays(7 * weeks - 1));

        var fullScale = HeatmapScale.FullScaleFor(totals.Where(t => t.Date <= today).Select(t => t.Count));

        var columns = new HeatmapWeek[weeks];
        for (var w = 0; w < weeks; w++)
        {
            var cells = new HeatmapCell[7];
            for (var d = 0; d < 7; d++)
            {
                var t = totals[w * 7 + d];
                cells[d] = new HeatmapCell(t.Date, t.Count, t.FocusMinutes, HeatmapScale.Level(t.Count, fullScale), t.Date > today);
            }
            columns[w] = new HeatmapWeek(firstColumn.AddDays(7 * w), cells);
        }

        return new Heatmap(firstDayOfWeek, columns, fullScale);
    }
}

/// <summary>
/// Quantises daily counts into intensity levels 0–4.
/// <list type="bullet">
/// <item>Level 0 means exactly no pomodoros. Any activity is at least level 1, so a
/// single pomodoro is never invisible.</item>
/// <item>Levels 1–4 split <c>(0, F]</c> into four equal bands:
/// <c>level = ceil(4 · count / F)</c>, capped at 4.</item>
/// <item>The full-scale count <c>F</c> is the 90th percentile (nearest rank) of the
/// <em>active</em> days in the period, so one marathon day does not wash every other
/// day out to level 1. Zero days are excluded: in sparse data they would otherwise
/// dominate every percentile.</item>
/// <item><c>F</c> is at least <see cref="MinimumFullScale"/>. With sparse or uniform data
/// (say one pomodoro every few days) a purely relative scale would paint every active day
/// at full intensity, which reads as "you did a lot". The floor keeps light use looking light:
/// 1 → level 1, 2 → 2, 3 → 3, 4+ → 4.</item>
/// </list>
/// Linear bands rather than GitHub-style quartiles: quartiles over a handful of distinct
/// values skip levels, and a day's colour reshuffles every time a new day arrives.
/// </summary>
public static class HeatmapScale
{
    public const int Levels = 4;

    /// <summary>A day of this many pomodoros is "full" even if the user never does more.</summary>
    public const int MinimumFullScale = 4;

    public static int FullScaleFor(IEnumerable<int> dailyCounts)
    {
        var active = dailyCounts.Where(c => c > 0).OrderBy(c => c).ToArray();
        if (active.Length == 0)
            return MinimumFullScale;

        // Nearest rank, ceil(0.9 · n), in integers so it never lands one off on a float rounding.
        var rank = (9 * active.Length + 9) / 10 - 1;
        return Math.Max(MinimumFullScale, active[rank]);
    }

    public static int Level(int count, int fullScale)
    {
        if (count <= 0)
            return 0;
        if (fullScale < 1)
            throw new ArgumentOutOfRangeException(nameof(fullScale), fullScale, "Full scale must be positive.");

        // Integer ceil(Levels * count / fullScale) without floating-point edge cases.
        var level = (int)((Levels * (long)count + fullScale - 1) / fullScale);
        return Math.Clamp(level, 1, Levels);
    }
}
