using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YAPA.Avalonia.Audio.PulseAudio;

/// <summary>
/// P/Invoke for PulseAudio's "simple" blocking API. On current Ubuntu/Fedora desktops the
/// server is PipeWire's pulse compatibility layer, which implements the same protocol.
///
/// Signatures avoid unsafe code: buffers go in as <c>ref byte</c>, which the marshaller
/// pins and passes as a pointer without copying.
/// </summary>
internal static class PulseSimpleNative
{
    public const string SimpleLibrary = "libpulse-simple.so.0";
    public const string CoreLibrary = "libpulse.so.0";

    public const int StreamPlayback = 1; // PA_STREAM_PLAYBACK

    public const int SampleFloat32Le = 5; // PA_SAMPLE_FLOAT32LE
    public const int SampleFloat32Be = 6; // PA_SAMPLE_FLOAT32BE

    /// <summary>(uint32_t)-1: "let the server choose" for any pa_buffer_attr field.</summary>
    public const uint Default = uint.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec
    {
        public int Format;     // pa_sample_format_t (C enum)
        public uint Rate;      // uint32_t
        public byte Channels;  // uint8_t
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    /// <summary>Owns a pa_simple*. pa_simple_free also drops the server connection.</summary>
    public sealed class SimpleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SimpleHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle()
        {
            pa_simple_free(handle);
            return true;
        }
    }

    [DllImport(SimpleLibrary)]
    public static extern SimpleHandle pa_simple_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int direction,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? device,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string streamName,
        ref SampleSpec spec,
        IntPtr channelMap,
        ref BufferAttr attributes,
        out int error);

    [DllImport(SimpleLibrary)]
    public static extern int pa_simple_write(SimpleHandle s, ref byte data, nuint bytes, out int error);

    [DllImport(SimpleLibrary)]
    public static extern int pa_simple_drain(SimpleHandle s, out int error);

    [DllImport(SimpleLibrary)]
    public static extern int pa_simple_flush(SimpleHandle s, out int error);

    [DllImport(SimpleLibrary)]
    public static extern ulong pa_simple_get_latency(SimpleHandle s, out int error);

    [DllImport(SimpleLibrary)]
    private static extern void pa_simple_free(IntPtr s);

    [DllImport(CoreLibrary)]
    private static extern IntPtr pa_strerror(int error);

    public static string Describe(int error)
    {
        try
        {
            return Marshal.PtrToStringUTF8(pa_strerror(error)) ?? $"error {error}";
        }
        catch
        {
            return $"error {error}";
        }
    }

    /// <summary>
    /// Checks the libraries can be loaded at all, so a system without libpulse goes straight
    /// to the process fallback instead of failing on every cue.
    /// </summary>
    public static bool TryLoad(out string reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            reason = "not Linux";
            return false;
        }
        if (!NativeLibrary.TryLoad(SimpleLibrary, out _))
        {
            reason = $"{SimpleLibrary} not found";
            return false;
        }
        if (!NativeLibrary.TryLoad(CoreLibrary, out _))
        {
            reason = $"{CoreLibrary} not found";
            return false;
        }
        reason = "";
        return true;
    }
}
