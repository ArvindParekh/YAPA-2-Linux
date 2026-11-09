using System;
using System.Buffers.Binary;
using System.IO;

namespace YAPA.Avalonia.Audio;

/// <summary>
/// Writes 16-bit PCM WAV, the one format every command-line player (pw-play, paplay,
/// aplay, afplay) accepts. Used for the process fallback and for rendering previews.
/// </summary>
internal static class WavWriter
{
    private const int HeaderBytes = 44;

    public static byte[] ToPcm16(PcmBuffer pcm, double gain = 1.0)
    {
        var dataBytes = pcm.Samples.Length * 2;
        var wav = new byte[HeaderBytes + dataBytes];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(wav.Length - 8));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)pcm.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)pcm.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(pcm.SampleRate * pcm.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(pcm.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);

        var g = (float)gain;
        var data = span[HeaderBytes..];
        for (var i = 0; i < pcm.Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data[(i * 2)..], ToInt16(pcm.Samples[i] * g));
        return wav;
    }

    public static void WritePcm16(string path, PcmBuffer pcm, double gain = 1.0)
        => File.WriteAllBytes(path, ToPcm16(pcm, gain));

    // Clamp before converting: an out-of-range float cast to short wraps around, which turns
    // a slightly hot sample into a full-scale click.
    internal static short ToInt16(float v)
        => (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f);
}
