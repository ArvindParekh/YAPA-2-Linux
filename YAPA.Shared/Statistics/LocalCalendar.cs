#nullable enable
using System;

namespace YAPA.Shared.Statistics;

/// <summary>
/// Maps UTC instants onto local calendar days. All day-level logic works on
/// <see cref="DateOnly"/> after this conversion, so DST only matters here, and
/// a 23- or 25-hour day needs no special casing anywhere else.
/// </summary>
public static class LocalCalendar
{
    /// <summary>
    /// Normalises a timestamp to UTC. <c>Local</c> means the system zone (that is what the kind
    /// says); <c>Unspecified</c> is taken as UTC because that is how history is stored.
    /// </summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static DateOnly DateOf(DateTime instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(instant), timeZone);
        return DateOnly.FromDateTime(local);
    }

    public static DateOnly StartOfWeek(DateOnly date, DayOfWeek firstDayOfWeek)
    {
        var offset = ((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        return date.AddDays(-offset);
    }

    public static DateOnly StartOfMonth(DateOnly date) => new(date.Year, date.Month, 1);
}
