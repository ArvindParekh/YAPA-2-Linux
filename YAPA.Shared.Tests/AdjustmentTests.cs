#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Tests;

/// <summary>The engine operations the assistant relies on, and the schedule it plans with.</summary>
[TestClass]
public class AdjustmentTests
{
    private const int Work = 25 * 60;
    private const int ShortBreak = 5 * 60;
    private const int LongBreak = 15 * 60;

    private TimerMock _timer = null!;
    private DateMock _clock = null!;
    private ISettings _settings = null!;
    private PomodoroEngineSettings _engineSettings = null!;
    private PomodoroEngine _engine = null!;
    private List<PomodoroTransition> _transitions = null!;

    private void Create(int cycle = 4)
    {
        _timer = new TimerMock();
        _clock = new DateMock { DateToReturn = new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc) };
        _settings = TestSettings.Create();
        _engineSettings = TestSettings.Engine(_settings, new PomodoroProfile
        {
            WorkTime = Work,
            BreakTime = ShortBreak,
            LongBreakTime = LongBreak,
            PomodorosBeforeLongBreak = cycle,
        });
        _engine = new PomodoroEngine(_engineSettings, _timer, _clock, new InlineThreading(), _settings, Substitute.For<IPomodoroRepository>());
        _transitions = [];
        _engine.Transitioned += t => _transitions.Add(t);
    }

    private void Elapse(int seconds)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        _timer.PerformTick();
    }

    // ── ExtendInterval ───────────────────────────────────────────────────────

    [TestMethod]
    public void Extending_a_pomodoro_moves_its_end_and_is_recorded_at_its_real_length()
    {
        Create();
        var recorded = new List<int>();
        _engine.OnPomodoroCompleted += () => recorded.Add(_engine.LastPomodoroLength);
        _engine.Start();
        Elapse(10 * 60);

        _engine.ExtendInterval(5 * 60);
        Assert.AreEqual(Work + 300, _engine.CurrentIntervalLength);
        Assert.AreEqual(Work + 300 - 600, _engine.Remaining);
        Assert.AreEqual(PomodoroTransitionKind.Adjusted, _transitions.Last().Kind);

        Elapse(Work - 600); // where it would have ended
        Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
        Elapse(300);
        Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
        CollectionAssert.AreEqual(new[] { Work + 300 }, recorded);
    }

    [TestMethod]
    public void The_extension_belongs_to_one_interval()
    {
        Create();
        _engine.Start();
        _engine.ExtendInterval(120);
        Elapse(Work + 120);
        _engine.Start(); // the break

        Assert.AreEqual(ShortBreak, _engine.CurrentIntervalLength);
        Assert.AreEqual(0, _engine.ExtraTime);
    }

    [TestMethod]
    public void Shortening_below_what_has_run_ends_the_interval_now_not_in_the_past()
    {
        Create();
        _engine.Start();
        Elapse(20 * 60);

        _engine.ExtendInterval(-15 * 60); // would end at minute 10, ten minutes ago
        Assert.AreEqual(20 * 60, _engine.CurrentIntervalLength);
        Assert.AreEqual(TimeSpan.Zero, _timer.LastScheduled, "ends on the next tick");

        Elapse(0);
        Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
        Assert.AreEqual(0, _engine.Overrun, "no overrun piled up from a past end");
    }

    [TestMethod]
    public void A_paused_break_can_be_extended_and_nothing_idle_can()
    {
        Create();
        _engine.ExtendInterval(60);
        Assert.AreEqual(0, _transitions.Count);
        Assert.IsFalse(_engine.CanAdjustInterval);

        _engine.Start();
        Elapse(Work);
        _engine.Start();
        Elapse(60);
        _engine.Pause();
        Assert.IsTrue(_engine.CanAdjustInterval);
        _engine.ExtendInterval(10 * 60);

        Assert.AreEqual(ShortBreak + 600, _engine.CurrentIntervalLength);
        Assert.AreEqual(ShortBreak + 600 - 60, _engine.Remaining);
    }

    [TestMethod]
    public void An_extension_survives_a_restart()
    {
        Create();
        _engine.Start();
        Elapse(60);
        _engine.ExtendInterval(300);
        var snapshot = _engine.GetSnapshot();

        Create();
        _engine.LoadSnapshot(snapshot);
        Assert.AreEqual(Work + 300, _engine.CurrentIntervalLength);
        Assert.AreEqual(Work + 300 - 60, _engine.Remaining);
    }

    // ── SetElapsed ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Setting_elapsed_time_backdates_a_pomodoro_started_late()
    {
        Create();
        _engine.Start();
        _engine.SetElapsed(10 * 60);
        Assert.AreEqual(10 * 60, _engine.Elapsed);

        Elapse(30);
        Assert.AreEqual(10 * 60 + 30, _engine.Elapsed);
        Assert.AreEqual(PomodoroTransitionKind.Adjusted, _transitions.Last().Kind);
    }

    [TestMethod]
    public void Setting_elapsed_past_the_end_completes_it_on_the_next_tick()
    {
        Create();
        _engine.Start();
        _engine.SetElapsed(Work + 999);
        Assert.AreEqual(Work, _engine.Elapsed, "clamped to the interval");

        Elapse(0);
        Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
    }

    [TestMethod]
    public void Setting_elapsed_while_paused_keeps_it_paused()
    {
        Create();
        _engine.Start();
        Elapse(60);
        _engine.Pause();
        _engine.SetElapsed(300);
        Elapse(100);

        Assert.AreEqual(PomodoroPhase.Pause, _engine.Phase);
        Assert.AreEqual(300, _engine.Elapsed);
    }

    // ── MoveTo ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Moving_in_the_cycle_records_nothing_and_waits_idle()
    {
        Create();
        var completed = 0;
        _engine.OnPomodoroCompleted += () => completed++;

        _engine.MoveTo(4, cycle: 2);

        Assert.AreEqual(4, _engine.Index);
        Assert.AreEqual(2, _engine.Cycle);
        Assert.IsTrue(_engine.IsLongBreak, "the fourth of four is followed by the long break");
        Assert.AreEqual(PomodoroPhase.NotStarted, _engine.Phase);
        Assert.AreEqual(0, completed);
        Assert.AreEqual(PomodoroTransitionKind.Moved, _transitions.Last().Kind);
    }

    [TestMethod]
    public void Moving_is_refused_while_an_interval_is_under_way()
    {
        Create();
        _engine.Start();
        _engine.MoveTo(3);
        Assert.AreEqual(1, _engine.Index);
        Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
    }

    [TestMethod]
    public void A_moved_position_is_clamped_to_the_cycle_and_survives_a_restart()
    {
        Create();
        _engine.MoveTo(9);
        Assert.AreEqual(4, _engine.Index);

        _engine.MoveTo(3, cycle: 3);
        var snapshot = _engine.GetSnapshot();
        Create();
        _engine.LoadSnapshot(snapshot);
        Assert.AreEqual(3, _engine.Index);
        Assert.AreEqual(3, _engine.Cycle);
    }

    [TestMethod]
    public void The_session_count_round_trips_in_process_but_is_never_saved()
    {
        Create();
        _engine.MarkDone();
        var snapshot = _engine.GetSnapshot();
        Assert.AreEqual(1, snapshot.CompletedThisSession);

        var json = new NewtonsoftJson().Serialize(snapshot);
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("CompletedThisSession"));
    }

    // ── SchedulePlan ─────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 11, 9, 14, 0, 0);

    [TestMethod]
    public void From_idle_the_plan_alternates_and_puts_the_long_break_after_the_cycle()
    {
        Create(cycle: 2);
        var plan = SchedulePlan.Build(_engine, _engineSettings, Now, until: null, pomodoros: 3);

        CollectionAssert.AreEqual(
            new[] { PlannedKind.Pomodoro, PlannedKind.ShortBreak, PlannedKind.Pomodoro, PlannedKind.LongBreak, PlannedKind.Pomodoro },
            plan.Select(p => p.Kind).ToArray());
        Assert.AreEqual(Now, plan[0].Start);
        Assert.AreEqual(Now.AddMinutes(25 + 5 + 25 + 15 + 25), plan[^1].End);
    }

    [TestMethod]
    public void The_pomodoro_under_way_ends_when_the_timer_says_and_counts_toward_the_plan()
    {
        Create();
        _engine.Start();
        Elapse(10 * 60);

        var plan = SchedulePlan.Build(_engine, _engineSettings, Now, until: null, pomodoros: 2, doneToday: 3);
        Assert.IsTrue(plan[0].InProgress);
        Assert.AreEqual(Now.AddMinutes(15), plan[0].End);
        Assert.AreEqual(4, plan[0].Number);
        Assert.AreEqual(5, plan.Last(p => p.Kind == PlannedKind.Pomodoro).Number);
        Assert.AreEqual(Now.AddMinutes(15 + 5 + 25), plan[^1].End);
    }

    [TestMethod]
    public void A_cutoff_counts_only_pomodoros_finished_by_then()
    {
        Create();
        // From 14:00 the pomodoros end 14:25, 14:55 and 15:25; the fourth runs 15:30–15:55, past 15:30.
        var plan = SchedulePlan.Build(_engine, _engineSettings, Now, until: Now.AddHours(1.5), pomodoros: null);

        var finished = plan.Where(p => p.Kind == PlannedKind.Pomodoro && !p.CutOff).ToList();
        Assert.AreEqual(3, finished.Count);
        Assert.AreEqual(Now.AddMinutes(85), finished[^1].End);
        Assert.IsTrue(plan[^1].CutOff, "the plan stops at the first interval past the cutoff");
    }

    [TestMethod]
    public void After_a_pomodoro_ended_the_due_break_comes_first()
    {
        Create(cycle: 1);
        _engine.Start();
        Elapse(Work);

        var plan = SchedulePlan.Build(_engine, _engineSettings, Now, until: null, pomodoros: 1);
        Assert.AreEqual(PlannedKind.LongBreak, plan[0].Kind);
        Assert.AreEqual(Now.AddMinutes(15), plan[0].End);
        Assert.AreEqual(PlannedKind.Pomodoro, plan[1].Kind);
    }

    [TestMethod]
    public void Without_long_breaks_every_break_is_short()
    {
        Create(cycle: 1);
        var plan = SchedulePlan.Build(_engine, _engineSettings, Now, until: null, pomodoros: 3, longBreaks: false);
        Assert.IsFalse(plan.Any(p => p.Kind == PlannedKind.LongBreak));
        Assert.AreEqual(Now.AddMinutes(25 * 3 + 5 * 2), plan[^1].End);
    }

    [TestMethod]
    public void Idle_plans_can_start_later()
    {
        Create();
        var plan = SchedulePlan.Build(_engine, _engineSettings, Now, until: null, pomodoros: 1, startAt: Now.AddHours(1));
        Assert.AreEqual(Now.AddHours(1), plan[0].Start);
    }
}
