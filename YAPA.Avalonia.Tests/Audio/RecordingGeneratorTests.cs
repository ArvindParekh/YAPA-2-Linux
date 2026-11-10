using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Ambience;

namespace YAPA.Avalonia.Tests.Audio;

[TestClass]
public class RecordingGeneratorTests
{
    private static Stream Floats(params float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new MemoryStream(bytes);
    }

    /// <summary>Pulls <paramref name="count"/> samples, waiting for the reader thread as a real stream would.</summary>
    private static float[] Pull(RecordingGenerator generator, int count)
    {
        var output = new float[count];
        var done = 0;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (done < count && DateTime.UtcNow < deadline)
        {
            var block = new float[Math.Min(64, count - done)];
            generator.MixInto(block, 1f);
            // Silence means "not decoded yet"; the test data has no zeros, so drop it.
            var got = block.TakeWhile(x => x != 0).ToArray();
            got.CopyTo(output, done);
            done += got.Length;
            if (got.Length == 0) Thread.Sleep(1);
        }
        return output;
    }

    [TestMethod]
    public void The_recording_loops_with_no_gap_and_no_repeat_at_the_seam()
    {
        var runs = 0;
        using var generator = new RecordingGenerator(() => { runs++; return Floats(1, 2, 3, 4, 5, 6); }, "test", TimeSpan.FromMilliseconds(1));

        var output = Pull(generator, 18);

        CollectionAssert.AreEqual(new float[] { 1, 2, 3, 4, 5, 6, 1, 2, 3, 4, 5, 6, 1, 2, 3, 4, 5, 6 }, output);
        Assert.IsTrue(runs >= 3, "each pass is a fresh decode");
    }

    [TestMethod]
    public void A_float_split_across_two_reads_survives()
    {
        // A pipe can hand over any number of bytes; one read ending mid-float must not shift the stream.
        var bytes = new byte[4 * sizeof(float)];
        Buffer.BlockCopy(new float[] { 1, 2, 3, 4 }, 0, bytes, 0, bytes.Length);
        using var generator = new RecordingGenerator(() => new TrickleStream(bytes, 3), "test", TimeSpan.FromMilliseconds(1));

        CollectionAssert.AreEqual(new float[] { 1, 2, 3, 4, 1, 2, 3, 4 }, Pull(generator, 8));
    }

    [TestMethod]
    public void A_file_that_decodes_to_nothing_is_given_up_on()
    {
        var runs = 0;
        using var generator = new RecordingGenerator(() => { Interlocked.Increment(ref runs); return Floats(); }, "broken.mp3");
        Thread.Sleep(300);
        Assert.AreEqual(3, runs, "no endless respawn of a decoder that can't read the file");

        var block = new float[128];
        generator.MixInto(block, 1f);
        Assert.IsTrue(block.All(x => x == 0));
    }

    [TestMethod]
    public void Disposing_ends_the_decode()
    {
        var decode = new BlockingStream();
        var generator = new RecordingGenerator(() => decode, "test");
        Assert.IsTrue(SpinWait.SpinUntil(() => decode.Reading, 2000));

        generator.Dispose();
        Assert.IsTrue(decode.Disposed, "the decoder process is killed, not left running");
    }

    /// <summary>The real decoder on a real file. Decodes only; nothing is played.</summary>
    [TestMethod]
    public void Ffmpeg_decodes_a_file_into_stereo_48k_floats()
    {
        var ffmpeg = ExecutableResolver.Default.Resolve("ffmpeg");
        if (ffmpeg == null) { Assert.Inconclusive("ffmpeg not installed"); return; }
        var file = Path.Combine(Path.GetTempPath(), $"yapa-tone-{Guid.NewGuid():N}.ogg");
        try
        {
            // 0.25 s of a 441 Hz tone at 44.1 kHz mono: the decoder must resample and upmix.
            using (var make = Process.Start(ffmpeg, ["-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=441:duration=0.25:sample_rate=44100", file]))
                make!.WaitForExit(10_000);

            using var generator = RecordingGenerator.TryCreate(file, ExecutableResolver.Default)!;
            var output = new float[48_000 * 2 / 2]; // 0.5 s: past the end, so it has looped
            var done = 0;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (done < output.Length && DateTime.UtcNow < deadline)
            {
                // Consumed at real-time pace, as the audio thread does: 960 floats is 10 ms.
                // That pace is what lets the 2 s buffer cover the decoder's restart at the seam.
                Thread.Sleep(10);
                var block = new float[960];
                generator.MixInto(block, 1f);
                var n = Math.Min(block.Length, output.Length - done);
                if (block.Any(x => x != 0) || done > 0) { block.AsSpan(0, n).CopyTo(output.AsSpan(done)); done += n; }
            }

            Assert.AreEqual(output.Length, done, "decoded in time");
            var left = Enumerable.Range(0, output.Length / 2).Select(i => output[i * 2]).ToArray();
            var right = Enumerable.Range(0, output.Length / 2).Select(i => output[i * 2 + 1]).ToArray();
            CollectionAssert.AreEqual(left, right, "mono comes out on both channels");
            // The tone peaks at 0.088, so its rms is 0.0625 (ffmpeg decoding the file directly
            // measures 0.06248). Any gap at the loop seam would pull it down.
            var rms = Math.Sqrt(left.Average(x => x * x));
            Assert.AreEqual(0.0625, rms, 0.002, "the tone plays through the loop with no gap");
            // 441 Hz at 48 kHz: about 0.0184 cycles per sample, so ~2 zero crossings per 109 samples.
            var crossings = left.Zip(left.Skip(1)).Count(p => p.First < 0 != p.Second < 0);
            Assert.AreEqual(441 * 2 * 0.5, crossings, 30, "resampled to 48 kHz, not played at the wrong speed");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
    }

    private sealed class BlockingStream : Stream
    {
        private readonly ManualResetEventSlim _released = new();
        public volatile bool Reading;
        public volatile bool Disposed;

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reading = true;
            _released.Wait();
            throw new ObjectDisposedException(nameof(BlockingStream));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _released.Set();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingBed : IAmbienceGenerator, IDisposable
    {
        public bool Disposed;
        public void MixInto(Span<float> stereo, float gain) { }
        public void Dispose() => Disposed = true;
    }

    private static void Render(AmbienceMixer mixer, double seconds)
    {
        var block = new float[960];
        for (var t = 0.0; t < seconds; t += 0.01) mixer.Render(block, TimeSpan.Zero);
    }

    [TestMethod]
    public void The_mixer_lets_go_of_a_recording_once_it_has_faded_out()
    {
        var beds = new List<CountingBed>();
        var mixer = new AmbienceMixer(48_000, _ => { var b = new CountingBed(); beds.Add(b); return b; });
        var rain = new AmbientBed(AmbientSound.File, "/sounds/rain.opus");

        mixer.Set(true, rain, 1, 0);
        Render(mixer, 3);
        mixer.Deactivate();
        Render(mixer, 0.3);
        mixer.Set(true, rain, 1, 0);
        Render(mixer, 1);
        Assert.AreEqual(1, beds.Count, "a quick pause and resume carries on with the same decode");

        mixer.Deactivate();
        Render(mixer, AmbienceMixer.FadeOut.TotalSeconds + 0.1);
        Assert.IsTrue(beds[0].Disposed, "nothing keeps decoding while the timer is paused");
        Assert.IsFalse(mixer.WantsAudio);
    }

    [TestMethod]
    public void A_replaced_recording_is_released_after_the_crossfade()
    {
        var beds = new List<CountingBed>();
        var mixer = new AmbienceMixer(48_000, _ => { var b = new CountingBed(); beds.Add(b); return b; });

        mixer.Set(true, new AmbientBed(AmbientSound.File, "/sounds/a.opus"), 1, 0);
        Render(mixer, 3);
        mixer.Set(true, new AmbientBed(AmbientSound.File, "/sounds/b.opus"), 1, 0);
        Render(mixer, 0.5);
        Assert.IsFalse(beds[0].Disposed, "still audible underneath during the crossfade");
        Render(mixer, AmbienceMixer.Crossfade.TotalSeconds);
        Assert.IsTrue(beds[0].Disposed);
        Assert.IsFalse(beds[1].Disposed);
    }
}
