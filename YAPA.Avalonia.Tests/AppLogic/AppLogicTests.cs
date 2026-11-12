using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.SingleInstance;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.ViewModels;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Tests.AppLogic;

internal sealed class MemoryEnvironment : IEnvironment
{
    public string Settings = "{}";
    public string GetSettings() => Settings;
    public void SaveSettings(string settings) => Settings = settings;
    public string GetLocalSettings() => "{}";
    public void SaveLocalSettings(string settings) { }
    public string GetPluginDirectory() => "/nonexistent";
    public string GetThemeDirectory() => "/nonexistent";
    public bool PreRelease() => false;
}

internal sealed class ManualTimer : ITimer
{
    public event Action? Tick;
    public bool IsRunning { get; private set; }
    public void Fire() => Tick?.Invoke();
    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
}

internal sealed class ManualClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    public TimeSpan Monotonic { get; set; } = TimeSpan.FromHours(1);
    public void Advance(int seconds) { UtcNow = UtcNow.AddSeconds(seconds); Monotonic += TimeSpan.FromSeconds(seconds); }
}

internal sealed class Inline : IThreading
{
    public void RunOnUiThread(Action action) => action();
}

internal sealed class Rig
{
    public readonly ISettings Settings;
    public readonly PomodoroEngineSettings EngineSettings;
    public readonly ManualTimer Timer = new();
    public readonly ManualTimer ForecastTimer = new();
    public readonly ManualClock Clock = new();
    public readonly PomodoroEngine Engine;
    public readonly IPomodoroRepository Repository = Substitute.For<IPomodoroRepository>();

    public Rig(string settingsJson = "{}")
    {
        Settings = new JsonYapaSettings(new MemoryEnvironment { Settings = settingsJson }, new NewtonsoftJson());
        EngineSettings = new PomodoroEngineSettings(Settings);
        Engine = new PomodoroEngine(EngineSettings, Timer, Clock, new Inline(), Settings, Repository);
    }

    public void Elapse(int seconds)
    {
        Clock.Advance(seconds);
        Timer.Fire();
    }
}

[TestClass]
public class CommandLineTests
{
    [TestMethod]
    public void Accepts_dashed_and_legacy_slash_forms()
    {
        var parsed = CommandLine.Parse(["--start", "/pause", "toggle"]);
        CollectionAssert.AreEqual(new[] { AppCommand.Start, AppCommand.Pause, AppCommand.Toggle }, parsed.Commands.ToArray());
    }

    [TestMethod]
    public void Json_turns_status_into_json_status()
    {
        var parsed = CommandLine.Parse(["--status", "--json"]);
        CollectionAssert.AreEqual(new[] { AppCommand.StatusJson }, parsed.Commands.ToArray());
        Assert.IsTrue(parsed.IsQueryOnly);
    }

    [TestMethod]
    public void Status_is_query_only_but_start_is_not()
    {
        Assert.IsTrue(CommandLine.Parse(["--status"]).IsQueryOnly);
        Assert.IsTrue(CommandLine.Parse(["--quit"]).IsQueryOnly);
        Assert.IsFalse(CommandLine.Parse(["--status", "--start"]).IsQueryOnly);
        Assert.IsFalse(CommandLine.Parse([]).IsQueryOnly);
    }

    [TestMethod]
    public void Long_break_command_parses()
        => CollectionAssert.AreEqual(new[] { AppCommand.LongBreak }, CommandLine.Parse(["--long-break"]).Commands.ToArray());

    [TestMethod]
    public void Mark_done_command_parses_and_is_not_query_only()
    {
        var parsed = CommandLine.Parse(["--mark-done"]);
        CollectionAssert.AreEqual(new[] { AppCommand.MarkDone }, parsed.Commands.ToArray());
        Assert.IsFalse(parsed.IsQueryOnly, "it changes state, so it launches YAPA if needed");
    }

    [TestMethod]
    public void Unknown_arguments_are_reported_not_fatal()
    {
        var parsed = CommandLine.Parse(["--bogus", "--skip"]);
        CollectionAssert.AreEqual(new[] { "--bogus" }, parsed.Unknown.ToArray());
        CollectionAssert.AreEqual(new[] { AppCommand.Skip }, parsed.Commands.ToArray());
    }
}

[TestClass]
public class InstanceChannelTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"yapa-test-{Guid.NewGuid():N}.sock");

    [TestMethod]
    public void Second_instance_gets_the_primary_reply()
    {
        var path = SocketPath();
        using var primary = InstanceChannel.TryBecomePrimary(path);
        Assert.IsNotNull(primary);
        primary!.RequestHandler = args => "got " + string.Join(",", args);

        Assert.IsNull(InstanceChannel.TryBecomePrimary(path), "only one primary per socket");
        Assert.AreEqual("got --status,--json", InstanceChannel.Send(["--status", "--json"], path));
    }

    [TestMethod]
    public void Stale_socket_from_a_crashed_instance_is_replaced()
    {
        var path = SocketPath();
        // A crashed process leaves its socket file behind with nobody listening.
        // (.NET unlinks the file on a clean dispose, so simulate the crash with a plain file.)
        File.WriteAllText(path, "");

        using var primary = InstanceChannel.TryBecomePrimary(path);
        Assert.IsNotNull(primary);
    }

    [TestMethod]
    public void Overlong_runtime_dir_falls_back_to_a_usable_socket_path()
    {
        // Regression: a long XDG_RUNTIME_DIR overflowed sun_path and crashed startup.
        var longDir = Path.Combine(Path.GetTempPath(), new string('d', 120));
        Directory.CreateDirectory(longDir);
        try
        {
            var path = InstanceChannel.ChooseSocketPath(longDir, "/nonexistent", "user");
            Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(path) <= 107, path);
            using var primary = InstanceChannel.TryBecomePrimary(path);
            Assert.IsNotNull(primary);
        }
        finally
        {
            Directory.Delete(longDir);
        }
    }

    [TestMethod]
    public void Runtime_dir_is_preferred_when_it_fits()
        => Assert.AreEqual("/tmp/yapa2-user.sock", InstanceChannel.ChooseSocketPath("/tmp", Path.GetTempPath(), "user"));

    [TestMethod]
    public void Send_without_a_primary_returns_null()
        => Assert.IsNull(InstanceChannel.Send(["--status"], SocketPath()));

    [TestMethod]
    public void Liveness_probe_does_not_invoke_the_handler()
    {
        var path = SocketPath();
        var calls = 0;
        using var primary = InstanceChannel.TryBecomePrimary(path)!;
        primary.RequestHandler = _ => { calls++; return ""; };
        Assert.IsNull(InstanceChannel.TryBecomePrimary(path)); // probes, finds us alive
        Task.Delay(100).Wait();
        Assert.AreEqual(0, calls);
    }
}

[TestClass]
public class WindowPlacementTests
{
    private static readonly PixelRect Primary = new(0, 40, 1920, 1040);
    private static readonly PixelRect Secondary = new(1920, 40, 1920, 1040);
    private static readonly PixelSize Size = new(234, 108);

    [TestMethod]
    public void Saved_position_on_a_connected_monitor_is_kept()
        => Assert.AreEqual(new PixelPoint(3560, 120), WindowPlacement.Restore(new PixelPoint(3560, 120), Size, [Primary, Secondary], Primary));

    [TestMethod]
    public void Position_on_an_unplugged_monitor_falls_back_to_primary_top_right()
    {
        var p = WindowPlacement.Restore(new PixelPoint(3560, 120), Size, [Primary], Primary);
        Assert.AreEqual(new PixelPoint(1920 - 234 - 24, 40 + 24), p);
    }

    [TestMethod]
    public void A_sliver_left_on_screen_does_not_count_as_visible()
    {
        var p = WindowPlacement.Restore(new PixelPoint(1915, 200), Size, [Primary], Primary);
        Assert.AreNotEqual(new PixelPoint(1915, 200), p);
    }

    [TestMethod]
    public void No_saved_position_uses_the_default()
        => Assert.AreEqual(new PixelPoint(1662, 64), WindowPlacement.Restore(null, Size, [Primary], Primary));
}

[TestClass]
public class SoundSettingsTests
{
    [TestMethod]
    public void Fresh_install_gets_the_new_distinct_cues()
    {
        var s = new SoundSettings(new Rig().Settings);
        Assert.AreEqual(DefaultCueSounds.Start, s.Get(SoundCue.Start));
        Assert.AreEqual(DefaultCueSounds.Pause, s.Get(SoundCue.Pause));
        Assert.AreNotEqual(s.Get(SoundCue.Start), s.Get(SoundCue.Stop));
    }

    [TestMethod]
    public void Custom_files_from_the_old_version_carry_over()
    {
        var rig = new Rig("""{"SoundNotifications":{"PeriodStartSound":"/home/me/start.ogg","PeriodEndSound":"/home/me/end.wav"}}""");
        var s = new SoundSettings(rig.Settings);
        Assert.AreEqual(SoundRef.File("/home/me/start.ogg"), s.Get(SoundCue.Start));
        Assert.AreEqual(SoundRef.File("/home/me/start.ogg"), s.Get(SoundCue.Resume));
        Assert.AreEqual(SoundRef.File("/home/me/end.wav"), s.Get(SoundCue.WorkCompleted));
        Assert.AreEqual(DefaultCueSounds.Pause, s.Get(SoundCue.Pause), "pause had no old equivalent");
    }

    [TestMethod]
    public void Old_default_tick_path_does_not_override_the_new_default()
    {
        var rig = new Rig("""{"SoundNotifications":{"PeriodStartSound":"/usr/local/lib/yapa2/Assets/tick.wav"}}""");
        Assert.AreEqual(DefaultCueSounds.Start, new SoundSettings(rig.Settings).Get(SoundCue.Start));
    }

    [TestMethod]
    public void Choosing_none_sticks()
    {
        var s = new SoundSettings(new Rig().Settings);
        s.Set(SoundCue.Pause, SoundRef.None);
        Assert.IsTrue(s.Get(SoundCue.Pause).IsNone);
    }
}

[TestClass]
public class TimerViewModelTests
{
    private static (Rig, TimerViewModel) Create(string settingsJson = "{}")
    {
        var rig = new Rig(settingsJson);
        var vm = new TimerViewModel(rig.Engine, rig.EngineSettings, new AvaloniaYapaThemeSettings(rig.Settings), Substitute.For<IAppNavigator>(),
            new YAPA.Avalonia.Audio.Ambience.AmbienceSettings(rig.Settings), new GeneralSettings(rig.Settings),
            rig.Repository, rig.Clock, rig.ForecastTimer);
        return (rig, vm);
    }

    [TestMethod]
    public void Todays_count_shows_against_the_daily_goal()
    {
        var (rig, vm) = Create(@"{""PomodoroEngine"":{""Counter"":1},""General"":{""DailyGoal"":12}}");
        rig.Repository.CompletedToday().Returns(2);
        vm.Refresh();
        Assert.AreEqual("2/12", vm.CounterText);
        Assert.AreEqual("2 of 12 pomodoros done today", vm.CounterTooltip);

        rig.Repository.CompletedToday().Returns(13);
        vm.Refresh();
        Assert.AreEqual("13/12", vm.CounterText, "past the goal it keeps counting");
    }

    [TestMethod]
    public void A_new_goal_shows_at_once()
    {
        var rig = new Rig(@"{""PomodoroEngine"":{""Counter"":1}}");
        var general = new GeneralSettings(rig.Settings);
        var vm = new TimerViewModel(rig.Engine, rig.EngineSettings, new AvaloniaYapaThemeSettings(rig.Settings), Substitute.For<IAppNavigator>(),
            new YAPA.Avalonia.Audio.Ambience.AmbienceSettings(rig.Settings), general, rig.Repository, rig.Clock, rig.ForecastTimer);
        Assert.AreEqual("0/8", vm.CounterText, "the default goal");
        general.DailyGoal = 12;
        Assert.AreEqual("0/12", vm.CounterText);
        general.DailyGoal = 0;
        Assert.AreEqual("0", vm.CounterText, "no goal, no denominator");
    }

    [TestMethod]
    public void The_cycle_position_shows_against_its_length_and_names_the_cycle()
    {
        var (rig, vm) = Create();
        Assert.AreEqual("1/4", vm.CounterText);
        Assert.AreEqual("", vm.CycleSuffix, "nothing to name before the first pomodoro");
        Assert.AreEqual("Pomodoro 1 of 4, cycle 1", vm.CounterTooltip);

        rig.Engine.Start();
        Assert.AreEqual(" · Cycle 1", vm.CycleSuffix);

        for (var i = 0; i < 4; i++)
        {
            if (i > 0) rig.Engine.Start();
            rig.Elapse(25 * 60);
            rig.Engine.Start();
            Assert.AreEqual($"{i + 1}/4", vm.CounterText);
            rig.Elapse(rig.Engine.BreakTime);
        }
        Assert.AreEqual("Break over", vm.StatusText);
        Assert.AreEqual(" · Cycle 1", vm.CycleSuffix, "still the cycle the long break ended");

        rig.Engine.Start();
        Assert.AreEqual("1/4", vm.CounterText);
        Assert.AreEqual(" · Cycle 2", vm.CycleSuffix);

        rig.Engine.Stop();
        Assert.AreEqual(" · Cycle 2", vm.CycleSuffix, "at rest later in the day, the cycle still shows");

        rig.EngineSettings.Counter = CounterEnum.CompletedToday;
        Assert.AreEqual("", vm.CycleSuffix, "only the cycle counter names the cycle");
    }

    [TestMethod]
    public void The_goal_is_not_shown_beside_other_counts()
    {
        var (rig, vm) = Create(@"{""General"":{""DailyGoal"":12}}");
        Assert.AreEqual("1/4", vm.CounterText, "the cycle's own length, not the day's goal");

        rig.EngineSettings.Counter = CounterEnum.CompletedThisSession;
        Assert.AreEqual("0", vm.CounterText);

        rig.EngineSettings.Counter = CounterEnum.CompletedToday;
        Assert.AreEqual("0/12", vm.CounterText, "switching the counter applies live");
    }

    [TestMethod]
    public void Buttons_follow_the_phase()
    {
        var (rig, vm) = Create();
        Assert.IsTrue(vm.CanStart);
        Assert.AreEqual("START", vm.StartLabel);
        Assert.IsFalse(vm.CanStop);

        rig.Engine.Start();
        Assert.IsFalse(vm.CanStart);
        Assert.IsTrue(vm.CanPause);
        Assert.IsTrue(vm.CanStop);
        Assert.IsFalse(vm.CanSkip);

        rig.Engine.Pause();
        Assert.AreEqual("RESUME", vm.StartLabel);
        Assert.AreEqual("Paused", vm.StatusText);

        rig.Engine.Start();
        rig.Elapse(25 * 60);
        Assert.AreEqual("BREAK", vm.StartLabel);
        Assert.IsTrue(vm.CanSkip);
        Assert.IsTrue(vm.IsWorkEndedAlert);
        Assert.AreEqual("End break", vm.StopMenuText);
    }

    [TestMethod]
    public void A_pomodoro_done_off_the_timer_is_recorded_and_the_cycle_moves_on()
    {
        var (rig, vm) = Create();
        _ = new YAPA.Shared.Common.Dashboard(rig.Engine, rig.Repository, rig.EngineSettings); // writes history, as in the app
        Assert.IsTrue(vm.CanMarkDone);

        vm.MarkDoneCommand.Execute(null);

        rig.Repository.Received(1).Add(Arg.Is<PomodoroEntity>(p => p.Count == 1 && p.DurationMin == 25));
        Assert.AreEqual("2/4", vm.CounterText);
        Assert.AreEqual("START", vm.StartLabel);
        Assert.IsTrue(vm.CanMarkDone, "once per pomodoro missed");

        rig.Engine.Start();
        Assert.IsFalse(vm.CanMarkDone, "hidden while one runs");
        rig.Engine.Pause();
        Assert.IsFalse(vm.CanMarkDone);
    }

    [TestMethod]
    public void Paused_break_says_so()
    {
        var (rig, vm) = Create();
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        rig.Engine.Start();
        rig.Engine.Pause();
        Assert.AreEqual("Break paused", vm.StatusText);
        Assert.IsTrue(vm.CanSkip);
        Assert.IsTrue(vm.IsBreakInterval);
    }

    [TestMethod]
    public void Toggle_starts_and_pauses()
    {
        var (rig, vm) = Create();
        vm.ToggleCommand.Execute(null);
        Assert.AreEqual(PomodoroPhase.Work, rig.Engine.Phase);
        vm.ToggleCommand.Execute(null);
        Assert.AreEqual(PomodoroPhase.Pause, rig.Engine.Phase);
    }

    [TestMethod]
    public void Time_text_formats_and_hides_seconds_with_a_last_minute_marker()
    {
        var (rig, vm) = Create();
        rig.EngineSettings.CountBackwards = true;
        rig.Engine.Start();
        rig.Elapse(61);
        Assert.AreEqual("23:59", vm.TimeText);

        new AvaloniaYapaThemeSettings(rig.Settings).HideSeconds = true;
        vm.Refresh();
        Assert.AreEqual("23", vm.TimeText);
        rig.Elapse(23 * 60 + 30);
        Assert.AreEqual("<1", vm.TimeText);
    }

    [TestMethod]
    public void After_an_interval_ends_the_clock_counts_the_overrun()
    {
        var (rig, vm) = Create();
        rig.EngineSettings.CountBackwards = true;
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        Assert.AreEqual("00:00", vm.TimeText, "the moment it ends");
        Assert.IsFalse(vm.IsOverrun);

        rig.Elapse(1);
        Assert.AreEqual("+00:01", vm.TimeText);
        Assert.IsTrue(vm.IsOverrun);
        rig.Elapse(250);
        Assert.AreEqual("+04:11", vm.TimeText);
        StringAssert.Contains(vm.Summary, "Work done · +04:11");

        rig.Engine.Start();
        Assert.AreEqual("05:00", vm.TimeText, "the break counts down as usual");
        Assert.IsFalse(vm.IsOverrun);

        rig.Elapse(5 * 60 + 30);
        Assert.AreEqual("Break over", vm.StatusText);
        Assert.AreEqual("+00:30", vm.TimeText, "breaks overrun too");
    }

    [TestMethod]
    public void Overrun_without_seconds_shows_whole_minutes()
    {
        var (rig, vm) = Create();
        new AvaloniaYapaThemeSettings(rig.Settings).HideSeconds = true;
        rig.Engine.Start();
        rig.Elapse(25 * 60 + 59);
        Assert.IsFalse(vm.IsOverrun, "under a minute there's no whole minute to show");
        rig.Elapse(1);
        Assert.AreEqual("+1", vm.TimeText);
    }

    [TestMethod]
    public void Overrun_formats()
    {
        Assert.AreEqual("+00:07", TimerViewModel.FormatOverrun(7, showSeconds: true));
        Assert.AreEqual("+59:59", TimerViewModel.FormatOverrun(3599, showSeconds: true));
        Assert.AreEqual("+1h", TimerViewModel.FormatOverrun(3600, showSeconds: true));
        Assert.AreEqual("+3h", TimerViewModel.FormatOverrun(3 * 3600 + 1800, showSeconds: false));
        Assert.AreEqual("+12", TimerViewModel.FormatOverrun(12 * 60 + 30, showSeconds: false));
    }

    private static string At(Rig rig, int minutesFromNow)
        => YAPA.Avalonia.Platform.ClockFormat.ShortTime(rig.Clock.UtcNow.ToLocalTime().AddMinutes(minutesFromNow));

    [TestMethod]
    public void The_goal_forecast_is_the_time_the_last_pomodoro_would_end()
    {
        // Default profile: 25/5/15, four to the cycle.
        var (rig, vm) = Create(@"{""General"":{""DailyGoal"":4}}");
        rig.Repository.CompletedToday().Returns(2);
        vm.Refresh();
        Assert.AreEqual($"4 by {At(rig, 25 + 5 + 25)}", vm.GoalForecastText);
        Assert.AreEqual($"2 of 4 today · done by {At(rig, 55)}", vm.GoalSummary);
        Assert.IsFalse(vm.HasGoalWithoutLongBreaks, "no long break falls before the goal");
    }

    [TestMethod]
    public void Idle_time_pushes_the_forecast_later_and_running_time_does_not()
    {
        var (rig, vm) = Create(@"{""General"":{""DailyGoal"":2}}");
        var start = At(rig, 55);
        Assert.AreEqual($"2 by {start}", vm.GoalForecastText);
        Assert.IsTrue(rig.ForecastTimer.IsRunning, "idle: only the refresh moves it on");

        rig.Clock.Advance(10 * 60);
        rig.ForecastTimer.Fire();
        Assert.AreEqual($"2 by {At(rig, 55)}", vm.GoalForecastText);

        rig.Engine.Start();
        Assert.IsFalse(rig.ForecastTimer.IsRunning, "the engine ticks while running");
        var running = vm.GoalForecastText;
        rig.Elapse(60);
        Assert.AreEqual(running, vm.GoalForecastText);

        rig.Repository.CompletedToday().Returns(1); // as the history records the finished one
        rig.Elapse(24 * 60);
        rig.Elapse(120);
        Assert.AreEqual("+02:00", vm.TimeText);
        Assert.AreEqual($"2 by {At(rig, 5 + 25)}", vm.GoalForecastText, "an overrun moves it on as it happens");
    }

    [TestMethod]
    public void Long_breaks_get_their_own_figure_when_they_fall_before_the_goal()
    {
        var (rig, vm) = Create(@"{""General"":{""DailyGoal"":8}}");
        // 8 × 25, six short breaks, one long one after the 4th.
        Assert.AreEqual($"0 of 8 today · done by {At(rig, 8 * 25 + 6 * 5 + 15)}", vm.GoalSummary);
        Assert.AreEqual($"{At(rig, 8 * 25 + 7 * 5)} without long breaks", vm.GoalWithoutLongBreaks);
        Assert.IsTrue(vm.HasGoalWithoutLongBreaks);
    }

    [TestMethod]
    public void A_reached_goal_leaves_only_the_count()
    {
        var (rig, vm) = Create(@"{""General"":{""DailyGoal"":4}}");
        rig.Repository.CompletedToday().Returns(4);
        vm.Refresh();
        Assert.AreEqual("", vm.GoalForecastText);
        Assert.IsFalse(vm.HasGoalForecast);
        Assert.AreEqual("4 of 4 today", vm.GoalSummary);

        rig.Repository.CompletedToday().Returns(0);
        rig.Clock.Advance(24 * 3600);
        rig.ForecastTimer.Fire();
        StringAssert.StartsWith(vm.GoalSummary, "0 of 4 today · done by", "the next morning starts afresh without a click");
    }

    [TestMethod]
    public void The_forecast_can_be_hidden_and_no_goal_means_none()
    {
        var rig = new Rig(@"{""General"":{""DailyGoal"":4}}");
        var general = new GeneralSettings(rig.Settings);
        var vm = new TimerViewModel(rig.Engine, rig.EngineSettings, new AvaloniaYapaThemeSettings(rig.Settings), Substitute.For<IAppNavigator>(),
            new YAPA.Avalonia.Audio.Ambience.AmbienceSettings(rig.Settings), general, rig.Repository, rig.Clock, rig.ForecastTimer);
        Assert.IsTrue(vm.HasGoalForecast);

        general.ShowGoalForecast = false;
        Assert.AreEqual("", vm.GoalForecastText);
        Assert.IsTrue(vm.HasGoal, "the menu still has the day's count");

        general.DailyGoal = 0;
        Assert.IsFalse(vm.HasGoal);
        Assert.AreEqual("", vm.GoalSummary);
    }

    [TestMethod]
    public void Flash_respects_the_setting()
    {
        var (rig, vm) = Create();
        new AvaloniaYapaThemeSettings(rig.Settings).DisableFlashingAnimation = true;
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        vm.Refresh();
        Assert.AreEqual(TimerAlert.WorkEnded, vm.Alert);
        Assert.IsFalse(vm.IsAlerting);
    }

    [TestMethod]
    public void A_click_after_the_interval_ends_stops_the_count_up_and_the_pulse()
    {
        var (rig, vm) = Create();
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        rig.Elapse(5);
        Assert.IsTrue(vm.IsOverrun);
        Assert.AreEqual("+00:05", vm.TimeText);
        Assert.IsTrue(vm.IsAlerting);

        vm.StopOverrunCommand.Execute(null);

        Assert.IsFalse(vm.IsOverrun);
        Assert.IsFalse(vm.TimeText.StartsWith('+'), vm.TimeText);
        Assert.IsFalse(vm.IsAlerting, "answered: no more pulsing");
        Assert.AreEqual(TimerAlert.WorkEnded, vm.Alert);
        Assert.AreEqual("BREAK", vm.StartLabel, "still waiting for the break");

        vm.StartCommand.Execute(null);
        rig.Elapse(rig.Engine.BreakTime);
        Assert.IsTrue(vm.IsAlerting, "the next ended interval calls again");
    }
}

[TestClass]
public class NotificationEffectsTests
{
    private static (Rig, IDesktopNotifier, GeneralSettings) Create()
    {
        var rig = new Rig();
        var notifier = Substitute.For<IDesktopNotifier>();
        var general = new GeneralSettings(rig.Settings);
        _ = new NotificationEffects(rig.Engine, rig.EngineSettings, general, notifier, Substitute.For<IAppNavigator>());
        return (rig, notifier, general);
    }

    [TestMethod]
    public void Work_completion_offers_start_and_skip()
    {
        var (rig, notifier, _) = Create();
        rig.Engine.Start();
        rig.Elapse(25 * 60);

        notifier.Received(1).ShowAsync(Arg.Is<DesktopNotification>(n =>
            n.Title == "Pomodoro complete"
            && n.Persistent
            && n.Actions.Any(a => a.Key == "start-break")
            && n.Actions.Any(a => a.Key == "skip-break")
            && n.Body.Contains("5 minutes")));
    }

    [TestMethod]
    public void Break_over_offers_the_long_break()
    {
        var (rig, notifier, _) = Create();
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        rig.Engine.Start();
        rig.Elapse(5 * 60);
        notifier.Received(1).ShowAsync(Arg.Is<DesktopNotification>(n =>
            n.Title == "Break's over" && n.Actions.Any(a => a.Key == "long-break")));
    }

    [TestMethod]
    public void Long_break_is_not_offered_when_it_is_already_due()
    {
        var rig = new Rig("""{"PomodoroEngine":{"Profiles":{"Pomodoro":{"WorkTime":1500,"BreakTime":300,"LongBreakTime":900,"PomodorosBeforeLongBreak":1}}}}""");
        var notifier = Substitute.For<IDesktopNotifier>();
        _ = new NotificationEffects(rig.Engine, rig.EngineSettings, new GeneralSettings(rig.Settings), notifier, Substitute.For<IAppNavigator>());
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        notifier.Received(1).ShowAsync(Arg.Is<DesktopNotification>(n =>
            n.Actions.All(a => a.Key != "long-break") && n.Actions.Any(a => a.Label == "Start long break")));
    }

    [TestMethod]
    public void Acting_in_the_app_withdraws_the_prompt()
    {
        var (rig, notifier, _) = Create();
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        rig.Engine.Start(); // user started the break from the timer
        notifier.Received().DismissAsync();
    }

    [TestMethod]
    public void Disabled_notifications_show_nothing()
    {
        var (rig, notifier, general) = Create();
        general.DesktopNotifications = false;
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        notifier.DidNotReceive().ShowAsync(Arg.Any<DesktopNotification>());
    }

    [TestMethod]
    public void Starting_a_pomodoro_by_hand_does_not_notify()
    {
        var (rig, notifier, _) = Create();
        rig.Engine.Start();
        notifier.DidNotReceive().ShowAsync(Arg.Any<DesktopNotification>());
    }
}

[TestClass]
[DoNotParallelize]
public class CrashRecoveryTests
{
    [TestMethod]
    public void A_crash_mid_pomodoro_is_recoverable_without_a_clean_quit()
    {
        // First run: start a pomodoro, work 10 minutes, then "crash" (no Save, no quit).
        var first = new Rig();
        using (var snapshots = new YAPA.Avalonia.Persistence.SnapshotService(first.Engine, first.Clock, new NewtonsoftJson()))
        {
            first.Engine.Start();
            first.Elapse(600);
            first.Engine.Pause(); // any transition is saved immediately
        }

        // Next launch, a minute later.
        var second = new Rig();
        second.Clock.UtcNow = first.Clock.UtcNow.AddMinutes(1);
        using var restore = new YAPA.Avalonia.Persistence.SnapshotService(second.Engine, second.Clock, new NewtonsoftJson());
        Assert.IsTrue(restore.Restore(resume: false), "the session survives the crash");
        Assert.AreEqual(PomodoroPhase.Pause, second.Engine.Phase);
        Assert.AreEqual(600, second.Engine.Elapsed);
    }

    [TestMethod]
    public void Idle_after_a_finished_cycle_keeps_the_cycle_across_a_restart()
    {
        var first = new Rig(@"{""PomodoroEngine"":{""Profiles"":{""Pomodoro"":{""WorkTime"":1500,""BreakTime"":300,""LongBreakTime"":900,""PomodorosBeforeLongBreak"":1}}}}");
        using (new YAPA.Avalonia.Persistence.SnapshotService(first.Engine, first.Clock, new NewtonsoftJson()))
        {
            first.Engine.MarkDone(); // the cycle's only pomodoro, and its long break
            Assert.AreEqual(1, first.Engine.Index);
            Assert.AreEqual(2, first.Engine.Cycle);
        }

        var second = new Rig(@"{""PomodoroEngine"":{""Profiles"":{""Pomodoro"":{""WorkTime"":1500,""BreakTime"":300,""LongBreakTime"":900,""PomodorosBeforeLongBreak"":1}}}}");
        second.Clock.UtcNow = first.Clock.UtcNow.AddMinutes(1);
        using var restore = new YAPA.Avalonia.Persistence.SnapshotService(second.Engine, second.Clock, new NewtonsoftJson());
        Assert.IsTrue(restore.Restore(resume: false));
        Assert.AreEqual(2, second.Engine.Cycle);
    }

    [TestMethod]
    public void Starting_a_pomodoro_is_on_disk_immediately()
    {
        var rig = new Rig();
        using var snapshots = new YAPA.Avalonia.Persistence.SnapshotService(rig.Engine, rig.Clock, new NewtonsoftJson());
        rig.Engine.Start();
        var path = System.IO.Path.Combine(YAPA.Avalonia.Platform.AppPaths.DataDirectory, "snapshot.json");
        StringAssert.Contains(System.IO.File.ReadAllText(path), "\"Phase\": 1", "Work is saved the moment it starts");
    }
}
