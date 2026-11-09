#nullable enable
using System;
using System.Globalization;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Tests.Statistics;

internal static class StatisticsTestData
{
    public static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateTime Utc(string iso)
        => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static PomodoroEntity At(string isoUtc, int count = 1, int minutes = 25, string? profile = null)
        => new() { DateTime = Utc(isoUtc), Count = count, DurationMin = minutes, ProfileName = profile! };

    /// <summary>A record at local noon of <paramref name="date"/> in <paramref name="zone"/>, far from any day edge.</summary>
    public static PomodoroEntity OnLocalDay(DateOnly date, TimeZoneInfo zone, int count = 1, int minutes = 25, string? profile = null)
    {
        var localNoon = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        return new PomodoroEntity
        {
            DateTime = TimeZoneInfo.ConvertTimeToUtc(localNoon, zone),
            Count = count,
            DurationMin = minutes,
            ProfileName = profile!,
        };
    }

    public static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
