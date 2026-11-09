using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;

namespace YAPA.Avalonia.Tests.Audio;

[TestClass]
public class CuePlayerTests
{
    private string _cacheDir = "";
    private FakeLauncher _launcher = null!;
    private List<FakeCueStream> _streams = null!;
    private ConcurrentQueue<CueTelemetry> _telemetry = null!;

    [TestInitialize]
    public void Setup()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"yapa-cue-test-{Guid.NewGuid():N}");
        _launcher = new FakeLauncher();
        _streams = new List<FakeCueStream>();
        _telemetry = new ConcurrentQueue<CueTelemetry>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    private ProcessCueBackend Fallback()
        => new(new ExecutableResolver(new[] { "/usr/bin" }, p => p == "/usr/bin/pw-play"), _launcher, _cacheDir);

    private CuePlayer Create(bool streamWorks = true, TimeSpan? writeDelay = null, TimeSpan? idleClose = null)
    {
        CueStreamFactory factory = (int rate, int channels, out string error) =>
        {
            error = streamWorks ? "" : "Connection refused";
            if (!streamWorks) return null;
            var stream = new FakeCueStream(rate, channels) { WriteDelay = writeDelay ?? TimeSpan.Zero };
            lock (_streams) _streams.Add(stream);
            return stream;
        };
        var player = new CuePlayer(new SoundLibrary(_ => null), factory, "fake stream", Fallback(), idleClose);
        player.CueFinished += _telemetry.Enqueue;
        return player;
    }

    private static PcmBuffer Constant(float value, int frames, int rate = 48000, int channels = 2)
        => new(Enumerable.Repeat(value, frames * channels).ToArray(), rate, channels);

    [TestMethod]
    public void PlaysWholeCueWithVolumeAppliedAndDrains()
    {
        using var player = Create();
        player.PlayPcm(Constant(0.5f, 4800), volume: 0.5);

        Assert.IsTrue(Wait.Until(() => _telemetry.Count == 1));
        var stream = _streams.Single();
        Assert.AreEqual(9600, stream.Written.Count);
        Assert.AreEqual(0.5f * (float)VolumeCurve.ToGain(0.5), stream.Written[100], 1e-6f);
        CollectionAssert.AreEqual(new[] { "write", "drain" }, stream.Events.ToArray());
        Assert.IsTrue(_telemetry.Single().Drained);
        Assert.AreEqual("fake stream", player.BackendDescription);
    }

    [TestMethod]
    public void OutputIsClampedNotClipped()
    {
        using var player = Create();
        player.PlayPcm(new PcmBuffer(new[] { 3f, -3f, 0.25f, 0f }, 48000, 2), volume: 1);

        Assert.IsTrue(Wait.Until(() => _telemetry.Count == 1));
        CollectionAssert.AreEqual(new[] { 1f, -1f, 0.25f, 0f }, _streams.Single().Written);
    }

    [TestMethod]
    public void NewCueInterruptsWithFlushAndReusesTheStream()
    {
        // Writes paced at 10 ms per 10 ms chunk, like a real server: a 2 s cue takes 2 s to write.
        using var player = Create(writeDelay: TimeSpan.FromMilliseconds(10));
        player.PlayPcm(Constant(0.1f, 96000), volume: 1);
        Assert.IsTrue(Wait.Until(() => _streams.Count == 1 && _streams[0].Written.Count > 0));

        player.PlayPcm(Constant(0.2f, 480), volume: 1);

        Assert.IsTrue(Wait.Until(() => _telemetry.Count == 2));
        var stream = _streams.Single();
        var first = _telemetry.First();
        Assert.IsTrue(first.Interrupted);
        Assert.IsTrue(first.Total < TimeSpan.FromSeconds(1), $"interrupt took {first.Total}");
        CollectionAssert.AreEqual(new[] { "write", "flush", "write", "drain" }, stream.Events.ToArray());
        Assert.AreEqual(0.2f, stream.Written[^1], 1e-6f, "the second cue was played");
    }

    [TestMethod]
    public void StopFlushes()
    {
        using var player = Create(writeDelay: TimeSpan.FromMilliseconds(10));
        player.PlayPcm(Constant(0.1f, 96000), volume: 1);
        Assert.IsTrue(Wait.Until(() => _streams.Count == 1 && _streams[0].Written.Count > 0));

        player.Stop();

        Assert.IsTrue(Wait.Until(() => _streams[0].Events.Contains("flush")));
        Assert.IsTrue(_telemetry.Single().Interrupted);
    }

    [TestMethod]
    public void StreamClosesAfterIdle()
    {
        using var player = Create(idleClose: TimeSpan.FromMilliseconds(100));
        player.PlayPcm(Constant(0.1f, 480), volume: 1);
        Assert.IsTrue(Wait.Until(() => _telemetry.Count == 1));
        Assert.IsTrue(Wait.Until(() => !player.IsStreamOpen));
        Assert.IsTrue(_streams.Single().Disposed);

        // ...and a later cue opens a fresh one.
        player.PlayPcm(Constant(0.1f, 480), volume: 1);
        Assert.IsTrue(Wait.Until(() => _telemetry.Count == 2));
        Assert.AreEqual(2, _streams.Count);
    }

    [TestMethod]
    public void FormatChangeReopensStream()
    {
        using var player = Create();
        player.PlayPcm(Constant(0.1f, 480, rate: 48000), volume: 1);
        Assert.IsTrue(Wait.Until(() => _telemetry.Count == 1));
        player.PlayPcm(Constant(0.1f, 441, rate: 44100), volume: 1);
        Assert.IsTrue(Wait.Until(() => _telemetry.Count == 2));

        Assert.AreEqual(2, _streams.Count);
        Assert.IsTrue(_streams[0].Disposed);
        Assert.AreEqual(44100, _streams[1].SampleRate);
    }

    [TestMethod]
    public void ConnectFailure_FallsBackToProcessWithBakedWav()
    {
        using var player = Create(streamWorks: false);
        player.Play(SoundRef.BuiltIn(BuiltInSound.SoftTap), volume: 0.5);

        Assert.IsTrue(Wait.Until(() => _launcher.Started.Count == 1));
        var command = _launcher.Last.Command;
        Assert.AreEqual("pw-play", command.Name);
        var wavPath = command.Arguments.Single();
        StringAssert.StartsWith(wavPath, _cacheDir);
        StringAssert.EndsWith(wavPath, "builtin-soft-tap-v050.wav");

        Assert.IsTrue(WavDecoder.TryDecode(File.ReadAllBytes(wavPath), out var baked, out _));
        var expectedPeak = SoundSynthesizer.Render(BuiltInSound.SoftTap)!.Peak() * VolumeCurve.ToGain(0.5);
        Assert.AreEqual(expectedPeak, baked.Peak(), 1e-3, "volume is baked into the fallback WAV");
        Assert.AreEqual("pw-play", player.BackendDescription);
    }

    [TestMethod]
    public void NoneSound_PlaysNothing()
    {
        using var player = Create();
        player.Play(SoundRef.None, 1);
        player.Play(SoundRef.File("/nonexistent/x.wav"), 1);
        System.Threading.Thread.Sleep(100);

        Assert.AreEqual(0, _streams.Count);
        Assert.AreEqual(0, _launcher.Started.Count);
    }

    [TestMethod]
    public void UndecodableFile_GoesToMediaPlayer()
    {
        Directory.CreateDirectory(_cacheDir);
        var mp3 = Path.Combine(_cacheDir, "gong.mp3");
        File.WriteAllBytes(mp3, new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        var resolver = new ExecutableResolver(new[] { "/usr/bin" }, p => p is "/usr/bin/pw-play" or "/usr/bin/ffplay");
        using var player = new CuePlayer(new SoundLibrary(_ => null), null, "unused",
            new ProcessCueBackend(resolver, _launcher, _cacheDir));

        player.Play(SoundRef.File(mp3), volume: 1);

        Assert.IsTrue(Wait.Until(() => _launcher.Started.Count == 1));
        Assert.AreEqual("ffplay", _launcher.Last.Command.Name);
    }

    [TestMethod]
    public void DisposeIsPromptAndIdempotent()
    {
        var player = Create(writeDelay: TimeSpan.FromMilliseconds(10));
        player.PlayPcm(Constant(0.1f, 480_000), volume: 1);
        Assert.IsTrue(Wait.Until(() => _streams.Count == 1 && _streams[0].Written.Count > 0));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        player.Dispose();
        player.Dispose();
        player.Play(SoundRef.BuiltIn(BuiltInSound.Bell), 1);

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1), $"dispose took {sw.Elapsed}");
        Assert.IsTrue(_streams[0].Disposed);
    }
}
