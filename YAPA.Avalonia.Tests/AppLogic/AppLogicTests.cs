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
    public void Fire() => Tick?.Invoke();
    public void Start() { }
    public void Stop() { }
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
    public readonly ManualClock Clock = new();
    public readonly PomodoroEngine Engine;

    public Rig(string settingsJson = "{}")
    {
        Settings = new JsonYapaSettings(new MemoryEnvironment { Settings = settingsJson }, new NewtonsoftJson());
        EngineSettings = new PomodoroEngineSettings(Settings);
        Engine = new PomodoroEngine(EngineSettings, Timer, Clock, new Inline(), Settings, Substitute.For<IPomodoroRepository>());
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
    private static (Rig, TimerViewModel) Create()
    {
        var rig = new Rig();
        var vm = new TimerViewModel(rig.Engine, rig.EngineSettings, new AvaloniaYapaThemeSettings(rig.Settings), Substitute.For<IAppNavigator>());
        return (rig, vm);
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
