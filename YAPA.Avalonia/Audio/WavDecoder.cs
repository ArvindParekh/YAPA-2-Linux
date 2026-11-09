using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Decodes RIFF/WAVE files: integer PCM (8/16/24/32-bit, any container width), IEEE float
/// (32/64-bit), WAVE_FORMAT_EXTENSIBLE, any channel count up to <see cref="MaxChannels"/>
/// and any sample rate.
///
/// It is written to accept the files real tools produce rather than only the spec: wrong
/// RIFF sizes, data chunks that claim more bytes than the file holds, missing pad bytes,
/// metadata chunks in any order. Compressed codecs (ADPCM, A-law, MP3-in-WAV) are reported
/// as unsupported, and the caller hands those files to an external player instead.
/// </summary>
internal static class WavDecoder
{
    // PulseAudio's PA_CHANNELS_MAX. Anything wider can't be played in-process anyway.
    public const int MaxChannels = 32;
    private const int MaxSampleRate = 1_536_000;

    private const ushort FormatPcm = 0x0001;
    private const ushort FormatIeeeFloat = 0x0003;
    private const ushort FormatExtensible = 0xFFFE;

    private enum SampleEncoding { UInt8, Int16, Int24, Int32, Float32, Float64 }

    private readonly record struct WavFormat(int Channels, int SampleRate, int BlockAlign, int ContainerBytes, SampleEncoding Encoding);

    public static bool LooksLikeWav(ReadOnlySpan<byte> header)
        => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WAVE"u8);

    public static bool TryDecode(ReadOnlySpan<byte> wav, [NotNullWhen(true)] out PcmBuffer? pcm, out string error)
    {
        pcm = null;
        if (!LooksLikeWav(wav))
        {
            error = "not a RIFF/WAVE file";
            return false;
        }

        // The RIFF size field is ignored: streaming writers leave it 0 or 0xFFFFFFFF, and the
        // chunk walk below is bounded by the real buffer length anyway.
        WavFormat? format = null;
        var dataOffset = -1;
        var dataLength = 0;
        var pos = 12;

        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4));
            var bodyStart = pos + 8;
            var bodyLength = (int)Math.Min(declared, (uint)(wav.Length - bodyStart));

            if (id.SequenceEqual("fmt "u8))
            {
                if (!TryParseFormat(wav.Slice(bodyStart, bodyLength), out var parsed, out error))
                    return false;
                format = parsed;
            }
            else if (id.SequenceEqual("data"u8) && dataOffset < 0)
            {
                // A truncated download or a crashed recorder leaves a data chunk claiming more
                // than is there; play what exists instead of rejecting the file.
                dataOffset = bodyStart;
                dataLength = bodyLength;
            }

            // Chunks are word-aligned: an odd-sized chunk is followed by an uncounted pad byte.
            var next = (long)bodyStart + declared + (declared & 1);
            if ((declared & 1) == 1 && !LooksLikeChunkId(wav, next) && LooksLikeChunkId(wav, next - 1))
                next--; // Some writers omit the pad byte; trust the bytes over the spec.
            if (next > wav.Length - 8)
                break;
            pos = (int)next;
        }

        if (format is not { } fmt)
        {
            error = "no fmt chunk";
            return false;
        }
        if (dataOffset < 0)
        {
            error = "no data chunk";
            return false;
        }

        var frames = dataLength / fmt.BlockAlign;
        if (frames == 0)
        {
            error = "no audio frames";
            return false;
        }

        pcm = new PcmBuffer(DecodeSamples(wav.Slice(dataOffset, frames * fmt.BlockAlign), fmt, frames), fmt.SampleRate, fmt.Channels);
        error = "";
        return true;
    }

    private static bool TryParseFormat(ReadOnlySpan<byte> body, out WavFormat format, out string error)
    {
        format = default;
        if (body.Length < 16)
        {
            error = "fmt chunk too short";
            return false;
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var channels = (int)BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        var blockAlign = (int)BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
        var bitsPerSample = (int)BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);

        if (tag == FormatExtensible)
        {
            // cbSize(2) validBits(2) channelMask(4) subFormat GUID(16). The first two bytes of
            // the GUID are the classic format tag; the rest is the fixed KSDATAFORMAT suffix.
            if (body.Length < 40)
            {
                error = "WAVE_FORMAT_EXTENSIBLE fmt chunk too short";
                return false;
            }
            tag = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
        }

        if (channels is < 1 or > MaxChannels)
        {
            error = $"unsupported channel count {channels}";
            return false;
        }
        if (sampleRate is 0 or > MaxSampleRate)
        {
            error = $"unsupported sample rate {sampleRate}";
            return false;
        }

        // The container width comes from blockAlign, not bitsPerSample: 20- or 24-bit samples
        // often sit in 3- or 4-byte containers, MSB-aligned, so decoding the whole container
        // gives the right value. Fall back to the bit depth when blockAlign is nonsense.
        var containerBytes = blockAlign > 0 && blockAlign % channels == 0
            ? blockAlign / channels
            : (bitsPerSample + 7) / 8;
        if (containerBytes == 0)
        {
            error = "zero-width samples";
            return false;
        }
        if (blockAlign < containerBytes * channels)
            blockAlign = containerBytes * channels;

        SampleEncoding encoding;
        switch (tag, containerBytes)
        {
            case (FormatPcm, 1): encoding = SampleEncoding.UInt8; break;
            case (FormatPcm, 2): encoding = SampleEncoding.Int16; break;
            case (FormatPcm, 3): encoding = SampleEncoding.Int24; break;
            case (FormatPcm, 4): encoding = SampleEncoding.Int32; break;
            case (FormatIeeeFloat, 4): encoding = SampleEncoding.Float32; break;
            case (FormatIeeeFloat, 8): encoding = SampleEncoding.Float64; break;
            default:
                error = $"unsupported encoding (format tag 0x{tag:X4}, {containerBytes * 8}-bit container)";
                return false;
        }

        format = new WavFormat(channels, (int)sampleRate, blockAlign, containerBytes, encoding);
        error = "";
        return true;
    }

    private static float[] DecodeSamples(ReadOnlySpan<byte> data, WavFormat fmt, int frames)
    {
        var samples = new float[frames * fmt.Channels];
        var i = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var frameBytes = data.Slice(frame * fmt.BlockAlign, fmt.BlockAlign);
            for (var ch = 0; ch < fmt.Channels; ch++)
                samples[i++] = DecodeSample(frameBytes.Slice(ch * fmt.ContainerBytes, fmt.ContainerBytes), fmt.Encoding);
        }
        return samples;
    }

    private static float DecodeSample(ReadOnlySpan<byte> b, SampleEncoding encoding)
    {
        switch (encoding)
        {
            case SampleEncoding.UInt8:
                return (b[0] - 128) / 128f;
            case SampleEncoding.Int16:
                return BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f;
            case SampleEncoding.Int24:
                return (b[0] | (b[1] << 8) | ((sbyte)b[2] << 16)) / 8388608f;
            case SampleEncoding.Int32:
                return (float)(BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648.0);
            case SampleEncoding.Float32:
                return Sanitize(BinaryPrimitives.ReadSingleLittleEndian(b));
            case SampleEncoding.Float64:
                return Sanitize((float)BinaryPrimitives.ReadDoubleLittleEndian(b));
            default:
                return 0;
        }
    }

    // Float files can legally hold values beyond ±1 (they are clamped at output), but NaN and
    // infinity would poison the whole mix, so they become silence here.
    private static float Sanitize(float v) => float.IsFinite(v) ? v : 0f;

    private static bool LooksLikeChunkId(ReadOnlySpan<byte> wav, long offset)
    {
        if (offset < 0 || offset + 4 > wav.Length)
            return false;
        foreach (var c in wav.Slice((int)offset, 4))
        {
            if (c is < 0x20 or > 0x7E)
                return false;
        }
        return true;
    }
}
