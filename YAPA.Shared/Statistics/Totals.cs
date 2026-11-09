#nullable enable
using System;

namespace YAPA.Shared.Statistics;

/// <summary>Totals for one local calendar day.</summary>
/// <param name="Count">Sum of <c>PomodoroEntity.Count</c>. A day is active when this is at least 1.</param>
/// <param name="FocusMinutes">Sum of <c>PomodoroEntity.DurationMin</c>.</param>
public readonly record struct DayTotal(DateOnly Date, int Count, int FocusMinutes)
{
    public bool IsActive => Count > 0;
}

/// <summary>Totals over the inclusive local date range <see cref="First"/>..<see cref="Last"/>.</summary>
public readonly record struct PeriodTotal(DateOnly First, DateOnly Last, int Count, int FocusMinutes, int ActiveDays);

/// <summary>A run of consecutive active days. <see cref="Length"/> 0 means no streak.</summary>
public readonly record struct Streak(DateOnly Start, int Length)
{
    public static Streak None => default;

    public bool IsEmpty => Length == 0;

    public DateOnly End => Start.AddDays(Math.Max(Length - 1, 0));
}
