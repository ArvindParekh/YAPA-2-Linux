using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Ambience;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Tests.AppLogic;

namespace YAPA.Avalonia.Tests.Audio;

internal sealed class FakeAmbiencePlayer : IAmbiencePlayer
{
    public readonly List<string> Calls = new();
    public AmbienceMix? Last;
    public bool Playing;

    public TickAnchor? LastAnchor;

    public void Play(AmbienceMix mix, TickAnchor? ticks)
    {
        Calls.Add(ticks.HasValue ? "play+retime" : "play");
        if (ticks.HasValue) LastAnchor = ticks;
        Last = mix;
        Playing = true;
    }

    public void Stop()
    {
        Calls.Add("stop");
        Playing = false;
    }

    public string BackendDescription => "fake";
    public void Dispose() { }
}

[TestClass]
public class AmbienceEffectsTests
{
    private static (Rig Rig, AmbienceSettings Ambience, FakeAmbiencePlayer Player, AmbienceEffects Effects) Create()
    {
        var rig = new Rig();
        var ambience = new AmbienceSettings(rig.Settings) { Sound = AmbientSound.SteadyRain, Ticking = true };
        var player = new FakeAmbiencePlayer();
        var effects = new AmbienceEffects(rig.Engine, ambience, rig.EngineSettings, player, rig.Settings, rig.Clock);
        player.Calls.Clear();
        return (rig, ambience, player, effects);
    }

    [TestMethod]
    public void Plays_while_working_and_fades_on_pause_stop_and_completion()
    {
        var (rig, _, player, _) = Create();
        rig.Engine.Start();
        Assert.IsTrue(player.Playing);
        Assert.AreEqual("play+retime", player.Calls.Last(), "a new pomodoro sets the tick timing");
        Assert.AreEqual(rig.Engine.NextSecondAt, player.LastAnchor!.Value.NextBoundary, "ticks follow the engine's seconds");

        rig.Engine.Pause();
        Assert.IsFalse(player.Playing);
        rig.Engine.Start();
        Assert.AreEqual("play+retime", player.Calls.Last(), "resuming re-times the ticks");

        rig.Elapse(25 * 60);
        Assert.IsFalse(player.Playing, "silence marks the end of the pomodoro");
    }

    [TestMethod]
    public void Breaks_are_quiet_unless_asked_and_never_tick()
    {
        var (rig, ambience, player, _) = Create();
        rig.Engine.Start();
        rig.Elapse(25 * 60);
        rig.Engine.Start(); // break
        Assert.IsFalse(player.Playing);

        ambience.DuringBreaks = true;
        Assert.IsTrue(player.Playing);
        Assert.IsFalse(player.Last!.Ticking, "no ticking clock while resting");
        Assert.AreEqual(AmbientSound.SteadyRain, player.Last.Sound);
    }

    [TestMethod]
    public void Settings_changes_apply_live()
    {
        var (rig, ambience, player, _) = Create();
        rig.Engine.Start();
        ambience.Sound = AmbientSound.Downpour;
        Assert.AreEqual(AmbientSound.Downpour, player.Last!.Sound);
        Assert.AreEqual("play", player.Calls.Last(), "changing the sound keeps the tick timing");
        ambience.Volume = 0.2;
        Assert.AreEqual(0.2, player.Last.Volume);
    }

    [TestMethod]
    public void Quick_switch_and_master_mute_silence_it()
    {
        var (rig, ambience, player, _) = Create();
        rig.Engine.Start();
        ambience.Enabled = false;
        Assert.IsFalse(player.Playing);
        ambience.Enabled = true;
        Assert.IsTrue(player.Playing);

        rig.EngineSettings.DisableSoundNotifications = true;
        Assert.IsFalse(player.Playing, "'Play sounds' off means no ambience either");
    }

    [TestMethod]
    public void Nothing_configured_means_nothing_plays()
    {
        var (rig, ambience, player, _) = Create();
        ambience.Sound = AmbientSound.None;
        ambience.Ticking = false;
        rig.Engine.Start();
        Assert.IsFalse(player.Playing);
    }

    [TestMethod]
    public void Preview_plays_without_a_timer_and_stops_cleanly()
    {
        var (_, _, player, effects) = Create();
        effects.Preview();
        Assert.IsTrue(player.Playing);
        Assert.IsTrue(effects.IsPreviewing);
        effects.StopPreview();
        Assert.IsFalse(player.Playing, "timer is idle, so stopping the preview means silence");
    }
}

/// <summary>The real player thread against a fake output stream.</summary>
[TestClass]
public class AmbiencePlayerTests
{
    private sealed class RecordingStream : ICueOutputStream
    {
        public readonly List<float> Samples = new();
        public int SampleRate => 48_000;
        public int Channels => 2;
        public bool Disposed;

        public bool Write(ReadOnlySpan<float> interleaved, out string error)
        {
            lock (Samples) Samples.AddRange(interleaved.ToArray());
            Thread.Sleep(2); // a real stream blocks; don't spin
            error = "";
            return true;
        }

        public bool Flush() => true;
        public bool Drain() => true;
        public TimeSpan? Latency => TimeSpan.Zero;
        public void Dispose() => Disposed = true;
    }

    [TestMethod]
    public void Play_streams_sound_and_stop_fades_to_silence()
    {
        var stream = new RecordingStream();
        using var player = new AmbiencePlayer(Substitute.For<IBackgroundMusicPlayer>(), () => stream);

        player.Play(new AmbienceMix(AmbientSound.SteadyRain, null, 1, true, 1), new TickAnchor(System.Diagnostics.Stopwatch.GetElapsedTime(0), 0));
        SpinWait.SpinUntil(() => { lock (stream.Samples) return stream.Samples.Count > 48_000 * 2 * 3; }, 5000);
        float[] playing;
        lock (stream.Samples) playing = stream.Samples.ToArray();
        Assert.IsTrue(playing.Skip(playing.Length / 2).Any(x => Math.Abs(x) > 0.01), "rain is audible once faded in");

        player.Stop();
        Thread.Sleep(2500); // longer than the fade-out
        int mark;
        lock (stream.Samples) mark = stream.Samples.Count;
        Thread.Sleep(200);
        float[] after;
        lock (stream.Samples) after = stream.Samples.Skip(mark).ToArray();
        Assert.IsTrue(after.All(x => Math.Abs(x) < 1e-6), "silent after the fade-out");
    }

    [TestMethod]
    public void User_file_goes_to_the_media_player_on_loop()
    {
        var file = System.IO.Path.GetTempFileName();
        try
        {
            var music = Substitute.For<IBackgroundMusicPlayer>();
            using var player = new AmbiencePlayer(music, () => new RecordingStream());
            player.Play(new AmbienceMix(AmbientSound.File, file, 0.6, false, 0), null);
            music.Received(1).Play(file, true, 0.6);
            player.Stop();
            music.Received().Stop();
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }

    [TestMethod]
    public void Server_not_up_yet_falls_back_then_moves_to_the_stream()
    {
        // Launched at login a moment before PipeWire: the first open fails.
        var attempts = 0;
        var stream = new RecordingStream();
        var music = Substitute.For<IBackgroundMusicPlayer>();
        using var player = new AmbiencePlayer(music, () => ++attempts == 1 ? null : stream, retryAfter: TimeSpan.FromMilliseconds(300));

        player.Play(new AmbienceMix(AmbientSound.SoftRain, null, 1, false, 0), null);
        SpinWait.SpinUntil(() => music.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "Play"), 5000);
        music.Received().Play(Arg.Is<string>(p => p.EndsWith(".wav")), true, Arg.Any<double>());

        // After the retry period it plays through the stream, and the fallback stops.
        SpinWait.SpinUntil(() => { lock (stream.Samples) return stream.Samples.Count > 48_000; }, 5000);
        Assert.IsTrue(attempts >= 2, "retried the stream");
        music.Received().Stop();
        lock (stream.Samples) Assert.IsTrue(stream.Samples.Count > 48_000, "streaming after the retry");
    }
}
