using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;

namespace YAPA.Avalonia.Tests.Audio;

[TestClass]
public class ProcessAudioPlayerTests
{
    private string _file = "";
    private FakeLauncher _launcher = null!;
    private ManualTime _time = null!;

    [TestInitialize]
    public void Setup()
    {
        _file = Path.Combine(Path.GetTempPath(), $"yapa-music-{Guid.NewGuid():N}.ogg");
        File.WriteAllBytes(_file, new byte[16]);
        _launcher = new FakeLauncher();
        _time = new ManualTime();
    }

    [TestCleanup]
    public void Cleanup() => File.Delete(_file);

    private ProcessAudioPlayer Create(params string[] tools)
        => new("music", (path, _) => tools.Select(t => new PlayerCommand("/usr/bin/" + t, t, new[] { path })).ToArray(), _launcher, _time);

    [TestMethod]
    public void RespawnGuard_GivesUpAfterThreeQuickExits()
    {
        using var player = Create("pw-play");
        player.Play(_file, loop: true, volume: 0.5);

        // A loop whose player exits immediately (say, the device is busy) but "successfully".
        for (var i = 0; i < 10 && player.IsPlaying; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            _launcher.Last.Exit(0);
        }

        Assert.AreEqual(ProcessAudioPlayer.MaxConsecutiveQuickExits, _launcher.Started.Count);
        Assert.IsFalse(player.IsPlaying);
    }

    [TestMethod]
    public void RespawnGuard_ResetsWhenPlaybackRunsNormally()
    {
        using var player = Create("pw-play");
        player.Play(_file, loop: true, volume: 0.5);

        // quick, quick, long, quick, quick: never three quick in a row.
        foreach (var ranMs in new[] { 100, 100, 30_000, 100, 100 })
        {
            _time.Advance(TimeSpan.FromMilliseconds(ranMs));
            _launcher.Last.Exit(0);
        }

        Assert.IsTrue(player.IsPlaying);
        Assert.AreEqual(6, _launcher.Started.Count);
    }

    [TestMethod]
    public void Loop_RestartsTheSameFileOnNormalExit()
    {
        using var player = Create("ffplay");
        player.Play(_file, loop: true, volume: 0.5);
        _time.Advance(TimeSpan.FromMinutes(3));
        _launcher.Last.Exit(0);

        Assert.AreEqual(2, _launcher.Started.Count);
        Assert.AreEqual(_launcher.Started[0].Command, _launcher.Started[1].Command);
        Assert.IsTrue(player.IsPlaying);
    }

    [TestMethod]
    public void NoLoop_EndsAfterOnePlay()
    {
        using var player = Create("ffplay");
        player.Play(_file, loop: false, volume: 0.5);
        _time.Advance(TimeSpan.FromSeconds(30));
        _launcher.Last.Exit(0);

        Assert.AreEqual(1, _launcher.Started.Count);
        Assert.IsFalse(player.IsPlaying);
    }

    [TestMethod]
    public void QuickFailure_FallsThroughToNextPlayer()
    {
        using var player = Create("pw-play", "ffplay", "gst-play-1.0");
        player.Play(_file, loop: false, volume: 0.5);

        _launcher.Last.LastErrorLine = "unsupported format";
        _launcher.Last.Exit(1);

        Assert.AreEqual(2, _launcher.Started.Count);
        Assert.AreEqual("ffplay", _launcher.Last.Command.Name);
        Assert.IsTrue(player.IsPlaying);
    }

    [TestMethod]
    public void UnstartableExecutable_FallsThroughToNextPlayer()
    {
        _launcher.Unstartable.Add("pw-play");
        using var player = Create("pw-play", "paplay");
        player.Play(_file, loop: false, volume: 0.5);

        Assert.AreEqual("paplay", _launcher.Last.Command.Name);
        Assert.IsTrue(player.IsPlaying);
    }

    [TestMethod]
    public void Stop_KillsAndDoesNotRestart()
    {
        using var player = Create("pw-play");
        player.Play(_file, loop: true, volume: 0.5);
        var process = _launcher.Last;

        player.Stop();

        Assert.IsTrue(process.Killed);
        Assert.AreEqual(1, _launcher.Started.Count, "the kill's exit notification must not trigger a loop restart");
        Assert.IsFalse(player.IsPlaying);
    }

    [TestMethod]
    public void Play_ReplacesCurrentPlayback()
    {
        using var player = Create("pw-play");
        player.Play(_file, loop: true, volume: 0.5);
        var first = _launcher.Last;
        player.Play(_file, loop: false, volume: 0.2);

        Assert.IsTrue(first.Killed);
        Assert.AreEqual(2, _launcher.Started.Count);
        Assert.IsTrue(player.IsPlaying);
    }

    [TestMethod]
    public void Dispose_KillsAndIgnoresLaterCalls()
    {
        var player = Create("pw-play");
        player.Play(_file, loop: true, volume: 0.5);
        var process = _launcher.Last;

        player.Dispose();
        player.Play(_file, loop: true, volume: 0.5);
        player.Stop();
        player.Dispose();

        Assert.IsTrue(process.Killed);
        Assert.IsTrue(process.Disposed);
        Assert.AreEqual(1, _launcher.Started.Count);
    }

    [TestMethod]
    public void MissingFileOrNoPlayer_IsSilentNoOp()
    {
        using var withPlayer = Create("pw-play");
        withPlayer.Play(_file + ".gone", loop: true, volume: 0.5);
        withPlayer.Play("", loop: true, volume: 0.5);

        using var withoutPlayer = Create();
        withoutPlayer.Play(_file, loop: true, volume: 0.5);

        Assert.AreEqual(0, _launcher.Started.Count);
        Assert.IsFalse(withPlayer.IsPlaying);
        Assert.IsFalse(withoutPlayer.IsPlaying);
    }
}

[TestClass]
public class PlayerCommandsTests
{
    private static readonly ExecutableResolver Everything = new(new[] { "/usr/bin" }, _ => true);

    private static PlayerCommand Find(IReadOnlyList<PlayerCommand> commands, string name)
        => commands.Single(c => c.Name == name);

    [TestMethod]
    public void EveryArgumentIsItsOwnToken()
    {
        var commands = PlayerCommands.ForMedia("/music/focus.ogg", 0.5, Everything);
        foreach (var command in commands)
            foreach (var arg in command.Arguments)
                Assert.IsFalse(arg.StartsWith('-') && arg.Contains(' '), $"{command.Name}: '{arg}' merges a flag and its value");

        CollectionAssert.AreEqual(
            new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", "-volume", "31", "/music/focus.ogg" },
            Find(commands, "ffplay").Arguments.ToArray());
    }

    [TestMethod]
    public void PreferenceOrder_ForOgg()
    {
        var names = PlayerCommands.ForMedia("/m/a.ogg", 1, Everything).Select(c => c.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "pw-play", "ffplay", "gst-play-1.0", "paplay" }, names);
    }

    [TestMethod]
    public void Mp3_SkipsSndfilePlayers()
    {
        var names = PlayerCommands.ForMedia("/m/a.MP3", 1, Everything).Select(c => c.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "ffplay", "gst-play-1.0", "mpg123" }, names);
    }

    [TestMethod]
    public void VolumeFlags_MatchEachToolsScale()
    {
        var gain = VolumeCurve.ToGain(0.5); // ~0.315
        var wav = PlayerCommands.ForMedia("/m/a.wav", 0.5, Everything);
        var mp3 = PlayerCommands.ForMedia("/m/a.mp3", 0.5, Everything);

        Assert.AreEqual(gain, double.Parse(Find(wav, "pw-play").Arguments[1], System.Globalization.CultureInfo.InvariantCulture), 1e-3);
        Assert.AreEqual(gain, double.Parse(Find(wav, "gst-play-1.0").Arguments[3], System.Globalization.CultureInfo.InvariantCulture), 1e-3);
        Assert.AreEqual(Math.Round(gain * 32768), double.Parse(Find(mp3, "mpg123").Arguments[2]));
        // pa_volume_t is cubic: (v/65536)^3 must come back to the same gain.
        Assert.AreEqual(gain, Math.Pow(double.Parse(Find(wav, "paplay").Arguments[1]) / 65536, 3), 1e-3);
    }

    [TestMethod]
    public void VolumeFormatting_IgnoresCurrentCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var arg = Find(PlayerCommands.ForMedia("/m/a.wav", 0.5, Everything), "pw-play").Arguments[1];
            Assert.IsFalse(arg.Contains(','), arg);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [TestMethod]
    public void CueWav_OrderIsPwPlayPaplayAplay()
    {
        var names = PlayerCommands.ForPcmWav("/c/x.wav", Everything).Select(c => c.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "pw-play", "paplay", "aplay" }, names);
    }

    [TestMethod]
    public void OnlyInstalledToolsAreOffered()
    {
        var onlyAplay = new ExecutableResolver(new[] { "/usr/bin" }, p => p == "/usr/bin/aplay");
        var cue = PlayerCommands.ForPcmWav("/c/x.wav", onlyAplay);
        Assert.AreEqual(1, cue.Count);
        Assert.AreEqual("/usr/bin/aplay", cue[0].Executable);
        Assert.AreEqual(0, PlayerCommands.ForMedia("/m/a.mp3", 1, onlyAplay).Count);
    }
}
