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
    public WindDown? LastWindDown;

    public void Play(AmbienceMix mix, TickAnchor? ticks, WindDown? windDown = null)
    {
        LastWindDown = windDown;
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
    public void The_wind_down_is_aimed_at_the_end_of_the_interval()
    {
        var (rig, ambience, player, _) = Create();
        rig.Engine.Start();
        var end = rig.Clock.Monotonic + TimeSpan.FromMinutes(25);
        Assert.AreEqual(end, player.LastWindDown!.Value.EndsAt);
        Assert.AreEqual(WindDown.Longest, player.LastWindDown.Value.Length);

        rig.Elapse(600);
        rig.Engine.Pause();
        rig.Clock.Advance(300); // five minutes paused don't count
        rig.Engine.Start();
        Assert.AreEqual(end + TimeSpan.FromMinutes(5), player.LastWindDown!.Value.EndsAt, "re-aimed on resume");

        ambience.WindDownBeforeEnd = false;
        Assert.IsNull(player.LastWindDown, "switched off, it plays at full level to the end");
    }

    [TestMethod]
    public void A_short_interval_winds_down_for_a_quarter_of_its_length()
    {
        var w = WindDown.ForInterval(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));
        Assert.AreEqual(TimeSpan.FromSeconds(30), w.Length);
    }

    [TestMethod]
    public void Wind_down_envelope_eases_from_full_to_the_floor()
    {
        var w = new WindDown(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(60));
        Assert.AreEqual(1f, w.GainAt(TimeSpan.FromSeconds(30)), "before the last minute");
        Assert.AreEqual(1f, w.GainAt(TimeSpan.FromSeconds(40)), 1e-6, "where it starts there's no corner");
        Assert.AreEqual(WindDown.Floor + (1 - WindDown.Floor) / 2, w.GainAt(TimeSpan.FromSeconds(70)), 1e-6);
        Assert.AreEqual(WindDown.Floor, w.GainAt(TimeSpan.FromSeconds(100)), 1e-6);
        Assert.AreEqual(WindDown.Floor, w.GainAt(TimeSpan.FromSeconds(105)), "past the end it stays on the floor until the fade");

        var previous = 1f;
        for (var t = 40.0; t <= 100; t += 0.02)
        {
            var g = w.GainAt(TimeSpan.FromSeconds(t));
            Assert.IsTrue(g <= previous + 1e-6, $"rises at {t}");
            Assert.IsTrue(previous - g < 0.001f, $"steps {previous - g} at {t}: audible");
            previous = g;
        }
    }

    [TestMethod]
    public void Mixer_output_thins_by_about_ten_decibels_over_the_last_minute()
    {
        const int rate = 48_000;
        var mixer = new AmbienceMixer(rate, s => AmbienceGenerators.Create(s.Sound, rate, 11));
        var end = TimeSpan.FromSeconds(70);
        mixer.Set(true, AmbientSound.SteadyRain, 1f, 0f, windDown: new WindDown(end, TimeSpan.FromSeconds(60)));

        var block = new float[960 * 2];
        var t = TimeSpan.Zero;
        double Rms(double seconds)
        {
            double sum = 0; long n = 0;
            for (var i = 0; i < seconds / 0.02; i++)
            {
                mixer.Render(block, t);
                t += TimeSpan.FromSeconds(0.02);
                foreach (var x in block) { sum += x * x; n++; }
            }
            return Math.Sqrt(sum / n);
        }

        Rms(4);                    // fade in: 0-4 s
        var full = Rms(4);         // 4-8 s: full level
        Rms(61);                   // 8-69 s: into the last second
        var last = Rms(1);         // 69-70 s
        var drop = 20 * Math.Log10(last / full);
        Assert.IsTrue(drop < -9 && drop > -12, $"dropped {drop:F1} dB");
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
    public void Without_a_decoder_a_recording_goes_to_the_media_player_on_loop()
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

    [TestMethod]
    public void A_recording_plays_in_the_stream_and_follows_the_volume_slider_live()
    {
        var file = System.IO.Path.GetTempFileName();
        try
        {
            var stream = new RecordingStream();
            var music = Substitute.For<IBackgroundMusicPlayer>();
            var opened = new List<string>();
            using var player = new AmbiencePlayer(music, () => stream,
                openRecording: path => { opened.Add(path); return new ConstantBed(0.5f); });

            player.Play(new AmbienceMix(AmbientSound.File, file, 1, false, 0), null);
            var loud = LevelAfterSettling(stream);
            player.Play(new AmbienceMix(AmbientSound.File, file, 0.3, false, 0), null);
            var quiet = LevelAfterSettling(stream);

            CollectionAssert.AreEqual(new[] { file }, opened, "decoded once, not restarted for a volume change");
            music.DidNotReceive().Play(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<double>());
            Assert.AreEqual(0.5, loud, 0.01, "full volume plays the recording as recorded");
            Assert.AreEqual(0.5 * VolumeCurve.ToGain(0.3), quiet, 0.01, "the slider changes the level while it plays");
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }

    /// <summary>Waits past the fade-in and volume glide, then reads the level of the latest block.</summary>
    private static double LevelAfterSettling(RecordingStream stream)
    {
        int start;
        lock (stream.Samples) start = stream.Samples.Count;
        // 3 s of audio: longer than the 2.5 s fade-in and the 0.4 s glide.
        SpinWait.SpinUntil(() => { lock (stream.Samples) return stream.Samples.Count > start + 48_000 * 2 * 3; }, 10_000);
        lock (stream.Samples) return stream.Samples.Skip(stream.Samples.Count - 960).Average(Math.Abs);
    }

    private sealed class ConstantBed(float level) : IAmbienceGenerator
    {
        public void MixInto(Span<float> stereo, float gain)
        {
            for (var i = 0; i < stereo.Length; i++) stereo[i] += level * gain;
        }
    }
}
