#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Shared.Statistics;
using static YAPA.Shared.Tests.Statistics.StatisticsTestData;

namespace YAPA.Shared.Tests.Statistics;

[TestClass]
public class LocalDayTests
{
    [DataTestMethod]
    // 2026-03-08 is a 23-hour day in New York: 00:00 EST = 05:00Z, 03:00 EDT follows 01:59:59 EST.
    [DataRow("2026-03-08T04:59:59.9999999Z", "2026-03-07")]
    [DataRow("2026-03-08T05:00:00Z", "2026-03-08")]
    [DataRow("2026-03-08T06:59:59Z", "2026-03-08")] // 01:59:59 EST
    [DataRow("2026-03-08T07:00:00Z", "2026-03-08")] // 03:00:00 EDT
    [DataRow("2026-03-09T03:59:59.9999999Z", "2026-03-08")] // 23:59:59 EDT
    [DataRow("2026-03-09T04:00:00Z", "2026-03-09")]
    // 2026-11-01 is a 25-hour day: 00:00 EDT = 04:00Z, 01:00-01:59 happens twice, 00:00 EST next day = 05:00Z.
    [DataRow("2026-11-01T03:59:59.9999999Z", "2026-10-31")]
    [DataRow("2026-11-01T04:00:00Z", "2026-11-01")]
    [DataRow("2026-11-01T05:30:00Z", "2026-11-01")] // first 01:30 (EDT)
    [DataRow("2026-11-01T06:30:00Z", "2026-11-01")] // second 01:30 (EST)
    [DataRow("2026-11-02T04:59:59.9999999Z", "2026-11-01")] // 23:59:59 EST
    [DataRow("2026-11-02T05:00:00Z", "2026-11-02")]
    public void DateOf_NewYork_AcrossDstTransitions(string utc, string expectedLocalDate)
    {
        Assert.AreEqual(D(expectedLocalDate), LocalCalendar.DateOf(Utc(utc), NewYork));
    }

    [TestMethod]
    public void DailyTotals_SpringForwardDay_HoldsItsTwentyThreeHours()
    {
        var records = new[]
        {
            At("2026-03-08T04:59:59Z"), // Mar 7
            At("2026-03-08T05:00:00Z"),
            At("2026-03-08T06:59:59Z"),
            At("2026-03-08T07:00:00Z"),
            At("2026-03-09T03:59:59Z"),
            At("2026-03-09T04:00:00Z"), // Mar 9
        };

        var daily = DailyTotals.From(records, NewYork);

        Assert.AreEqual(1, daily[D("2026-03-07")].Count);
        Assert.AreEqual(4, daily[D("2026-03-08")].Count);
        Assert.AreEqual(1, daily[D("2026-03-09")].Count);
    }

    [TestMethod]
    public void DailyTotals_FallBackDay_HoldsItsTwentyFiveHours()
    {
        var records = new[]
        {
            At("2026-11-01T03:59:59Z"), // Oct 31
            At("2026-11-01T04:00:00Z"),
            At("2026-11-01T05:30:00Z"),
            At("2026-11-01T06:30:00Z"),
            At("2026-11-02T04:59:59Z"),
            At("2026-11-02T05:00:00Z"), // Nov 2
        };

        var daily = DailyTotals.From(records, NewYork);

        Assert.AreEqual(1, daily[D("2026-10-31")].Count);
        Assert.AreEqual(4, daily[D("2026-11-01")].Count);
        Assert.AreEqual(1, daily[D("2026-11-02")].Count);
    }

    [TestMethod]
    public void DailyTotals_JustBeforeAndAfterLocalMidnight_LandOnDifferentDays()
    {
        // 23:59:59.9999999 and 00:00:00 EST on an ordinary winter night.
        var records = new[] { At("2026-01-16T04:59:59.9999999Z", minutes: 25), At("2026-01-16T05:00:00Z", minutes: 50) };

        var daily = DailyTotals.From(records, NewYork);

        Assert.AreEqual(new DayTotal(D("2026-01-15"), 1, 25), daily[D("2026-01-15")]);
        Assert.AreEqual(new DayTotal(D("2026-01-16"), 1, 50), daily[D("2026-01-16")]);
    }

    [TestMethod]
    public void DailyTotals_SumsCountAndMinutes_AndTreatsUnspecifiedKindAsUtc()
    {
        var unspecified = DateTime.SpecifyKind(Utc("2026-01-16T04:30:00Z"), DateTimeKind.Unspecified);
        var records = new[]
        {
            new YAPA.Shared.Contracts.PomodoroEntity { DateTime = unspecified, Count = 2, DurationMin = 50 },
            At("2026-01-16T01:00:00Z", count: 1, minutes: 30),
        };

        var day = DailyTotals.From(records, NewYork)[D("2026-01-15")];

        Assert.AreEqual(3, day.Count);
        Assert.AreEqual(80, day.FocusMinutes);
    }

    [TestMethod]
    public void Range_IsContiguousAndZeroFilled()
    {
        var daily = DailyTotals.From(new[] { OnLocalDay(D("2026-11-03"), NewYork) }, NewYork);

        var range = daily.Range(D("2026-10-30"), D("2026-11-04"));

        CollectionAssert.AreEqual(
            new[] { "2026-10-30", "2026-10-31", "2026-11-01", "2026-11-02", "2026-11-03", "2026-11-04" }.Select(D).ToArray(),
            range.Select(d => d.Date).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 1, 0 }, range.Select(d => d.Count).ToArray());
        Assert.AreEqual(0, daily.Range(D("2026-11-04"), D("2026-11-03")).Count);
    }

    [DataTestMethod]
    [DataRow("2026-11-11", DayOfWeek.Monday, "2026-11-09")]
    [DataRow("2026-11-09", DayOfWeek.Monday, "2026-11-09")]
    [DataRow("2026-11-08", DayOfWeek.Monday, "2026-11-02")]
    [DataRow("2026-11-11", DayOfWeek.Sunday, "2026-11-08")]
    [DataRow("2026-11-08", DayOfWeek.Sunday, "2026-11-08")]
    [DataRow("2026-11-13", DayOfWeek.Saturday, "2026-11-07")]
    public void StartOfWeek_HonoursFirstDayOfWeek(string date, DayOfWeek first, string expected)
    {
        Assert.AreEqual(D(expected), LocalCalendar.StartOfWeek(D(date), first));
    }
}
