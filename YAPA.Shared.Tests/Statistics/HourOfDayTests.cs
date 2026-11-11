#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Shared.Statistics;
using static YAPA.Shared.Tests.Statistics.StatisticsTestData;

namespace YAPA.Shared.Tests.Statistics;

[TestClass]
public class HourOfDayTests
{
    // New York is UTC-5 in November.
    private static readonly DateOnly Today = D("2026-11-11");

    [TestMethod]
    public void A_pomodoro_counts_in_the_hours_it_ran_not_the_hour_it_ended()
    {
        // Ends 10:05 local after 50 minutes: 45 minutes at 9, 5 at 10.
        var h = HourOfDay.Build([At("2026-11-11T15:05:00Z", minutes: 50)], NewYork, Today, days: 28);
        Assert.AreEqual(45, h.Minutes[9], 1e-9);
        Assert.AreEqual(5, h.Minutes[10], 1e-9);
        Assert.AreEqual(50, h.Minutes.Sum(), 1e-9);
        Assert.AreEqual(9, h.PeakHour);
    }

    [TestMethod]
    public void Hours_add_up_across_days_and_the_window_is_respected()
    {
        var h = HourOfDay.Build(
        [
            At("2026-11-11T15:00:00Z", minutes: 50), // 9:10-10:00 today
            At("2026-11-10T15:00:00Z", minutes: 50), // the same, yesterday
            At("2026-10-15T14:00:00Z", minutes: 50), // the window's first day (EDT, UTC-4, in October)
            At("2026-10-14T14:00:00Z", minutes: 50), // the day before it: outside
        ], NewYork, Today, days: 28);
        Assert.AreEqual(150, h.Minutes[9], 1e-9);
        Assert.AreEqual(28, h.Days);
    }

    [TestMethod]
    public void A_pomodoro_across_midnight_splits_between_the_hours()
    {
        // 23:40 to 00:30 local.
        var h = HourOfDay.Build([At("2026-11-11T05:30:00Z", minutes: 50)], NewYork, Today, days: 28);
        Assert.AreEqual(20, h.Minutes[23], 1e-9);
        Assert.AreEqual(30, h.Minutes[0], 1e-9);
    }

    [TestMethod]
    public void Daylight_saving_changes_count_real_minutes_only()
    {
        // 2026-03-08 New York springs forward at 2:00 (EST) to 3:00 (EDT): 1:40 EST to 3:10 EDT is 30 minutes.
        var spring = HourOfDay.Build([At("2026-03-08T07:10:00Z", minutes: 30)], NewYork, D("2026-03-08"), days: 1);
        Assert.AreEqual(20, spring.Minutes[1], 1e-9);
        Assert.AreEqual(0, spring.Minutes[2], 1e-9, "2:00-3:00 didn't happen");
        Assert.AreEqual(10, spring.Minutes[3], 1e-9);

        // 2026-11-01 falls back at 2:00 EDT to 1:00 EST: 1:40 EDT to 1:10 EST is 30 minutes, all in hour 1.
        var fall = HourOfDay.Build([At("2026-11-01T06:10:00Z", minutes: 30)], NewYork, D("2026-11-01"), days: 1);
        Assert.AreEqual(30, fall.Minutes[1], 1e-9);
        Assert.AreEqual(30, fall.Minutes.Sum(), 1e-9);
    }

    [TestMethod]
    public void The_profile_filter_applies()
    {
        var h = HourOfDay.Build(
        [
            At("2026-11-11T15:00:00Z", minutes: 25, profile: "Study"),
            At("2026-11-11T20:00:00Z", minutes: 25, profile: "Work"),
        ], NewYork, Today, days: 28, profile: "Work");
        Assert.AreEqual(0, h.Minutes[9], 1e-9);
        Assert.AreEqual(25, h.Minutes[14], 1e-9);
    }

    [TestMethod]
    public void Records_without_a_duration_count_as_a_classic_pomodoro()
    {
        var h = HourOfDay.Build([At("2026-11-11T15:30:00Z", minutes: 0)], NewYork, Today, days: 28);
        Assert.AreEqual(25, h.Minutes[10], 1e-9);
    }

    [TestMethod]
    public void Empty_history_has_no_peak()
    {
        var h = HourOfDay.Build([], NewYork, Today, days: 28);
        Assert.IsTrue(h.IsEmpty);
        Assert.IsNull(h.PeakHour);
        Assert.AreEqual(24, h.Minutes.Count);
    }
}
