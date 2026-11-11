using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Tests;

[TestClass]
public class GoalForecastTests
{
    // A 50/10/30 day with four pomodoros to the cycle.
    private const int Work = 50 * 60, Short = 10 * 60, Long = 30 * 60;

    private TimerMock _timer = null!;
    private DateMock _clock = null!;
    private PomodoroEngineSettings _settings = null!;
    private PomodoroEngine _engine = null!;

    [TestInitialize]
    public void Create()
    {
        _timer = new TimerMock();
        _clock = new DateMock { DateToReturn = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc) };
        var settings = TestSettings.Create();
        _settings = TestSettings.Engine(settings, new PomodoroProfile
        {
            WorkTime = Work, BreakTime = Short, LongBreakTime = Long, PomodorosBeforeLongBreak = 4,
        });
        _engine = new PomodoroEngine(_settings, _timer, _clock, new InlineThreading(), settings, Substitute.For<IPomodoroRepository>());
    }

    private void Elapse(int seconds)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        _timer.PerformTick();
    }

    /// <summary>Works through <paramref name="pomodoros"/> pomodoros with their breaks, ending idle before the next.</summary>
    private void Complete(int pomodoros)
    {
        for (var i = 0; i < pomodoros; i++)
        {
            _engine.Start();
            Elapse(Work);
            _engine.Start();
            Elapse(_engine.BreakTime);
        }
    }

    private GoalForecast Forecast(int done, int goal) => GoalForecast.For(_engine, _settings, done, goal)!.Value;

    private static TimeSpan Minutes(int m) => TimeSpan.FromMinutes(m);

    [TestMethod]
    public void A_whole_day_from_the_start()
    {
        var f = Forecast(done: 0, goal: 12);
        Assert.AreEqual(12, f.Remaining);
        // 12 pomodoros, 11 breaks between them: two long (after the 4th and 8th), nine short.
        Assert.AreEqual(Minutes(12 * 50 + 9 * 10 + 2 * 30), f.Left);
        Assert.AreEqual(Minutes(12 * 50 + 11 * 10), f.LeftWithoutLongBreaks);
        Assert.IsTrue(f.LongBreaksMatter);
    }

    [TestMethod]
    public void No_break_is_counted_after_the_last_pomodoro()
    {
        var f = Forecast(done: 0, goal: 4);
        Assert.AreEqual(Minutes(4 * 50 + 3 * 10), f.Left, "the long break after the 4th is past the goal");
        Assert.IsFalse(f.LongBreaksMatter);
    }

    [TestMethod]
    public void Mid_pomodoro_only_its_remainder_counts()
    {
        Complete(3);
        _engine.Start();
        Elapse(20 * 60);
        var f = Forecast(done: 3, goal: 5);
        Assert.AreEqual(Minutes(30 + 30 + 50), f.Left, "this one, the long break, one more");
        Assert.AreEqual(Minutes(30 + 10 + 50), f.LeftWithoutLongBreaks);

        _engine.Pause();
        Assert.AreEqual(Minutes(30 + 30 + 50), Forecast(done: 3, goal: 5).Left, "paused: as if resumed now");
    }

    [TestMethod]
    public void During_the_long_break_cutting_it_short_saves_the_rest_of_it()
    {
        Complete(3);
        _engine.Start();
        Elapse(Work);
        _engine.Start();
        Elapse(5 * 60);
        Assert.IsTrue(_engine.IsLongBreak);

        var f = Forecast(done: 4, goal: 5);
        Assert.AreEqual(Minutes(25 + 50), f.Left);
        Assert.AreEqual(Minutes(5 + 50), f.LeftWithoutLongBreaks, "a short break's worth, less the 5 min already rested");

        Elapse(10 * 60);
        Assert.AreEqual(Minutes(15 + 50), Forecast(done: 4, goal: 5).Left);
        Assert.AreEqual(Minutes(50), Forecast(done: 4, goal: 5).LeftWithoutLongBreaks, "already longer than a short break");
    }

    [TestMethod]
    public void Waiting_after_work_the_break_is_still_ahead()
    {
        Complete(1);
        _engine.Start();
        Elapse(Work);
        Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
        Assert.AreEqual(Minutes(10 + 50), Forecast(done: 2, goal: 3).Left);
    }

    [TestMethod]
    public void After_the_long_break_the_cycle_starts_over()
    {
        Complete(3);
        _engine.Start();
        Elapse(Work);
        _engine.Start();
        Elapse(Long);
        Assert.AreEqual(PomodoroPhase.BreakEnded, _engine.Phase);

        // Pomodoros 1-4 of the new cycle, with the next long break only after the 4th.
        var f = Forecast(done: 4, goal: 9);
        Assert.AreEqual(Minutes(5 * 50 + 3 * 10 + 30), f.Left);
    }

    [TestMethod]
    public void An_early_long_break_is_forecast_as_long()
    {
        _engine.Start();
        Elapse(Work);
        _engine.StartLongBreak();
        Assert.AreEqual(Minutes(30 + 50), Forecast(done: 1, goal: 2).Left);
    }

    [TestMethod]
    public void No_forecast_without_a_goal_or_once_it_is_reached()
    {
        Assert.IsNull(GoalForecast.For(_engine, _settings, doneToday: 3, goal: 0));
        Assert.IsNull(GoalForecast.For(_engine, _settings, doneToday: 12, goal: 12));
        Assert.IsNull(GoalForecast.For(_engine, _settings, doneToday: 13, goal: 12));
    }
}
