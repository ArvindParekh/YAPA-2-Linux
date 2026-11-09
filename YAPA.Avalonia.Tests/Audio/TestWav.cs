using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YAPA.Avalonia.Tests.Audio;

/// <summary>Builds WAV files in memory, including deliberately awkward ones.</summary>
internal sealed class TestWav
{
    private readonly List<(string Id, byte[] Body, bool Pad)> _chunks = new();

    public uint? RiffSizeOverride { get; set; }

    public TestWav Chunk(string id, byte[] body, bool pad = true)
    {
        _chunks.Add((id, body, pad));
        return this;
    }

    /// <summary>A chunk whose size field says <paramref name="declaredSize"/> regardless of the body.</summary>
    public TestWav RawChunk(string id, uint declaredSize, byte[] body)
    {
        var header = new byte[8 + body.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
        BitConverter.GetBytes(declaredSize).CopyTo(header, 4);
        body.CopyTo(header, 8);
        _chunks.Add(("", header, false));
        return this;
    }

    public static byte[] Fmt(ushort tag, int channels, int rate, int bits, int? blockAlign = null)
    {
        var align = blockAlign ?? channels * ((bits + 7) / 8);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(tag);
        w.Write((ushort)channels);
        w.Write((uint)rate);
        w.Write((uint)(rate * align));
        w.Write((ushort)align);
        w.Write((ushort)bits);
        return ms.ToArray();
    }

    public static byte[] FmtExtensible(ushort subFormat, int channels, int rate, int containerBits, int validBits)
    {
        var align = channels * containerBits / 8;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0xFFFE);
        w.Write((ushort)channels);
        w.Write((uint)rate);
        w.Write((uint)(rate * align));
        w.Write((ushort)align);
        w.Write((ushort)containerBits);
        w.Write((ushort)22);            // cbSize
        w.Write((ushort)validBits);
        w.Write((uint)0x3);             // channel mask FL|FR
        w.Write(subFormat);             // GUID data1 (low half)
        w.Write((ushort)0x0000);        // GUID data1 (high half)
        w.Write((ushort)0x0000);        // data2
        w.Write((ushort)0x0010);        // data3
        w.Write(new byte[] { 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        return ms.ToArray();
    }

    public byte[] Build()
    {
        using var body = new MemoryStream();
        foreach (var (id, data, pad) in _chunks)
        {
            if (id.Length == 0)
            {
                body.Write(data);
                continue;
            }
            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write(BitConverter.GetBytes((uint)data.Length));
            body.Write(data);
            if (pad && data.Length % 2 == 1)
                body.WriteByte(0);
        }

        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        file.Write(BitConverter.GetBytes(RiffSizeOverride ?? (uint)(body.Length + 4)));
        file.Write("WAVE"u8);
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    public static byte[] Int16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(bytes, i * 2);
        return bytes;
    }

    public static byte[] Int24(params int[] samples)
    {
        var bytes = new byte[samples.Length * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[i * 3] = (byte)samples[i];
            bytes[i * 3 + 1] = (byte)(samples[i] >> 8);
            bytes[i * 3 + 2] = (byte)(samples[i] >> 16);
        }
        return bytes;
    }

    public static byte[] Int32(params int[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    public static byte[] Float32(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    public static byte[] Float64(params double[] samples)
    {
        var bytes = new byte[samples.Length * 8];
        for (var i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(bytes, i * 8);
        return bytes;
    }
}
