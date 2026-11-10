using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.PulseAudio;

namespace YAPA.Avalonia.Tests.Audio;

/// <summary>
/// Opt-in checks against the real machine. They never make a sound: the PulseAudio test
/// plays pure digital silence, and the render test only writes files.
///
///   YAPA_AUDIO_SILENT_TEST=1 dotnet test --filter TestCategory=AudioHardware
///   YAPA_AUDIO_RENDER_DIR=/some/dir dotnet test --filter TestCategory=AudioHardware
/// </summary>
[TestClass]
public class AudioHardwareTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("AudioHardware")]
    public void PulseAudio_PlaysSilenceEndToEnd()
    {
        if (Environment.GetEnvironmentVariable("YAPA_AUDIO_SILENT_TEST") != "1")
            Assert.Inconclusive("set YAPA_AUDIO_SILENT_TEST=1 to run against the real sound server");
        if (!PulseSimpleNative.TryLoad(out var reason))
            Assert.Inconclusive(reason);

        // Through the real CuePlayer first, while the sink is most likely suspended: that is what
        // the first cue of a session meets. Then warm (stream reused) and an interrupt.
        var telemetry = new BlockingCollection<CueTelemetry>();
        var cache = Path.Combine(Path.GetTempPath(), $"yapa-hw-{Guid.NewGuid():N}");
        using var player = new CuePlayer(SoundLibrary.Shared,
            (int rate, int channels, out string err) => PulseSimpleStream.TryOpen(rate, channels, out err),
            AudioFactory.InProcessBackendName,
            new ProcessCueBackend(ExecutableResolver.Default, SystemProcessLauncher.Instance, cache));
        player.CueFinished += telemetry.Add;

        var cue = new PcmBuffer(new float[48000 * 2 * 400 / 1000], 48000, 2);
        player.PlayPcm(cue, 1.0);
        var cold = Take(telemetry);
        player.PlayPcm(cue, 1.0);
        var warm = Take(telemetry);
        player.PlayPcm(new PcmBuffer(new float[48000 * 2 * 2], 48000, 2), 1.0);
        System.Threading.Thread.Sleep(150);
        var interruptAt = Stopwatch.StartNew();
        player.PlayPcm(cue, 1.0);
        var interrupted = Take(telemetry);
        var interruptLatency = interruptAt.Elapsed;
        var afterInterrupt = Take(telemetry);

        foreach (var (name, t) in new[] { ("cold", cold), ("warm", warm), ("interrupted", interrupted), ("after interrupt", afterInterrupt) })
        {
            TestContext.WriteLine($"cue {name}: backend {t.Backend}, open {Ms(t.StreamOpen)}, first write {Ms(t.FirstWrite)}, " +
                                  $"queue filled {Ms(t.QueueFilled)}, output latency {Ms(t.OutputLatency)}, total {Ms(t.Total)}, " +
                                  $"interrupted {t.Interrupted}, drained {t.Drained}");
        }
        TestContext.WriteLine($"interrupt: previous cue released {Ms(interruptLatency)} after the new Play()");
        TestContext.WriteLine($"backend: {player.BackendDescription}");
        TestContext.WriteLine($"cold cue end-to-end overhead (total - 400 ms of audio): {Ms(cold.Total - cue.Duration)}");

        // Raw stream: connect, write 300 ms of zeros, drain.
        var sw = Stopwatch.StartNew();
        using (var stream = PulseSimpleStream.TryOpen(48000, 2, out var error))
        {
            Assert.IsNotNull(stream, "pa_simple_new failed: " + error);
            var connect = sw.Elapsed;

            var silence = new float[48000 * 2 * 300 / 1000];
            sw.Restart();
            Assert.IsTrue(stream.Write(silence, out error), error);
            var write = sw.Elapsed;
            var latency = stream.Latency;
            sw.Restart();
            Assert.IsTrue(stream.Drain(), "drain failed");
            var drain = sw.Elapsed;
            Assert.IsTrue(stream.Flush(), "flush failed");

            TestContext.WriteLine($"raw stream: connect {Ms(connect)}, write 300 ms {Ms(write)}, " +
                                  $"latency after write {(latency is { } l ? Ms(l) : "unknown")}, drain {Ms(drain)}");
        }

        Assert.AreEqual("stream", cold.Backend);
        Assert.IsTrue(cold.Drained && warm.Drained && afterInterrupt.Drained);
        Assert.IsTrue(interrupted.Interrupted);
        Assert.IsNull(warm.StreamOpen, "the warm cue must reuse the open stream");
        Assert.IsTrue(warm.QueueFilled < TimeSpan.FromMilliseconds(100), $"warm start {warm.QueueFilled}");
        if (Directory.Exists(cache)) Directory.Delete(cache, true);
    }

    /// <summary>
    /// Runs every installed external player with the exact argv we build, on silent files, and
    /// checks each exits 0. This is what catches flag-syntax bugs like "-volume 50" as one token.
    /// </summary>
    [TestMethod]
    [TestCategory("AudioHardware")]
    public void ExternalPlayers_AcceptOurArguments()
    {
        if (Environment.GetEnvironmentVariable("YAPA_AUDIO_SILENT_TEST") != "1")
            Assert.Inconclusive("set YAPA_AUDIO_SILENT_TEST=1 to run the installed players on silent files");

        var dir = Path.Combine(Path.GetTempPath(), $"yapa-players-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var wav = Path.Combine(dir, "silence.wav");
            WavWriter.WritePcm16(wav, new PcmBuffer(new float[48000 * 2 / 5], 48000, 2));
            var files = new System.Collections.Generic.List<string> { wav };

            // Other formats only if ffmpeg can make them; the point is the players' flags, not ffmpeg.
            if (ExecutableResolver.Default.Resolve("ffmpeg") is { } ffmpeg)
            {
                // Vorbis explicitly: ffmpeg's default for .ogg can be FLAC-in-Ogg, which libsndfile rejects.
                foreach (var (ext, codec) in new[] { ("mp3", "libmp3lame"), ("ogg", "libvorbis"), ("flac", "flac") })
                {
                    var target = Path.Combine(dir, "silence." + ext);
                    using var p = Process.Start(new ProcessStartInfo(ffmpeg)
                    {
                        ArgumentList = { "-loglevel", "quiet", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "0.2", "-c:a", codec, target },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    })!;
                    p.WaitForExit(10_000);
                    if (File.Exists(target)) files.Add(target);
                }
            }

            // Two separate claims. Flags: every tool must accept its argv on a WAV, which all of
            // them can read, so a failure there is ours. Coverage: every file must be played by at
            // least one candidate, which is what the fallback chain needs. A tool rejecting a
            // particular file (pw-play and Ogg variants, a player with no output driver) is
            // logged but is not a failure: ProcessAudioPlayer moves on to the next candidate.
            var flagFailures = new System.Collections.Generic.List<string>();
            var unplayable = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                var anySucceeded = false;
                var commands = PlayerCommands.ForMedia(file, 0.5, ExecutableResolver.Default)
                    .Concat(PlayerCommands.ForPcmWav(file, ExecutableResolver.Default).Where(_ => file == wav));
                foreach (var command in commands)
                {
                    var info = new ProcessStartInfo(command.Executable)
                    {
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    foreach (var arg in command.Arguments) info.ArgumentList.Add(arg);

                    var sw = Stopwatch.StartNew();
                    using var process = Process.Start(info)!;
                    var stderr = process.StandardError.ReadToEndAsync();
                    var exited = process.WaitForExit(10_000);
                    if (!exited) process.Kill(entireProcessTree: true);
                    var line = $"{command.Name,-13} {Path.GetExtension(file),-5} exit {(exited ? process.ExitCode.ToString() : "timeout")} " +
                               $"in {Ms(sw.Elapsed)}  [{string.Join(' ', command.Arguments.Take(command.Arguments.Count - 1))}]";
                    TestContext.WriteLine(line);
                    var ok = exited && process.ExitCode == 0;
                    anySucceeded |= ok;
                    if (!ok)
                    {
                        TestContext.WriteLine($"    stderr: {stderr.Result.Trim()}");
                        if (file == wav) flagFailures.Add(line + " " + stderr.Result.Trim());
                    }
                }
                if (!anySucceeded) unplayable.Add(file);
            }

            Assert.AreEqual(0, flagFailures.Count, string.Join(Environment.NewLine, flagFailures));
            Assert.AreEqual(0, unplayable.Count, "no candidate could play: " + string.Join(", ", unplayable));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Real processes: a looping silent track, then Stop and Dispose must leave no child and no zombie.</summary>
    [TestMethod]
    [TestCategory("AudioHardware")]
    public void MusicPlayer_StopAndDisposeLeaveNoProcesses()
    {
        if (Environment.GetEnvironmentVariable("YAPA_AUDIO_SILENT_TEST") != "1")
            Assert.Inconclusive("set YAPA_AUDIO_SILENT_TEST=1 to run real players on silent files");

        var wav = Path.Combine(Path.GetTempPath(), $"yapa-loop-{Guid.NewGuid():N}.wav");
        WavWriter.WritePcm16(wav, new PcmBuffer(new float[48000 * 2 * 3], 48000, 2));
        try
        {
            // These tests exist to touch the real audio stack (with silent files), so they
            // opt out of the suite-wide YAPA_SILENT_AUDIO for the factory call.
            Environment.SetEnvironmentVariable("YAPA_SILENT_AUDIO", null);
            ProcessAudioPlayer player;
            try { player = (ProcessAudioPlayer)AudioFactory.CreateMusicPlayer(); }
            finally { Environment.SetEnvironmentVariable("YAPA_SILENT_AUDIO", "1"); }
            player.Play(wav, loop: true, volume: 0.5);
            Assert.IsTrue(Wait.Until(() => ChildrenOfThisProcess().Any()), "player did not start");
            TestContext.WriteLine($"playing with {player.CurrentPlayerName}; children: {string.Join(", ", ChildrenOfThisProcess())}");

            player.Stop();
            Assert.IsTrue(Wait.Until(() => !ChildrenOfThisProcess().Any(), TimeSpan.FromSeconds(3)),
                "left behind: " + string.Join(", ", ChildrenOfThisProcess()));
            Assert.IsFalse(player.IsPlaying);

            player.Play(wav, loop: true, volume: 0.5);
            Assert.IsTrue(Wait.Until(() => ChildrenOfThisProcess().Any()));
            player.Dispose();
            var remaining = ChildrenOfThisProcess().ToArray();
            TestContext.WriteLine($"after Dispose: {(remaining.Length == 0 ? "no children" : string.Join(", ", remaining))}");
            Assert.AreEqual(0, remaining.Length, "Dispose must wait until the player is gone and reaped");
        }
        finally
        {
            File.Delete(wav);
        }
    }

    /// <summary>"pid:comm:state" for each direct child, zombies included (state Z).</summary>
    private static System.Collections.Generic.IEnumerable<string> ChildrenOfThisProcess()
    {
        var self = Environment.ProcessId.ToString();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            string stat;
            try { stat = File.ReadAllText(Path.Combine(dir, "stat")); }
            catch { continue; }
            // Format: pid (comm) state ppid ...; comm may contain spaces, so split after the last ')'.
            var close = stat.LastIndexOf(')');
            if (close < 0) continue;
            var fields = stat[(close + 2)..].Split(' ');
            if (fields.Length > 1 && fields[1] == self)
                yield return $"{Path.GetFileName(dir)}:{stat[(stat.IndexOf('(') + 1)..close]}:{fields[0]}";
        }
    }

    [TestMethod]
    [TestCategory("AudioHardware")]
    public void RenderBuiltInsForReview()
    {
        var dir = Environment.GetEnvironmentVariable("YAPA_AUDIO_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
            Assert.Inconclusive("set YAPA_AUDIO_RENDER_DIR to write each built-in sound as a WAV");

        Directory.CreateDirectory(dir);
        foreach (var sound in Enum.GetValues<BuiltInSound>())
        {
            var decoded = SoundLibrary.Shared.Load(SoundRef.BuiltIn(sound), out _);
            Assert.IsNotNull(decoded, sound.ToString());
            var pcm = decoded.Pcm;
            var path = Path.Combine(dir, BuiltInSoundNames.ToKebab(sound) + ".wav");
            WavWriter.WritePcm16(path, pcm);

            var peakDb = 20 * Math.Log10(pcm.Peak());
            var rmsDb = 20 * Math.Log10(pcm.Rms());
            TestContext.WriteLine($"{BuiltInSoundNames.ToKebab(sound),-13} {pcm.Duration.TotalMilliseconds,5:0} ms  " +
                                  $"{pcm.SampleRate} Hz x{pcm.Channels}  peak {pcm.Peak():0.000} ({peakDb:0.0} dBFS)  " +
                                  $"RMS {pcm.Rms():0.0000} ({rmsDb:0.0} dBFS)  -> {path}");
        }
    }

    private static CueTelemetry Take(BlockingCollection<CueTelemetry> telemetry)
    {
        Assert.IsTrue(telemetry.TryTake(out var t, TimeSpan.FromSeconds(10)), "cue did not finish");
        return t!;
    }

    private static string Ms(TimeSpan? t) => t is { } v ? $"{v.TotalMilliseconds:0.0} ms" : "-";
}
