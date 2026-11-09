using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;

namespace YAPA.Avalonia.Tests.Audio;

[TestClass]
public class SoundSynthesizerTests
{
    private static readonly BuiltInSound[] Synthesized = Enum.GetValues<BuiltInSound>()
        .Where(SoundSynthesizer.CanSynthesize).ToArray();

    [TestMethod]
    public void OnlyClassicSoundsAreNotSynthesized()
    {
        CollectionAssert.AreEquivalent(
            new[] { BuiltInSound.ChimeUp, BuiltInSound.ChimeDown, BuiltInSound.SoftTap, BuiltInSound.Bell, BuiltInSound.BrightBell },
            Synthesized);
    }

    [TestMethod]
    public void LengthIsCueSized()
    {
        foreach (var sound in Synthesized)
        {
            var pcm = SoundSynthesizer.Render(sound)!;
            Assert.IsTrue(pcm.Duration >= TimeSpan.FromMilliseconds(150) && pcm.Duration <= TimeSpan.FromMilliseconds(900),
                $"{sound}: {pcm.Duration.TotalMilliseconds} ms");
            Assert.AreEqual(SoundSynthesizer.SampleRate, pcm.SampleRate);
            Assert.AreEqual(2, pcm.Channels);
        }
    }

    [TestMethod]
    public void PeakIsAboutMinus3dBFS()
    {
        foreach (var sound in Synthesized)
        {
            var peak = SoundSynthesizer.Render(sound)!.Peak();
            Assert.IsTrue(peak <= 0.75f, $"{sound} peak {peak}");
            Assert.IsTrue(peak >= 0.65f, $"{sound} peak {peak} is too quiet");
        }
    }

    [TestMethod]
    public void StartsAndEndsAtSilence_SoNoClicks()
    {
        foreach (var sound in Synthesized)
        {
            var s = SoundSynthesizer.Render(sound)!.Samples;
            Assert.AreEqual(0f, s[0], 1e-6f, $"{sound} first L");
            Assert.AreEqual(0f, s[1], 1e-6f, $"{sound} first R");
            Assert.AreEqual(0f, s[^1], 1e-6f, $"{sound} last R");
            Assert.AreEqual(0f, s[^2], 1e-6f, $"{sound} last L");

            // A click is a jump from silence. The ramp must be audible-speed gradual at both ends,
            // not just zero at the single edge sample.
            var ms = SoundSynthesizer.SampleRate / 1000 * 2; // interleaved samples per millisecond
            Assert.IsTrue(s.Take(ms / 4).All(v => Math.Abs(v) < 0.05f), $"{sound} attack edge");
            Assert.IsTrue(s.Skip(s.Length - ms).All(v => Math.Abs(v) < 0.01f), $"{sound} release edge");
        }
    }

    [TestMethod]
    public void IsDeterministic()
    {
        foreach (var sound in Synthesized)
            CollectionAssert.AreEqual(SoundSynthesizer.Render(sound)!.Samples, SoundSynthesizer.Render(sound)!.Samples, sound.ToString());
    }

    [TestMethod]
    public void SoundsAreDistinct()
    {
        var rendered = Synthesized.Select(s => SoundSynthesizer.Render(s)!).ToArray();
        for (var i = 0; i < rendered.Length; i++)
            for (var j = i + 1; j < rendered.Length; j++)
                Assert.IsTrue(rendered[i].Samples.Length != rendered[j].Samples.Length
                              || !rendered[i].Samples.SequenceEqual(rendered[j].Samples),
                    $"{Synthesized[i]} and {Synthesized[j]} are identical");
    }

    [TestMethod]
    public void LibraryCachesRenderedBuffers()
    {
        var library = new SoundLibrary(_ => null);
        var first = library.Load(SoundRef.BuiltIn(BuiltInSound.Bell), out _);
        var second = library.Load(SoundRef.BuiltIn(BuiltInSound.Bell), out _);
        Assert.IsNotNull(first);
        Assert.AreSame(first.Pcm, second!.Pcm);
        Assert.AreEqual("builtin-bell", first.CacheKey);
    }

    [TestMethod]
    public void LibraryLoadsClassicSounds()
    {
        var tick = SoundLibrary.Shared.Load(SoundRef.BuiltIn(BuiltInSound.ClassicTick), out _);
        var ding = SoundLibrary.Shared.Load(SoundRef.BuiltIn(BuiltInSound.ClassicDing), out _);
        Assert.IsNotNull(tick);
        Assert.IsNotNull(ding);
        Assert.AreNotEqual(tick.Pcm.FrameCount, ding.Pcm.FrameCount);
    }

    [TestMethod]
    public void LibraryPassesThroughUndecodableFiles()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"yapa-test-{Guid.NewGuid():N}.mp3");
        System.IO.File.WriteAllBytes(path, new byte[] { 0xFF, 0xFB, 0x90, 0x00, 1, 2, 3 });
        try
        {
            var library = new SoundLibrary(_ => null);
            Assert.IsNull(library.Load(SoundRef.File(path), out var passthrough));
            Assert.AreEqual(path, passthrough);

            Assert.IsNull(library.Load(SoundRef.File(path + ".missing"), out passthrough));
            Assert.IsNull(passthrough, "a missing file is not handed to a player");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}

[TestClass]
public class VolumeCurveTests
{
    [TestMethod]
    public void EndpointsAndClamping()
    {
        Assert.AreEqual(0, VolumeCurve.ToGain(0));
        Assert.AreEqual(1, VolumeCurve.ToGain(1));
        Assert.AreEqual(0, VolumeCurve.ToGain(-0.5));
        Assert.AreEqual(1, VolumeCurve.ToGain(7));
        Assert.AreEqual(0, VolumeCurve.ToGain(double.NaN));
        Assert.AreEqual(1, VolumeCurve.ToGain(double.PositiveInfinity));
    }

    [TestMethod]
    public void IsMonotonic()
    {
        var previous = -1.0;
        for (var v = 0.0; v <= 1.0; v += 0.01)
        {
            var gain = VolumeCurve.ToGain(v);
            Assert.IsTrue(gain >= previous);
            previous = gain;
        }
    }

    [TestMethod]
    public void HalfwayIsAboutHalfAsLoud()
    {
        // -10 dB is the conventional "half as loud"; a linear slider would give -6 dB.
        Assert.AreEqual(-10.0, VolumeCurve.ToDecibels(0.5), 0.1);
        Assert.IsTrue(VolumeCurve.ToDecibels(0.1) < -30);
    }
}
