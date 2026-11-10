using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Ambience;

namespace YAPA.Avalonia.Tests.Audio;

/// <summary>
/// The ambience can't be judged by ear in CI, so these pin down what makes it
/// pleasant or broken by measurement: level, spectral balance (rain must be
/// bright-ish noise, brown noise dark), no DC, no clipping, no clicks during
/// fades, ticks exactly one second apart. Set YAPA_AMBIENCE_RENDER_DIR to write
/// each sound to a WAV for listening.
/// </summary>
[TestClass]
public class AmbienceTests
{
    private const int Rate = 48_000;

    private static float[] Render(IAmbienceGenerator gen, double seconds, float gain = 1f)
    {
        var buffer = new float[(int)(Rate * seconds) * 2];
        // Render in realistic 20 ms blocks so block-boundary bugs show up.
        const int block = 960 * 2;
        for (var i = 0; i < buffer.Length; i += block)
            gen.MixInto(buffer.AsSpan(i, Math.Min(block, buffer.Length - i)), gain);
        return buffer;
    }

    private static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return Math.Sqrt(sum / s.Length);
    }

    private static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));

    /// <summary>Energy share above and below ~1.5 kHz, via a one-pole split (cheap and good enough to tell bright from dark).</summary>
    private static (double Low, double High) BandShare(float[] stereo)
    {
        var a = Math.Exp(-2 * Math.PI * 1500 / Rate);
        double y = 0, low = 0, high = 0;
        for (var i = 0; i < stereo.Length; i += 2)
        {
            double x = stereo[i];
            y = x + a * (y - x);
            low += y * y;
            high += (x - y) * (x - y);
        }
        var total = low + high;
        return (low / total, high / total);
    }

    private static void MaybeSave(string name, float[] stereo)
    {
        if (Environment.GetEnvironmentVariable("YAPA_AMBIENCE_RENDER_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        WavWriter.WritePcm16(Path.Combine(dir, name + ".wav"), new PcmBuffer(stereo, Rate, 2));
    }

    [DataTestMethod]
    [DataRow(AmbientSound.SoftRain)]
    [DataRow(AmbientSound.SteadyRain)]
    [DataRow(AmbientSound.Downpour)]
    [DataRow(AmbientSound.BrownNoise)]
    public void Generated_sound_has_a_sane_level_and_never_clips(AmbientSound sound)
    {
        var gen = AmbienceGenerators.Create(sound, Rate)!;
        var audio = Render(gen, 30);
        MaybeSave(sound.ToString(), audio);

        Assert.IsTrue(audio.All(float.IsFinite), "no NaN or infinity");
        var rms = Db(Rms(audio));
        var peak = Db(audio.Max(Math.Abs));
        var mean = audio.Average();
        Console.WriteLine($"{sound}: RMS {rms:F1} dBFS, peak {peak:F1} dBFS, DC {mean:E2}, bands {BandShare(audio)}");

        // A comfortable background level at full volume, leaving room for cues on top.
        Assert.IsTrue(rms is > -30 and < -16, $"RMS {rms:F1} dBFS out of range");
        Assert.IsTrue(peak < -1, $"peak {peak:F1} dBFS would clip after the volume curve");
        Assert.IsTrue(Math.Abs(mean) < 0.01, "DC offset wastes headroom and thumps on start");
    }

    [TestMethod]
    public void Rain_is_broadband_and_brown_noise_is_dark()
    {
        var (_, rainHigh) = BandShare(Render(AmbienceGenerators.Create(AmbientSound.SteadyRain, Rate)!, 10));
        var (_, brownHigh) = BandShare(Render(AmbienceGenerators.Create(AmbientSound.BrownNoise, Rate)!, 10));
        Console.WriteLine($"high-band share: rain {rainHigh:P0}, brown {brownHigh:P0}");
        Assert.IsTrue(rainHigh > 0.25, "rain needs its hiss, or it sounds like a hum");
        Assert.IsTrue(brownHigh < 0.05, "brown noise must stay deep");
    }

    [TestMethod]
    public void Heavier_rain_is_louder_and_denser()
    {
        var soft = Rms(Render(AmbienceGenerators.Create(AmbientSound.SoftRain, Rate)!, 10));
        var steady = Rms(Render(AmbienceGenerators.Create(AmbientSound.SteadyRain, Rate)!, 10));
        var pour = Rms(Render(AmbienceGenerators.Create(AmbientSound.Downpour, Rate)!, 10));
        Assert.IsTrue(soft < steady && steady < pour, $"{Db(soft):F1} < {Db(steady):F1} < {Db(pour):F1}");
    }

    [TestMethod]
    public void Rain_channels_are_decorrelated_so_it_sounds_wide()
    {
        var audio = Render(AmbienceGenerators.Create(AmbientSound.SteadyRain, Rate)!, 5);
        double lr = 0, ll = 0, rr = 0;
        for (var i = 0; i < audio.Length; i += 2)
        {
            lr += audio[i] * audio[i + 1];
            ll += audio[i] * audio[i];
            rr += audio[i + 1] * audio[i + 1];
        }
        var correlation = lr / Math.Sqrt(ll * rr);
        Console.WriteLine($"L/R correlation {correlation:F2}");
        Assert.IsTrue(Math.Abs(correlation) < 0.5);
    }

    [TestMethod]
    public void Same_seed_same_sound()
    {
        var a = Render(new RainGenerator(RainPreset.Steady, Rate, seed: 42), 1);
        var b = Render(new RainGenerator(RainPreset.Steady, Rate, seed: 42), 1);
        CollectionAssert.AreEqual(a, b);
    }

    private static float[] RenderTicks(TickAnchor anchor, TimeSpan heardAt, double seconds, int blockFrames = 960)
    {
        var gen = new TickGenerator(Rate);
        var buffer = new float[(int)(Rate * seconds) * 2];
        for (var f = 0; f < buffer.Length / 2; f += blockFrames)
        {
            var n = Math.Min(blockFrames, buffer.Length / 2 - f);
            gen.MixInto(buffer.AsSpan(f * 2, n * 2), 1f, heardAt + TimeSpan.FromSeconds(f / (double)Rate), anchor);
        }
        return buffer;
    }

    private static int FirstOnset(float[] stereo, int fromFrame)
    {
        for (var f = fromFrame; f < stereo.Length / 2; f++)
            if (Math.Abs(stereo[f * 2]) > 0.005) return f;
        return -1;
    }

    [TestMethod]
    public void Ticks_are_heard_exactly_when_the_timer_second_changes()
    {
        // The timer's next second starts 0.37 s into this audio, then every second after.
        var anchor = new TickAnchor(TimeSpan.FromSeconds(100.37), 41);
        var audio = RenderTicks(anchor, heardAt: TimeSpan.FromSeconds(100), seconds: 3.5);
        MaybeSave("Ticking", audio);

        var onsets = new[] { FirstOnset(audio, 0), FirstOnset(audio, (int)(Rate * 1.1)), FirstOnset(audio, (int)(Rate * 2.1)) };
        var expected = new[] { 0.37, 1.37, 2.37 }.Select(t => (int)Math.Round(t * Rate)).ToArray();
        for (var i = 0; i < 3; i++)
            Assert.IsTrue(Math.Abs(onsets[i] - expected[i]) <= 2, $"tick {i} at frame {onsets[i]}, expected {expected[i]}");

        // Alternation follows the second's parity, not the audio's start.
        var first = audio.AsSpan(expected[0] * 2, 400).ToArray();
        var second = audio.AsSpan(expected[1] * 2, 400).ToArray();
        CollectionAssert.AreNotEqual(first, second, "tick and tock alternate");
    }

    [TestMethod]
    public void A_tick_straddling_two_audio_blocks_is_rendered_once_and_whole()
    {
        // Boundary 5 frames before the end of the first 960-frame block.
        var anchor = new TickAnchor(TimeSpan.FromSeconds(955 / (double)Rate), 0);
        var inBlocks = RenderTicks(anchor, TimeSpan.Zero, 0.2, blockFrames: 960);
        var inOne = RenderTicks(anchor, TimeSpan.Zero, 0.2, blockFrames: 48_000);
        CollectionAssert.AreEqual(inOne, inBlocks);
    }

    [TestMethod]
    public void Tick_level_and_shape()
    {
        var audio = RenderTicks(new TickAnchor(TimeSpan.Zero, 0), TimeSpan.Zero, 4.5);
        var peak = Db(audio.Max(Math.Abs));
        Assert.IsTrue(peak is > -18 and < -3, $"tick peak {peak:F1} dBFS");
        var silent = audio.Count(x => Math.Abs(x) < 1e-4) / (double)audio.Length;
        Assert.IsTrue(silent > 0.9, $"{silent:P0} silent: ticks are short");
    }

    [TestMethod]
    public void Mixer_fades_in_and_out_without_clicks()
    {
        var mixer = new AmbienceMixer(Rate, s => AmbienceGenerators.Create(s.Sound, Rate, 7));
        var block = new float[960 * 2];
        var all = new System.Collections.Generic.List<float>();

        var t = TimeSpan.Zero;
        void Render() { mixer.Render(block, t); all.AddRange(block); t += TimeSpan.FromSeconds(0.02); }
        mixer.Set(true, AmbientSound.SteadyRain, 1f, 1f, new TickAnchor(TimeSpan.FromSeconds(0.5), 0));
        for (var i = 0; i < 200; i++) Render(); // 4 s
        mixer.Set(true, AmbientSound.Downpour, 1f, 1f);
        for (var i = 0; i < 150; i++) Render(); // crossfade
        mixer.Set(false, AmbientSound.Downpour, 1f, 1f);
        for (var i = 0; i < 100; i++) Render(); // fade out

        var audio = all.ToArray();
        MaybeSave("MixerSession", audio);

        // The first 50 ms of a 2.5 s fade-in must be much quieter than steady state.
        var start = Rms(audio.AsSpan(0, Rate / 20 * 2));
        var steady = Rms(audio.AsSpan(Rate * 3 * 2, Rate / 2 * 2));
        Assert.IsTrue(Db(start) < Db(steady) - 20, $"fade-in starts at {Db(start):F1} vs {Db(steady):F1}");

        // After the fade-out everything is silent, and the mixer says so.
        var tail = audio.AsSpan(audio.Length - Rate / 10 * 2);
        Assert.IsTrue(Rms(tail) < 1e-6);
        Assert.IsFalse(mixer.WantsAudio);
    }

    [TestMethod]
    public void Mixer_wants_audio_only_while_active_or_fading()
    {
        var mixer = new AmbienceMixer(Rate);
        Assert.IsFalse(mixer.WantsAudio);
        mixer.Set(true, AmbientSound.None, 0, 1f);
        Assert.IsTrue(mixer.WantsAudio);
    }

    [TestMethod]
    public void Fallback_loop_is_seamless()
    {
        var loop = AmbienceLoop.Render(new AmbienceMix(AmbientSound.SteadyRain, null, 0.5, true, 0.5), seed: 3);
        Assert.AreEqual((int)(Rate * AmbienceLoop.Length.TotalSeconds) * 2, loop.Length);

        // The jump from the last sample back to the first must look like any other step.
        var steps = new double[loop.Length / 2 - 1];
        for (var f = 0; f + 1 < loop.Length / 2; f++) steps[f] = Math.Abs(loop[(f + 1) * 2] - loop[f * 2]);
        var typical = steps.OrderBy(x => x).ElementAt((int)(steps.Length * 0.999));
        var wrap = Math.Abs(loop[0] - loop[^2]);
        Assert.IsTrue(wrap <= typical * 1.5, $"wrap step {wrap:F4} vs 99.9th percentile {typical:F4}");
    }

    [TestMethod]
    public void Ambience_mix_audibility()
    {
        Assert.IsFalse(AmbienceMix.Silent.IsAudible);
        Assert.IsTrue(new AmbienceMix(AmbientSound.None, null, 0, true, 0.3).IsAudible);
        Assert.IsFalse(new AmbienceMix(AmbientSound.SoftRain, null, 0, false, 0).IsAudible);
    }
}

[TestClass]
public class AmbienceHardwareTests
{
    /// <summary>Opens the real "Ambience" stream and writes half a second of pure silence. Opt-in.</summary>
    [TestMethod]
    [TestCategory("AudioHardware")]
    public void Ambience_stream_opens_and_accepts_silence()
    {
        if (Environment.GetEnvironmentVariable("YAPA_AUDIO_SILENT_TEST") != "1") Assert.Inconclusive("set YAPA_AUDIO_SILENT_TEST=1");
        if (!YAPA.Avalonia.Audio.PulseAudio.PulseSimpleNative.TryLoad(out var reason)) Assert.Inconclusive(reason);

        using var stream = YAPA.Avalonia.Audio.PulseAudio.PulseSimpleStream.TryOpen(48_000, 2, "Ambience", TimeSpan.FromMilliseconds(300), out var error);
        Assert.IsNotNull(stream, error);
        var silence = new float[960 * 2];
        for (var i = 0; i < 25; i++) Assert.IsTrue(stream!.Write(silence, out error), error);
        Assert.IsTrue(stream!.Drain());
    }
}
