using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Shared.Tests
{
    [TestClass]
    public class EngineTests
    {
        private const int Work = 25 * 60;
        private const int ShortBreak = 5 * 60;
        private const int LongBreak = 15 * 60;

        private TimerMock _timer;
        private DateMock _clock;
        private ISettings _settings;
        private PomodoroEngineSettings _engineSettings;
        private IPomodoroRepository _repository;
        private PomodoroEngine _engine;
        private List<PomodoroTransition> _transitions;

        private void Create(int cycle = 4, bool autoBreak = false, bool autoWork = false)
        {
            _timer = new TimerMock();
            _clock = new DateMock { DateToReturn = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc) };
            _settings = TestSettings.Create();
            _engineSettings = TestSettings.Engine(_settings, new PomodoroProfile
            {
                WorkTime = Work,
                BreakTime = ShortBreak,
                LongBreakTime = LongBreak,
                PomodorosBeforeLongBreak = cycle,
                AutoStartBreak = autoBreak,
                AutoStartWork = autoWork,
            });
            _repository = Substitute.For<IPomodoroRepository>();
            _engine = new PomodoroEngine(_engineSettings, _timer, _clock, new InlineThreading(), _settings, _repository);
            _transitions = new List<PomodoroTransition>();
            _engine.Transitioned += t => _transitions.Add(t);
        }

        private void Elapse(int seconds)
        {
            _clock.Advance(TimeSpan.FromSeconds(seconds));
            _timer.PerformTick();
        }

        private PomodoroTransitionKind LastKind => _transitions.Last().Kind;

        [TestMethod]
        public void Start_from_idle_begins_work()
        {
            Create();
            _engine.Start();

            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Assert.AreEqual(PomodoroTransitionKind.WorkStarted, LastKind);
            Assert.IsTrue(_timer.IsRunning);
        }

        [TestMethod]
        public void Start_while_running_is_a_no_op()
        {
            Create();
            _engine.Start();
            Elapse(10);
            _engine.Start();

            Assert.AreEqual(1, _transitions.Count);
            Assert.AreEqual(10, _engine.Elapsed);
        }

        [TestMethod]
        public void Pause_and_resume_work_are_distinct_transitions()
        {
            Create();
            _engine.Start();
            Elapse(60);
            _engine.Pause();
            Assert.AreEqual(PomodoroTransitionKind.Paused, LastKind);
            Assert.AreEqual(PomodoroPhase.Work, _engine.PausedPhase);

            _clock.Advance(TimeSpan.FromMinutes(10)); // paused time doesn't count
            _engine.Start();
            Assert.AreEqual(PomodoroTransitionKind.Resumed, LastKind);
            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Assert.AreEqual(60, _engine.Elapsed);
        }

        [TestMethod]
        public void Break_can_be_paused_and_resumes_as_a_break()
        {
            Create();
            _engine.Start();
            Elapse(Work);
            _engine.Start();
            Elapse(30);

            _engine.Pause();
            Assert.AreEqual(PomodoroPhase.Pause, _engine.Phase);
            Assert.AreEqual(PomodoroPhase.Break, _engine.PausedPhase);
            Assert.AreEqual(ShortBreak, _engine.CurrentIntervalLength);
            Assert.AreEqual(ShortBreak - 30, _engine.Remaining);

            _engine.Start();
            Assert.AreEqual(PomodoroPhase.Break, _engine.Phase);
            Elapse(ShortBreak - 30);
            Assert.AreEqual(PomodoroPhase.BreakEnded, _engine.Phase);
        }

        [TestMethod]
        public void Completing_work_raises_completion_exactly_once()
        {
            Create();
            var completions = 0;
            _engine.OnPomodoroCompleted += () => completions++;

            _engine.Start();
            Elapse(Work - 1);
            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Elapse(1);
            _timer.PerformTick();

            Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
            Assert.AreEqual(1, completions);
            Assert.AreEqual(1, _transitions.Count(t => t.Kind == PomodoroTransitionKind.WorkCompleted));
            Assert.IsTrue(_timer.IsRunning, "keeps ticking to count the overrun");
        }

        [TestMethod]
        public void Stopping_work_abandons_it_without_recording()
        {
            Create();
            var completions = 0;
            _engine.OnPomodoroCompleted += () => completions++;

            _engine.Start();
            Elapse(600);
            _engine.Stop();

            Assert.AreEqual(PomodoroPhase.NotStarted, _engine.Phase);
            Assert.AreEqual(1, _engine.Index, "an abandoned pomodoro is retried, not skipped");
            Assert.AreEqual(0, _engine.Elapsed);
            Assert.AreEqual(0, completions);
            Assert.AreEqual(PomodoroTransitionKind.Stopped, LastKind);
        }

        [TestMethod]
        public void Stopping_a_break_moves_on_to_the_next_pomodoro()
        {
            Create();
            _engine.Start();
            Elapse(Work);
            _engine.Start();
            Elapse(60);
            _engine.Stop();

            Assert.AreEqual(PomodoroPhase.NotStarted, _engine.Phase);
            Assert.AreEqual(2, _engine.Index);
        }

        [TestMethod]
        public void Skip_after_work_starts_next_pomodoro_immediately()
        {
            Create();
            _engine.Start();
            Elapse(Work);
            _engine.Skip();

            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Assert.AreEqual(2, _engine.Index);
            Assert.AreEqual(PomodoroTransitionKind.WorkStarted, LastKind);
        }

        [TestMethod]
        public void Skip_during_work_does_nothing()
        {
            Create();
            _engine.Start();
            Elapse(10);
            _engine.Skip();

            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Assert.AreEqual(1, _engine.Index);
        }

        [TestMethod]
        public void Long_break_comes_after_the_last_pomodoro_of_the_cycle()
        {
            Create(cycle: 2);
            _engine.Start();
            Elapse(Work);
            Assert.IsFalse(_engine.IsLongBreak);
            _engine.Start();
            Assert.AreEqual(ShortBreak, _engine.CurrentIntervalLength);
            Elapse(ShortBreak);
            _engine.Start();
            Assert.AreEqual(2, _engine.Index);
            Elapse(Work);
            Assert.IsTrue(_engine.IsLongBreak);
            _engine.Start();
            Assert.AreEqual(LongBreak, _engine.CurrentIntervalLength);
            Elapse(LongBreak);
            _engine.Start();
            Assert.AreEqual(1, _engine.Index, "cycle wraps after the long break");
        }

        [TestMethod]
        public void Cycle_length_change_applies_without_restart()
        {
            Create(cycle: 4);
            _engine.Start();
            Elapse(Work);
            _engine.Skip(); // index 2
            Elapse(Work);
            _engine.Skip(); // index 3

            _engineSettings.PomodorosBeforeLongBreak = 2;
            Assert.AreEqual(2, _engine.Index, "index clamps into the shorter cycle");
            Elapse(Work);
            Assert.IsTrue(_engine.IsLongBreak);
        }

        [TestMethod]
        public void A_cycle_of_one_makes_every_break_long()
        {
            Create(cycle: 1);
            _engine.Start();
            Elapse(Work);
            _engine.Start();
            Assert.AreEqual(LongBreak, _engine.CurrentIntervalLength);
        }

        [TestMethod]
        public void Auto_start_break_waits_for_the_delay()
        {
            Create(autoBreak: true);
            _engine.Start();
            Elapse(Work);
            Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
            Assert.IsTrue(_timer.IsRunning, "timer keeps ticking to fire the auto-start");

            Elapse(1);
            Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
            Elapse(1);
            Assert.AreEqual(PomodoroPhase.Break, _engine.Phase);

            var started = _transitions.Last();
            Assert.AreEqual(PomodoroTransitionKind.BreakStarted, started.Kind);
            Assert.IsTrue(started.Automatic);
        }

        [TestMethod]
        public void Stopping_during_auto_start_delay_cancels_it()
        {
            Create(autoBreak: true);
            _engine.Start();
            Elapse(Work);
            _engine.Stop();
            Elapse(5);

            Assert.AreEqual(PomodoroPhase.NotStarted, _engine.Phase);
            Assert.AreEqual(2, _engine.Index);
        }

        [TestMethod]
        public void Auto_start_work_follows_break_completion()
        {
            Create(autoWork: true);
            _engine.Start();
            Elapse(Work);
            _engine.Start();
            Elapse(ShortBreak);

            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Assert.AreEqual(2, _engine.Index);
            CollectionAssert.AreEqual(
                new[] { PomodoroTransitionKind.BreakCompleted, PomodoroTransitionKind.WorkStarted },
                _transitions.Skip(_transitions.Count - 2).Select(t => t.Kind).ToArray());
            Assert.IsTrue(_transitions.Last().Automatic);
        }

        [TestMethod]
        public void Time_asleep_does_not_count_towards_the_pomodoro()
        {
            Create();
            _engine.Start();
            Elapse(300);
            _clock.Sleep(TimeSpan.FromHours(2));
            _timer.PerformTick();

            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Assert.AreEqual(300, _engine.Elapsed);
        }

        [TestMethod]
        public void Reset_returns_to_first_pomodoro()
        {
            Create();
            _engine.Start();
            Elapse(Work);
            _engine.Skip();
            Assert.AreEqual(2, _engine.Index);

            _engine.Reset();
            Assert.AreEqual(PomodoroPhase.NotStarted, _engine.Phase);
            Assert.AreEqual(1, _engine.Index);
            Assert.AreEqual(PomodoroTransitionKind.Reset, LastKind);
        }

        [TestMethod]
        public void Count_backwards_displays_remaining_time()
        {
            Create();
            _engineSettings.CountBackwards = true;
            Assert.AreEqual(Work, _engine.DisplayValue);
            _engine.Start();
            Elapse(61);
            Assert.AreEqual(Work - 61, _engine.DisplayValue);
        }

        [TestMethod]
        public void Snapshot_round_trip_preserves_paused_break()
        {
            Create();
            _engine.Start();
            Elapse(Work);
            _engine.Start();
            Elapse(42);
            _engine.Pause();
            var snapshot = _engine.GetSnapshot();

            Create();
            _engine.LoadSnapshot(snapshot);

            Assert.AreEqual(PomodoroPhase.Pause, _engine.Phase);
            Assert.AreEqual(PomodoroPhase.Break, _engine.PausedPhase);
            Assert.AreEqual(42, _engine.Elapsed);
            _engine.Start();
            Assert.AreEqual(PomodoroPhase.Break, _engine.Phase);
        }

        [TestMethod]
        public void Snapshot_of_running_work_keeps_running_from_where_it_was()
        {
            Create();
            _engine.Start();
            Elapse(100);
            var snapshot = _engine.GetSnapshot();

            Create();
            _engine.LoadSnapshot(snapshot);
            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Elapse(10);
            Assert.AreEqual(110, _engine.Elapsed);
        }

        [TestMethod]
        public void Legacy_snapshot_without_paused_phase_resumes_as_work()
        {
            Create();
            _engine.LoadSnapshot(new PomodoroEngineSnapshot { Phase = PomodoroPhase.Pause, PausedTime = 90, PomodoroIndex = 3 });
            Assert.AreEqual(PomodoroPhase.Work, _engine.PausedPhase);
            Assert.AreEqual(3, _engine.Index);
            Assert.AreEqual(Work - 90, _engine.Remaining);
        }

        [TestMethod]
        public void Missing_active_profile_falls_back_instead_of_crashing()
        {
            var settings = TestSettings.Create();
            var engineSettings = TestSettings.Engine(settings, PomodoroProfile.Default(), "Pomodoro");
            engineSettings.ActiveProfile = "Deleted";

            Assert.AreEqual("Pomodoro", engineSettings.ActiveProfile);
            Assert.AreEqual(25 * 60, engineSettings.WorkTime);
        }

        [TestMethod]
        public void Default_profile_is_not_shared_between_reads()
        {
            var engineSettings = new PomodoroEngineSettings(TestSettings.Create());
            engineSettings.Profiles[PomodoroEngineSettings.DefaultProfileName].WorkTime = 1;
            Assert.AreEqual(25 * 60, engineSettings.WorkTime);
        }

        // ── Long break on demand ─────────────────────────────────────────────

        [TestMethod]
        public void Long_break_after_two_pomodoros_then_the_cycle_starts_over()
        {
            Create(cycle: 4);
            _engine.Start(); Elapse(Work); _engine.Skip();   // pomodoro 2
            Elapse(Work);
            _engine.Start(); Elapse(ShortBreak);            // "break over" after 2 sessions
            Assert.AreEqual(PomodoroPhase.BreakEnded, _engine.Phase);
            Assert.IsTrue(_engine.CanStartLongBreak);

            _engine.StartLongBreak();
            Assert.AreEqual(PomodoroPhase.Break, _engine.Phase);
            Assert.IsTrue(_engine.IsLongBreak);
            Assert.AreEqual(LongBreak, _engine.CurrentIntervalLength);
            Assert.AreEqual(2, _engine.Index, "the counter doesn't pretend the cycle was finished");
            var t = _transitions.Last();
            Assert.AreEqual(PomodoroTransitionKind.BreakStarted, t.Kind);
            Assert.IsTrue(t.IsLongBreak);

            Elapse(LongBreak);
            Assert.AreEqual(PomodoroPhase.BreakEnded, _engine.Phase);
            Assert.IsTrue(_transitions.Last().IsLongBreak);
            _engine.Start();
            Assert.AreEqual(1, _engine.Index, "an early long break closes the cycle");
            Assert.IsFalse(_engine.IsLongBreak);
            Assert.AreEqual(Work, _engine.CurrentIntervalLength);
        }

        [TestMethod]
        public void A_running_short_break_stretches_into_a_long_one_keeping_elapsed_time()
        {
            Create();
            _engine.Start(); Elapse(Work);
            _engine.Start(); Elapse(120);
            _engine.StartLongBreak();

            Assert.AreEqual(PomodoroPhase.Break, _engine.Phase);
            Assert.AreEqual(LongBreak, _engine.CurrentIntervalLength);
            Assert.AreEqual(120, _engine.Elapsed);
            Assert.IsFalse(_engine.CanStartLongBreak, "already the long break");
            Elapse(LongBreak - 120);
            Assert.AreEqual(PomodoroPhase.BreakEnded, _engine.Phase);
        }

        [TestMethod]
        public void A_paused_short_break_resumes_as_the_long_break()
        {
            Create();
            _engine.Start(); Elapse(Work);
            _engine.Start(); Elapse(60);
            _engine.Pause();
            _engine.StartLongBreak();

            Assert.AreEqual(PomodoroPhase.Break, _engine.Phase);
            Assert.IsTrue(_engine.IsLongBreak);
            Assert.AreEqual(60, _engine.Elapsed);
        }

        [TestMethod]
        public void Long_break_instead_of_the_short_one_right_after_work()
        {
            Create();
            _engine.Start(); Elapse(Work);
            _engine.StartLongBreak();
            Assert.AreEqual(LongBreak, _engine.CurrentIntervalLength);
            _engine.Stop();
            Assert.AreEqual(PomodoroPhase.NotStarted, _engine.Phase);
            Assert.AreEqual(1, _engine.Index);
            Assert.IsFalse(_engine.IsLongBreak);
        }

        [TestMethod]
        public void Long_break_from_idle_and_skip_returns_to_pomodoro_one()
        {
            Create();
            _engine.Start(); Elapse(Work); _engine.Skip(); _engine.Stop(); // idle at pomodoro 2
            _engine.StartLongBreak();
            Assert.AreEqual(PomodoroPhase.Break, _engine.Phase);
            _engine.Skip();
            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            Assert.AreEqual(1, _engine.Index);
        }

        [TestMethod]
        public void Long_break_is_not_offered_during_work()
        {
            Create();
            _engine.Start();
            Assert.IsFalse(_engine.CanStartLongBreak);
            _engine.StartLongBreak();
            Assert.AreEqual(PomodoroPhase.Work, _engine.Phase);
            _engine.Pause();
            Assert.IsFalse(_engine.CanStartLongBreak, "a paused pomodoro is still work");
        }

        [TestMethod]
        public void Requested_long_break_survives_a_restart()
        {
            Create();
            _engine.Start(); Elapse(Work); _engine.Skip(); Elapse(Work);
            _engine.StartLongBreak(); Elapse(30); _engine.Pause();
            var snapshot = _engine.GetSnapshot();

            Create();
            _engine.LoadSnapshot(snapshot);
            Assert.IsTrue(_engine.IsLongBreak);
            Assert.AreEqual(LongBreak - 30, _engine.Remaining);
            Assert.AreEqual(2, _engine.Index);
        }

        // ── Second boundaries (display flip timing, ticking clock) ───────────

        private void ElapseMs(int ms)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(ms));
            _timer.PerformTick();
        }

        [TestMethod]
        public void Next_second_is_exactly_one_second_after_start()
        {
            Create();
            Assert.IsNull(_engine.NextSecondAt, "nothing running");
            _engine.Start();
            Assert.AreEqual(_clock.Monotonic + TimeSpan.FromSeconds(1), _engine.NextSecondAt);
            ElapseMs(400);
            Assert.AreEqual(_clock.Monotonic + TimeSpan.FromMilliseconds(600), _engine.NextSecondAt);
        }

        [TestMethod]
        public void After_resuming_a_fractional_second_the_next_flip_is_the_remainder()
        {
            // Regression: ticks restarted on a fresh 1 s beat at resume, but the display
            // flips 0.4 s after resuming from 12.6 s, so they were off by 0.4 s for the rest.
            Create();
            _engine.Start();
            ElapseMs(12_600);
            _engine.Pause();
            _clock.Advance(TimeSpan.FromMinutes(3));
            _engine.Start();
            Assert.AreEqual(_clock.Monotonic + TimeSpan.FromMilliseconds(400), _engine.NextSecondAt);
            Assert.AreEqual(12, _engine.Elapsed);
        }

        [TestMethod]
        public void The_timer_is_asked_to_tick_just_after_each_boundary()
        {
            Create();
            _engine.Start();
            Assert.IsTrue(_timer.LastScheduled is { } d && d > TimeSpan.FromMilliseconds(1000) && d < TimeSpan.FromMilliseconds(1010),
                $"first tick scheduled for {_timer.LastScheduled}");
            ElapseMs(1003);
            Assert.AreEqual(1, _engine.Elapsed, "the scheduled tick sees the new second");
            // 1.003 s in: the boundary is 997 ms away, plus the 3 ms "just past it" margin.
            Assert.AreEqual(TimeSpan.FromMilliseconds(1000), _timer.LastScheduled, "aims at the next boundary, not a fixed interval");
        }

        [TestMethod]
        public void Paused_timer_has_no_next_second()
        {
            Create();
            _engine.Start();
            ElapseMs(500);
            _engine.Pause();
            Assert.IsNull(_engine.NextSecondAt);
        }

        // ── Cycles ───────────────────────────────────────────────────────────

        /// <summary>Works a pomodoro and its whole break, leaving the engine at BreakEnded.</summary>
        private void Pomodoro()
        {
            _engine.Start();
            Elapse(Work);
            _engine.Start();
            Elapse(_engine.BreakTime);
        }

        [TestMethod]
        public void The_cycle_number_moves_on_after_the_long_break_not_during_it()
        {
            Create(cycle: 2);
            Assert.AreEqual(1, _engine.Cycle);

            Pomodoro();
            _engine.Start();
            Elapse(Work);
            _engine.Start(); // the long break
            Assert.IsTrue(_engine.IsLongBreak);
            Assert.AreEqual(1, _engine.Cycle, "the long break belongs to the cycle it ends");

            Elapse(LongBreak);
            _engine.Start();
            Assert.AreEqual(2, _engine.Cycle);
            Assert.AreEqual(1, _engine.Index);
        }

        [TestMethod]
        public void Ending_or_skipping_the_long_break_also_finishes_the_cycle()
        {
            Create(cycle: 1);
            _engine.Start();
            Elapse(Work);
            _engine.Stop(); // ends the (long) break
            Assert.AreEqual(2, _engine.Cycle);

            _engine.Start();
            Elapse(Work);
            _engine.Skip();
            Assert.AreEqual(3, _engine.Cycle);
        }

        [TestMethod]
        public void An_early_long_break_finishes_the_cycle_only_if_work_was_done()
        {
            Create();
            _engine.StartLongBreak(); // straight from idle: nothing to finish
            Elapse(LongBreak);
            _engine.Start();
            Assert.AreEqual(1, _engine.Cycle);

            Elapse(Work);
            _engine.StartLongBreak();
            Elapse(LongBreak);
            _engine.Start();
            Assert.AreEqual(2, _engine.Cycle);
        }

        [TestMethod]
        public void Abandoning_a_pomodoro_or_resetting_keeps_the_cycle()
        {
            Create(cycle: 1);
            Pomodoro();
            _engine.Start();
            Assert.AreEqual(2, _engine.Cycle);

            _engine.Stop();
            Assert.AreEqual(2, _engine.Cycle);
            _engine.Reset();
            Assert.AreEqual(2, _engine.Cycle, "reset starts this cycle over");
        }

        [TestMethod]
        public void Cycles_are_counted_per_day()
        {
            Create(cycle: 1);
            Pomodoro();
            _engine.Start();
            Assert.AreEqual(2, _engine.Cycle);

            _engine.Stop();
            _clock.Advance(TimeSpan.FromDays(1));
            Assert.AreEqual(1, _engine.Cycle);

            Pomodoro();
            _engine.Start();
            Assert.AreEqual(2, _engine.Cycle, "yesterday's cycles don't carry over");
        }

        [TestMethod]
        public void The_cycle_survives_a_restart()
        {
            Create(cycle: 2);
            Pomodoro();
            Pomodoro();
            _engine.Start();
            Elapse(Work); // first pomodoro of cycle 2
            var snapshot = _engine.GetSnapshot();

            Create(cycle: 2);
            _engine.LoadSnapshot(snapshot);
            Assert.AreEqual(2, _engine.Cycle);
            Assert.AreEqual(1, _engine.Index);

            // Restored mid-cycle with work done: an early long break still finishes it.
            _engine.StartLongBreak();
            Elapse(LongBreak);
            _engine.Start();
            Assert.AreEqual(3, _engine.Cycle);
        }

        [TestMethod]
        public void Old_or_stale_snapshots_start_at_cycle_one()
        {
            Create();
            _engine.LoadSnapshot(new PomodoroEngineSnapshot { Phase = PomodoroPhase.NotStarted, PomodoroIndex = 2 });
            Assert.AreEqual(1, _engine.Cycle, "snapshots from older builds have no cycles");

            _engine.LoadSnapshot(new PomodoroEngineSnapshot
            {
                Phase = PomodoroPhase.NotStarted, PomodoroIndex = 1, CyclesFinished = 3,
                CyclesDate = _clock.UtcNow.ToLocalTime().Date.AddDays(-1),
            });
            Assert.AreEqual(1, _engine.Cycle);
        }
        // ── Overrun ──────────────────────────────────────────────────────────

        [TestMethod]
        public void Overrun_counts_up_while_an_ended_interval_waits()
        {
            Create();
            Assert.IsNull(_engine.Overrun, "nothing has ended");
            _engine.Start();
            Elapse(100);
            Assert.IsNull(_engine.Overrun, "not while working");

            Elapse(Work - 100);
            Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
            Assert.AreEqual(0, _engine.Overrun);

            var notified = new List<int?>();
            _engine.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IPomodoroEngine.Overrun)) notified.Add(_engine.Overrun); };
            Elapse(1);
            Elapse(1);
            _timer.PerformTick(); // a tick within the same second announces nothing
            Elapse(62);
            CollectionAssert.AreEqual(new int?[] { 1, 2, 64 }, notified);
            Assert.AreEqual(0, _engine.Remaining, "the interval itself stays over");

            _engine.Start();
            Assert.IsNull(_engine.Overrun, "the break has begun");
            Assert.AreEqual(null, notified.Last());

            Elapse(ShortBreak + 30);
            Assert.AreEqual(PomodoroPhase.BreakEnded, _engine.Phase);
            Assert.AreEqual(30, _engine.Overrun, "counted from when the break ran out, however late the tick");
        }

        [TestMethod]
        public void Overrun_ticks_land_on_its_own_seconds()
        {
            Create();
            _engine.Start();
            Elapse(Work);
            _clock.Advance(TimeSpan.FromMilliseconds(400));
            _timer.PerformTick();
            Assert.IsTrue(_timer.LastScheduled is { } d && d > TimeSpan.FromMilliseconds(600) && d < TimeSpan.FromMilliseconds(610),
                $"next overrun tick scheduled for {_timer.LastScheduled}");
        }

        [TestMethod]
        public void Overrun_ends_with_stop_skip_or_reset()
        {
            Create();
            _engine.Start();
            Elapse(Work + 5);
            _engine.Stop();
            Assert.IsNull(_engine.Overrun);
            Assert.IsFalse(_timer.IsRunning, "nothing left to count");

            _engine.Start();
            Elapse(Work + 5);
            _engine.Skip();
            Assert.IsNull(_engine.Overrun);

            Elapse(Work + 5);
            _engine.Reset();
            Assert.IsNull(_engine.Overrun);
        }

        [TestMethod]
        public void Sleep_does_not_count_as_overrun()
        {
            Create();
            _engine.Start();
            Elapse(Work);
            _clock.Sleep(TimeSpan.FromHours(8));
            Elapse(3);
            Assert.AreEqual(3, _engine.Overrun);
        }

        [TestMethod]
        public void Overrun_survives_a_restart_without_the_time_the_app_was_closed()
        {
            Create();
            _engine.Start();
            Elapse(Work + 70);
            var snapshot = _engine.GetSnapshot();
            Assert.AreEqual(70, snapshot.OverrunSeconds);

            Create();
            _clock.DateToReturn = snapshot.StartDate.AddHours(9); // switched off overnight
            _engine.LoadSnapshot(snapshot);
            Assert.AreEqual(PomodoroPhase.WorkEnded, _engine.Phase);
            Assert.AreEqual(70, _engine.Overrun, "not +9h");
            Assert.IsTrue(_timer.IsRunning);
            Elapse(1);
            Assert.AreEqual(71, _engine.Overrun);
        }

        [TestMethod]
        public void An_ended_snapshot_from_an_older_build_counts_from_the_restore()
        {
            Create();
            _engine.LoadSnapshot(new PomodoroEngineSnapshot
            {
                Phase = PomodoroPhase.BreakEnded,
                PomodoroIndex = 2,
                StartDate = _clock.UtcNow,
            });
            Assert.AreEqual(0, _engine.Overrun);
        }

        [TestMethod]
        public void Auto_started_breaks_still_begin_on_time()
        {
            Create(autoBreak: true);
            _engine.Start();
            Elapse(Work);
            Assert.AreEqual(0, _engine.Overrun);
            Elapse(2);
            Assert.AreEqual(PomodoroPhase.Break, _engine.Phase);
            Assert.IsNull(_engine.Overrun);
        }
    }
}
