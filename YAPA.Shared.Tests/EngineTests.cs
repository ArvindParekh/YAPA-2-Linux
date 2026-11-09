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
            Assert.IsFalse(_timer.IsRunning);
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
    }
}
