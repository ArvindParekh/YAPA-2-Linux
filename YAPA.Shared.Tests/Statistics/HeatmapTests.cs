#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Shared.Contracts;
using YAPA.Shared.Statistics;
using static YAPA.Shared.Tests.Statistics.StatisticsTestData;

namespace YAPA.Shared.Tests.Statistics;

[TestClass]
public class HeatmapTests
{
    private static readonly DateOnly Today = D("2026-11-11"); // a Wednesday

    private static Heatmap Build(DayOfWeek first, int weeks, params PomodoroEntity[] records)
        => Heatmap.Build(DailyTotals.From(records, NewYork), Today, weeks, first);

    private static PomodoroEntity Day(string date, int count) => OnLocalDay(D(date), NewYork, count: count);

    [TestMethod]
    public void Shape_WeeksAreColumns_EndingWithTheCurrentWeek()
    {
        var map = Build(DayOfWeek.Monday, 4);

        Assert.AreEqual(4, map.Weeks.Count);
        Assert.IsTrue(map.Weeks.All(w => w.Days.Count == 7));
        Assert.AreEqual(D("2026-10-19"), map.Start);
        Assert.AreEqual(D("2026-11-15"), map.End);
        Assert.AreEqual(D("2026-11-09"), map.Weeks[^1].Start);
        CollectionAssert.AreEqual(
            new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday },
            map.RowDays.ToArray());

        // Row r of every column is the same weekday.
        for (var w = 0; w < map.Weeks.Count; w++)
            for (var r = 0; r < 7; r++)
                Assert.AreEqual(map.RowDays[r], map[w, r].Date.DayOfWeek);
    }

    [TestMethod]
    public void Shape_SundayFirst()
    {
        var map = Build(DayOfWeek.Sunday, 1);

        Assert.AreEqual(D("2026-11-08"), map.Start);
        Assert.AreEqual(DayOfWeek.Sunday, map.RowDays[0]);
        Assert.AreEqual(Today, map[0, 3].Date);
    }

    [TestMethod]
    public void DaysAfterToday_AreMarkedFuture()
    {
        var map = Build(DayOfWeek.Monday, 1);

        CollectionAssert.AreEqual(
            new[] { false, false, false, true, true, true, true },
            map.Weeks[0].Days.Select(c => c.IsFuture).ToArray());
    }

    [TestMethod]
    public void Cells_AreConsecutiveDates_AcrossDstTransitions()
    {
        // 40 weeks back from Nov 11 covers both 2026 transitions.
        var map = Build(DayOfWeek.Monday, 40, Day("2026-03-08", 2), Day("2026-11-01", 3));

        var cells = map.Weeks.SelectMany(w => w.Days).ToArray();
        for (var i = 1; i < cells.Length; i++)
            Assert.AreEqual(cells[i - 1].Date.AddDays(1), cells[i].Date);

        Assert.AreEqual(2, cells.Single(c => c.Date == D("2026-03-08")).Count);
        Assert.AreEqual(3, cells.Single(c => c.Date == D("2026-11-01")).Count);
    }

    [TestMethod]
    public void SparseData_OnePomodoroDays_StayDim()
    {
        var map = Build(DayOfWeek.Monday, 8, Day("2026-10-01", 1), Day("2026-10-20", 1), Day("2026-11-10", 1));

        var active = map.Weeks.SelectMany(w => w.Days).Where(c => c.Count > 0).ToArray();
        Assert.AreEqual(HeatmapScale.MinimumFullScale, map.FullScaleCount);
        Assert.IsTrue(active.All(c => c.Level == 1));
        Assert.IsTrue(map.Weeks.SelectMany(w => w.Days).Where(c => c.Count == 0).All(c => c.Level == 0));
    }

    [TestMethod]
    public void SparseData_SmallCountsUseDistinctLevels()
    {
        var map = Build(DayOfWeek.Monday, 2, Day("2026-11-03", 1), Day("2026-11-04", 2), Day("2026-11-05", 3), Day("2026-11-06", 4), Day("2026-11-09", 6));

        Assert.AreEqual(6, map.FullScaleCount);
        Assert.AreEqual(1, map.LevelFor(1));
        Assert.AreEqual(2, map.LevelFor(2)); // ceil(8/6)
        Assert.AreEqual(2, map.LevelFor(3)); // ceil(12/6)
        Assert.AreEqual(3, map.LevelFor(4)); // ceil(16/6)
        Assert.AreEqual(4, map.LevelFor(6));
    }

    [TestMethod]
    public void OneOutlierDay_DoesNotFlattenTheRest()
    {
        // Twenty ordinary days of 4–8 and one 20-pomodoro marathon.
        var records = Enumerable.Range(0, 20)
            .Select(i => OnLocalDay(Today.AddDays(-i - 1), NewYork, count: 4 + i % 5))
            .Append(Day("2026-11-11", 20))
            .ToArray();

        var map = Build(DayOfWeek.Monday, 6, records);

        Assert.AreEqual(8, map.FullScaleCount); // p90 of the active days, not the max
        Assert.AreEqual(4, map.LevelFor(20));
        Assert.AreEqual(2, map.LevelFor(4));
        Assert.AreEqual(4, map.LevelFor(8));
    }

    [TestMethod]
    public void Distribution_OnlyUsesDaysInsideThePeriod()
    {
        var map = Build(DayOfWeek.Monday, 2, Day("2026-01-01", 40), Day("2026-11-10", 2));

        Assert.AreEqual(HeatmapScale.MinimumFullScale, map.FullScaleCount);
        Assert.AreEqual(2, map.Weeks.SelectMany(w => w.Days).Single(c => c.Count > 0).Level);
    }

    [TestMethod]
    public void EmptyData_AllLevelZero()
    {
        var map = Build(DayOfWeek.Monday, 53);

        Assert.AreEqual(53 * 7, map.Weeks.SelectMany(w => w.Days).Count());
        Assert.IsTrue(map.Weeks.SelectMany(w => w.Days).All(c => c.Level == 0));
    }

    [TestMethod]
    public void ZeroWeeks_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => Build(DayOfWeek.Monday, 0));
    }

    [DataTestMethod]
    [DataRow(0, 4, 0)]
    [DataRow(-1, 4, 0)]
    [DataRow(1, 4, 1)]
    [DataRow(4, 4, 4)]
    [DataRow(5, 4, 4)]
    [DataRow(1, 100, 1)] // any activity is visible
    [DataRow(25, 100, 1)]
    [DataRow(26, 100, 2)]
    [DataRow(75, 100, 3)]
    [DataRow(76, 100, 4)]
    [DataRow(int.MaxValue, 4, 4)]
    public void Level_IsCeilingOfFourBands(int count, int fullScale, int expected)
    {
        Assert.AreEqual(expected, HeatmapScale.Level(count, fullScale));
    }

    [TestMethod]
    public void Level_IsMonotonicInCount()
    {
        foreach (var scale in new[] { 4, 5, 7, 13 })
        {
            var levels = Enumerable.Range(0, 30).Select(c => HeatmapScale.Level(c, scale)).ToArray();
            for (var i = 1; i < levels.Length; i++)
                Assert.IsTrue(levels[i] >= levels[i - 1]);
        }
    }

    [DataTestMethod]
    [DataRow(new int[0], 4)]
    [DataRow(new[] { 0, 0, 0 }, 4)]
    [DataRow(new[] { 1, 1, 1 }, 4)]
    [DataRow(new[] { 9 }, 9)]
    [DataRow(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 50 }, 9)] // nearest rank: 9th of 10
    public void FullScale_IsFlooredP90OfActiveDays(int[] counts, int expected)
    {
        Assert.AreEqual(expected, HeatmapScale.FullScaleFor(counts));
    }
}
