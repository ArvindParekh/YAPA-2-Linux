#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Shared.Contracts;
using YAPA.Shared.Statistics;
using static YAPA.Shared.Tests.Statistics.StatisticsTestData;

namespace YAPA.Shared.Tests.Statistics;

[TestClass]
public class PomodoroStatisticsTests
{
    // Wednesday 2026-11-11, 22:00 EST. Already Nov 12 in UTC, which is exactly the trap.
    private static readonly DateTime Now = Utc("2026-11-12T03:00:00Z");
    private static readonly DateOnly Today = D("2026-11-11");

    private static StatisticsReport Compute(IEnumerable<PomodoroEntity> records, string? profile = null, DayOfWeek first = DayOfWeek.Monday, DateTime? now = null)
        => PomodoroStatistics.Compute(records, new StatisticsQuery(NewYork, now ?? Now) { Profile = profile, FirstDayOfWeek = first });

    private static PomodoroEntity[] OnDays(params string[] dates) => dates.Select(d => OnLocalDay(D(d), NewYork)).ToArray();

    [TestMethod]
    public void EmptyHistory_YieldsZerosEverywhere()
    {
        var report = Compute(Array.Empty<PomodoroEntity>());

        Assert.AreEqual(Today, report.Today);
        Assert.AreEqual(0, report.TodayTotal.Count);
        Assert.AreEqual(0, report.ThisWeek.Count);
        Assert.AreEqual(0, report.ThisMonth.Count);
        Assert.AreEqual(0, report.AllTime.ActiveDays);
        Assert.IsTrue(report.CurrentStreak.IsEmpty);
        Assert.IsTrue(report.BestStreak.IsEmpty);
        Assert.AreEqual(0, report.AverageCountPerActiveDay);
        Assert.AreEqual(0, report.AverageMinutesPerActiveDay);
        Assert.IsNull(report.BestDay);
        Assert.AreEqual(0, report.Daily.Days.Count);
        Assert.AreEqual(0, report.Profiles.Count);
        Assert.AreEqual(26, report.Heatmap.Weeks.Count);
        Assert.IsTrue(report.Heatmap.Weeks.SelectMany(w => w.Days).All(c => c.Count == 0 && c.Level == 0));
    }

    [TestMethod]
    public void Today_IsTheLocalDate_NotTheUtcDate()
    {
        var records = new[]
        {
            At("2026-11-12T02:00:00Z"), // Nov 11, 21:00 EST: today
            At("2026-11-11T04:59:00Z"), // Nov 10, 23:59 EST: yesterday
            At("2026-11-11T05:00:00Z"), // Nov 11, 00:00 EST: today
        };

        var report = Compute(records);

        Assert.AreEqual(2, report.TodayTotal.Count);
        Assert.AreEqual(1, report.Daily[D("2026-11-10")].Count);
    }

    [TestMethod]
    public void WeekAndMonthTotals_HonourFirstDayOfWeek()
    {
        var records = OnDays("2026-10-31", "2026-11-01", "2026-11-08", "2026-11-09", "2026-11-11");

        var monday = Compute(records);
        var sunday = Compute(records, first: DayOfWeek.Sunday);

        Assert.AreEqual(new PeriodTotal(D("2026-11-09"), D("2026-11-15"), 2, 50, 2), monday.ThisWeek);
        Assert.AreEqual(new PeriodTotal(D("2026-11-08"), D("2026-11-14"), 3, 75, 3), sunday.ThisWeek);
        Assert.AreEqual(new PeriodTotal(D("2026-11-01"), D("2026-11-30"), 4, 100, 4), monday.ThisMonth);
        Assert.AreEqual(new PeriodTotal(Today, Today, 1, 25, 1), monday.TodayTotal);
        Assert.AreEqual(5, monday.AllTime.Count);
    }

    [TestMethod]
    public void CurrentStreak_CountsBackFromToday_WhenTodayIsActive()
    {
        var report = Compute(OnDays("2026-11-05", "2026-11-07", "2026-11-08", "2026-11-09", "2026-11-10", "2026-11-11"));

        Assert.AreEqual(new Streak(D("2026-11-07"), 5), report.CurrentStreak);
        Assert.AreEqual(Today, report.CurrentStreak.End);
    }

    [TestMethod]
    public void CurrentStreak_CountsBackFromYesterday_WhenTodayHasNoneYet()
    {
        var report = Compute(OnDays("2026-11-08", "2026-11-09", "2026-11-10"));

        Assert.AreEqual(new Streak(D("2026-11-08"), 3), report.CurrentStreak);
    }

    [TestMethod]
    public void CurrentStreak_IsBroken_WhenYesterdayIsAlsoEmpty()
    {
        var report = Compute(OnDays("2026-11-07", "2026-11-08", "2026-11-09"));

        Assert.IsTrue(report.CurrentStreak.IsEmpty);
        Assert.AreEqual(3, report.BestStreak.Length);
    }

    [TestMethod]
    public void CurrentStreak_RecordJustAfterLocalMidnight_CountsForToday()
    {
        // 00:00:30 EST on Nov 11 continues a streak that ended on Nov 10.
        var records = OnDays("2026-11-09", "2026-11-10").Append(At("2026-11-11T05:00:30Z")).ToArray();

        var report = Compute(records, now: Utc("2026-11-11T05:01:00Z"));

        Assert.AreEqual(new Streak(D("2026-11-09"), 3), report.CurrentStreak);
    }

    [TestMethod]
    public void CurrentStreak_RecordJustBeforeLocalMidnight_DoesNotCountForToday()
    {
        // 23:59:30 EST on Nov 10; at 00:01 on Nov 11 today is still empty, streak runs to yesterday.
        var records = OnDays("2026-11-09").Append(At("2026-11-11T04:59:30Z")).ToArray();

        var report = Compute(records, now: Utc("2026-11-11T05:01:00Z"));

        Assert.AreEqual(0, report.TodayTotal.Count);
        Assert.AreEqual(new Streak(D("2026-11-09"), 2), report.CurrentStreak);
    }

    [TestMethod]
    public void Streaks_SpanDstTransitions()
    {
        var report = Compute(
            OnDays("2026-03-07", "2026-03-08", "2026-03-09", "2026-10-31", "2026-11-01", "2026-11-02", "2026-11-03"),
            now: Utc("2026-11-03T17:00:00Z"));

        Assert.AreEqual(new Streak(D("2026-10-31"), 4), report.CurrentStreak);
        Assert.AreEqual(new Streak(D("2026-10-31"), 4), report.BestStreak);
    }

    [TestMethod]
    public void BestStreak_PicksLongest_AndTheLaterRunOnATie()
    {
        var longest = Compute(OnDays("2026-01-01", "2026-01-02", "2026-01-03", "2026-01-04", "2026-02-01", "2026-02-02"));
        var tie = Compute(OnDays("2026-01-01", "2026-01-02", "2026-02-01", "2026-02-02"));

        Assert.AreEqual(new Streak(D("2026-01-01"), 4), longest.BestStreak);
        Assert.AreEqual(D("2026-01-04"), longest.BestStreak.End);
        Assert.AreEqual(new Streak(D("2026-02-01"), 2), tie.BestStreak);
    }

    [TestMethod]
    public void ZeroCountRecords_DoNotMakeADayActive()
    {
        var records = OnDays("2026-11-09", "2026-11-11").Append(OnLocalDay(D("2026-11-10"), NewYork, count: 0)).ToArray();

        var report = Compute(records);

        Assert.AreEqual(new Streak(Today, 1), report.CurrentStreak);
        Assert.AreEqual(2, report.AllTime.ActiveDays);
    }

    [TestMethod]
    public void AverageAndBestDay()
    {
        var records = new[]
        {
            OnLocalDay(D("2026-11-01"), NewYork, count: 4, minutes: 100),
            OnLocalDay(D("2026-11-03"), NewYork, count: 1, minutes: 25),
            OnLocalDay(D("2026-11-05"), NewYork, count: 4, minutes: 200),
        };

        var report = Compute(records);

        Assert.AreEqual(3.0, report.AverageCountPerActiveDay, 1e-9);
        Assert.AreEqual(325.0 / 3, report.AverageMinutesPerActiveDay, 1e-9);
        Assert.AreEqual(new DayTotal(D("2026-11-05"), 4, 200), report.BestDay); // most recent of the tie
    }

    [TestMethod]
    public void ProfileFilter_RestrictsTotals_ButNotTheProfileList()
    {
        var records = new[]
        {
            OnLocalDay(Today, NewYork, profile: "Work"),
            OnLocalDay(Today, NewYork, profile: "Work"),
            OnLocalDay(Today, NewYork, profile: "study"),
            OnLocalDay(D("2026-11-10"), NewYork, profile: "study"),
            OnLocalDay(D("2026-11-10"), NewYork, profile: null), // pre-profile record
            OnLocalDay(D("2026-11-10"), NewYork, profile: ""),
        };

        var work = Compute(records, profile: "Work");
        var all = Compute(records, profile: "");
        var caseMismatch = Compute(records, profile: "work");

        Assert.AreEqual(2, work.TodayTotal.Count);
        Assert.AreEqual(2, work.AllTime.Count);
        Assert.AreEqual(new Streak(Today, 1), work.CurrentStreak);
        Assert.AreEqual(6, all.AllTime.Count);
        Assert.AreEqual(new Streak(D("2026-11-10"), 2), all.CurrentStreak);
        Assert.AreEqual(0, caseMismatch.AllTime.Count);
        CollectionAssert.AreEqual(new[] { "study", "Work" }, work.Profiles.ToArray());
        CollectionAssert.AreEqual(work.Profiles.ToArray(), all.Profiles.ToArray());
    }

    [TestMethod]
    public void Compute_EnumeratesALazySourceOnce()
    {
        var enumerations = 0;
        IEnumerable<PomodoroEntity> Source()
        {
            enumerations++;
            yield return OnLocalDay(Today, NewYork, profile: "Work");
        }

        var report = Compute(Source());

        Assert.AreEqual(1, enumerations);
        Assert.AreEqual(1, report.TodayTotal.Count);
        Assert.AreEqual(1, report.Profiles.Count);
    }
}
