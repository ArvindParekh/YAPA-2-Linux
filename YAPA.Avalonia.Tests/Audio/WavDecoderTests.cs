using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;

namespace YAPA.Avalonia.Tests.Audio;

[TestClass]
public class WavDecoderTests
{
    private const float Tolerance = 1e-6f;

    private static PcmBuffer Decode(byte[] wav)
    {
        Assert.IsTrue(WavDecoder.TryDecode(wav, out var pcm, out var error), error);
        return pcm!;
    }

    private static void AssertSamples(PcmBuffer pcm, params float[] expected)
    {
        Assert.AreEqual(expected.Length, pcm.Samples.Length, "sample count");
        for (var i = 0; i < expected.Length; i++)
            Assert.AreEqual(expected[i], pcm.Samples[i], Tolerance, $"sample {i}");
    }

    [TestMethod]
    public void Pcm8_IsUnsignedWithMidpointSilence()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(1, 1, 8000, 8))
            .Chunk("data", new byte[] { 128, 255, 0, 192 })
            .Build();

        var pcm = Decode(wav);
        Assert.AreEqual(8000, pcm.SampleRate);
        Assert.AreEqual(1, pcm.Channels);
        AssertSamples(pcm, 0f, 127 / 128f, -1f, 0.5f);
    }

    [TestMethod]
    public void Pcm16_Stereo()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(1, 2, 44100, 16))
            .Chunk("data", TestWav.Int16(0, 16384, -32768, 32767))
            .Build();

        var pcm = Decode(wav);
        Assert.AreEqual(2, pcm.Channels);
        Assert.AreEqual(2, pcm.FrameCount);
        AssertSamples(pcm, 0f, 0.5f, -1f, 32767 / 32768f);
    }

    [TestMethod]
    public void Pcm24_SignExtendsNegativeSamples()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(1, 1, 96000, 24))
            .Chunk("data", TestWav.Int24(0x400000, -0x800000, -1))
            .Build();

        AssertSamples(Decode(wav), 0.5f, -1f, -1 / 8388608f);
    }

    [TestMethod]
    public void Pcm32()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(1, 1, 48000, 32))
            .Chunk("data", TestWav.Int32(int.MinValue, 0x40000000))
            .Build();

        AssertSamples(Decode(wav), -1f, 0.5f);
    }

    [TestMethod]
    public void Float32_KeepsOverRangeButZeroesNonFinite()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(3, 1, 48000, 32))
            .Chunk("data", TestWav.Float32(0.25f, 1.5f, float.NaN, float.PositiveInfinity))
            .Build();

        // Over-range floats are legal; they are clamped at output, not here.
        AssertSamples(Decode(wav), 0.25f, 1.5f, 0f, 0f);
    }

    [TestMethod]
    public void Float64()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(3, 2, 22050, 64))
            .Chunk("data", TestWav.Float64(-0.5, 0.125))
            .Build();

        var pcm = Decode(wav);
        Assert.AreEqual(22050, pcm.SampleRate);
        AssertSamples(pcm, -0.5f, 0.125f);
    }

    [TestMethod]
    public void Extensible_Pcm24In32BitContainer()
    {
        // 24 valid bits, MSB-aligned in a 4-byte container: decoding the container as int32 is correct.
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.FmtExtensible(1, 2, 48000, 32, 24))
            .Chunk("data", TestWav.Int32(0x40000000, -0x40000000))
            .Build();

        var pcm = Decode(wav);
        Assert.AreEqual(2, pcm.Channels);
        AssertSamples(pcm, 0.5f, -0.5f);
    }

    [TestMethod]
    public void Extensible_Float()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.FmtExtensible(3, 2, 44100, 32, 32))
            .Chunk("data", TestWav.Float32(0.1f, -0.2f))
            .Build();

        AssertSamples(Decode(wav), 0.1f, -0.2f);
    }

    [TestMethod]
    public void SixChannels_OddSampleRate()
    {
        var samples = new short[12];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(i * 1000);
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(1, 6, 11025, 16))
            .Chunk("data", TestWav.Int16(samples))
            .Build();

        var pcm = Decode(wav);
        Assert.AreEqual(6, pcm.Channels);
        Assert.AreEqual(11025, pcm.SampleRate);
        Assert.AreEqual(2, pcm.FrameCount);
        Assert.AreEqual(5000 / 32768f, pcm.Samples[5], Tolerance);
    }

    [TestMethod]
    public void SkipsOddSizedAndUnknownChunksInAnyOrder()
    {
        var wav = new TestWav()
            .Chunk("LIST", new byte[] { 1, 2, 3 })        // odd size, padded
            .Chunk("JUNK", new byte[28])
            .Chunk("fmt ", TestWav.Fmt(1, 1, 8000, 16))
            .Chunk("fact", new byte[4])
            .Chunk("id3 ", new byte[] { 9 })              // odd size, padded
            .Chunk("data", TestWav.Int16(8192))
            .Chunk("smpl", new byte[60])
            .Build();

        AssertSamples(Decode(wav), 0.25f);
    }

    [TestMethod]
    public void ToleratesMissingPadByteAfterOddChunk()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(1, 1, 8000, 16))
            .Chunk("LIST", new byte[] { 1, 2, 3 }, pad: false)
            .Chunk("data", TestWav.Int16(8192, -8192))
            .Build();

        AssertSamples(Decode(wav), 0.25f, -0.25f);
    }

    [TestMethod]
    public void ToleratesDataBeforeFmt()
    {
        var wav = new TestWav()
            .Chunk("data", TestWav.Int16(16384))
            .Chunk("fmt ", TestWav.Fmt(1, 1, 8000, 16))
            .Build();

        AssertSamples(Decode(wav), 0.5f);
    }

    [TestMethod]
    public void TruncatedDataChunk_PlaysWhatIsThere()
    {
        // Claims 1000 bytes, has 5: two whole frames and a dangling byte.
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(1, 1, 8000, 16))
            .RawChunk("data", 1000, new byte[] { 0, 0x40, 0, 0xC0, 0x11 })
            .Build();

        AssertSamples(Decode(wav), 0.5f, -0.5f);
    }

    [TestMethod]
    public void StreamingHeaderSizes_AreIgnored()
    {
        var wav = new TestWav { RiffSizeOverride = 0xFFFFFFFF }
            .Chunk("fmt ", TestWav.Fmt(1, 1, 8000, 16))
            .RawChunk("data", 0xFFFFFFFF, TestWav.Int16(16384, 16384))
            .Build();

        AssertSamples(Decode(wav), 0.5f, 0.5f);
    }

    [TestMethod]
    public void BogusBlockAlign_FallsBackToBitDepth()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(1, 2, 8000, 16, blockAlign: 0))
            .Chunk("data", TestWav.Int16(16384, -16384))
            .Build();

        AssertSamples(Decode(wav), 0.5f, -0.5f);
    }

    [DataTestMethod]
    [DataRow(new byte[0], DisplayName = "empty")]
    [DataRow(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 0, 0, 0, 0, 0, 0, 0 }, DisplayName = "ogg magic")]
    [DataRow(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'A', (byte)'V', (byte)'I', (byte)' ' }, DisplayName = "RIFF but not WAVE")]
    public void RejectsNonWav(byte[] bytes)
    {
        Assert.IsFalse(WavDecoder.TryDecode(bytes, out _, out var error));
        Assert.AreNotEqual("", error);
    }

    [TestMethod]
    public void RejectsCompressedCodecs()
    {
        var wav = new TestWav()
            .Chunk("fmt ", TestWav.Fmt(0x0011, 1, 8000, 4, blockAlign: 256)) // IMA ADPCM
            .Chunk("data", new byte[512])
            .Build();

        Assert.IsFalse(WavDecoder.TryDecode(wav, out _, out var error));
        StringAssert.Contains(error, "0x0011");
    }

    [TestMethod]
    public void RejectsMissingChunksAndAbsurdFormats()
    {
        var noData = new TestWav().Chunk("fmt ", TestWav.Fmt(1, 1, 8000, 16)).Build();
        var noFmt = new TestWav().Chunk("data", TestWav.Int16(1, 2)).Build();
        var zeroChannels = new TestWav().Chunk("fmt ", TestWav.Fmt(1, 0, 8000, 16, blockAlign: 2)).Chunk("data", TestWav.Int16(1)).Build();
        var zeroRate = new TestWav().Chunk("fmt ", TestWav.Fmt(1, 1, 0, 16)).Chunk("data", TestWav.Int16(1)).Build();
        var shortFmt = new TestWav().Chunk("fmt ", new byte[10]).Chunk("data", TestWav.Int16(1)).Build();
        var noFrames = new TestWav().Chunk("fmt ", TestWav.Fmt(1, 2, 8000, 16)).Chunk("data", new byte[3]).Build();

        foreach (var wav in new[] { noData, noFmt, zeroChannels, zeroRate, shortFmt, noFrames })
            Assert.IsFalse(WavDecoder.TryDecode(wav, out _, out _));
    }

    [TestMethod]
    public void ShippedClassicSounds_Decode()
    {
        foreach (var name in new[] { "tick.wav", "ding.wav" })
        {
            var pcm = SoundLibrary.LoadEmbeddedClassic(name);
            Assert.IsNotNull(pcm, $"{name} must be an embedded resource");
            Assert.AreEqual(44100, pcm.SampleRate);
            Assert.AreEqual(2, pcm.Channels);
            Assert.IsTrue(pcm.Duration > TimeSpan.FromSeconds(1));
        }
    }

    [TestMethod]
    public void WriterRoundTrip_ClampsInsteadOfWrapping()
    {
        var pcm = new PcmBuffer(new[] { 0f, 0.5f, -0.5f, 1.7f, -3f }, 48000, 1);
        var decoded = Decode(WavWriter.ToPcm16(pcm));

        Assert.AreEqual(48000, decoded.SampleRate);
        Assert.AreEqual(0.5f, decoded.Samples[1], 1e-4f);
        Assert.AreEqual(-0.5f, decoded.Samples[2], 1e-4f);
        Assert.AreEqual(32767 / 32768f, decoded.Samples[3], 1e-6f, "over-range must clamp to full scale, not wrap negative");
        Assert.AreEqual(-32767 / 32768f, decoded.Samples[4], 1e-6f);
    }
}
