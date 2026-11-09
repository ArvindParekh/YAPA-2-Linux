using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YAPA.Avalonia.Audio;

/// <param name="CacheKey">Stable, filename-safe identity of the audio content; the process
/// fallback uses it to name the WAV it writes.</param>
internal sealed record DecodedSound(PcmBuffer Pcm, string CacheKey);

/// <summary>
/// Resolves a <see cref="SoundRef"/> to PCM and caches the result, so a cue costs one
/// decode (or one synthesis) per process rather than one per play.
/// </summary>
internal sealed class SoundLibrary
{
    /// <summary>Bigger "cues" are handed to an external player instead of being decoded into memory.</summary>
    public const long MaxDecodableFileBytes = 64L * 1024 * 1024;

    private const int MaxCachedFiles = 16;

    private readonly ConcurrentDictionary<BuiltInSound, Lazy<PcmBuffer?>> _builtIns = new();
    private readonly ConcurrentDictionary<string, CachedFile> _files = new(StringComparer.Ordinal);
    private readonly Func<string, PcmBuffer?> _loadClassic;

    private sealed record CachedFile(long Length, DateTime LastWriteUtc, DecodedSound? Sound);

    public SoundLibrary() : this(LoadClassic) { }

    internal SoundLibrary(Func<string, PcmBuffer?> loadClassic) => _loadClassic = loadClassic;

    public static SoundLibrary Shared { get; } = new();

    /// <summary>
    /// Returns decoded PCM, or null. When the sound is a file this process can't decode
    /// (MP3, OGG, ADPCM-in-WAV...), <paramref name="passthroughPath"/> is set so the caller
    /// can give it to an external player instead.
    /// </summary>
    public DecodedSound? Load(SoundRef sound, out string? passthroughPath)
    {
        passthroughPath = null;
        try
        {
            switch (sound.Kind)
            {
                case SoundRefKind.BuiltIn:
                    var builtIn = sound.AsBuiltIn!.Value;
                    var pcm = _builtIns.GetOrAdd(builtIn, b => new Lazy<PcmBuffer?>(() => RenderBuiltIn(b))).Value;
                    return pcm is null ? null : new DecodedSound(pcm, "builtin-" + BuiltInSoundNames.ToKebab(builtIn));

                case SoundRefKind.File:
                    return LoadFile(sound.FilePath!, out passthroughPath);

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            AudioLog.Info($"could not load {sound}: {ex.Message}");
            return null;
        }
    }

    private PcmBuffer? RenderBuiltIn(BuiltInSound sound) => sound switch
    {
        BuiltInSound.ClassicTick => _loadClassic("tick.wav"),
        BuiltInSound.ClassicDing => _loadClassic("ding.wav"),
        _ => SoundSynthesizer.Render(sound),
    };

    private DecodedSound? LoadFile(string path, out string? passthroughPath)
    {
        passthroughPath = null;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            AudioLog.Once("missing:" + path, $"sound file not found: {path}");
            return null;
        }

        if (_files.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
        {
            if (cached.Sound is null) passthroughPath = path;
            return cached.Sound;
        }

        var decoded = TryDecodeFile(info);
        if (decoded is null) passthroughPath = path;

        // A user browsing through files in the settings page shouldn't grow this without bound.
        if (_files.Count >= MaxCachedFiles) _files.Clear();
        _files[path] = new CachedFile(info.Length, info.LastWriteTimeUtc, decoded);
        return decoded;
    }

    private static DecodedSound? TryDecodeFile(FileInfo info)
    {
        if (info.Length > MaxDecodableFileBytes)
            return null;

        var bytes = File.ReadAllBytes(info.FullName);
        if (!WavDecoder.LooksLikeWav(bytes))
            return null;
        if (!WavDecoder.TryDecode(bytes, out var pcm, out var error))
        {
            AudioLog.Once("decode:" + info.FullName, $"{info.FullName}: {error}; trying an external player");
            return null;
        }

        // The key changes when the file does, so a stale fallback WAV is never reused.
        var identity = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        return new DecodedSound(pcm, "file-" + hash);
    }

    /// <summary>
    /// The classic sounds are looked up next to the executable first (so a packager can
    /// swap them), then in the assembly. The embedded copy exists because a previous
    /// build silently lost the loose files, and a single-file publish has no loose files.
    /// </summary>
    internal static PcmBuffer? LoadClassic(string fileName)
    {
        var onDisk = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        try
        {
            if (File.Exists(onDisk) && WavDecoder.TryDecode(File.ReadAllBytes(onDisk), out var pcm, out var error))
                return pcm;
        }
        catch (Exception ex)
        {
            AudioLog.Info($"could not read {onDisk}: {ex.Message}");
        }

        var embedded = LoadEmbeddedClassic(fileName);
        if (embedded is null)
            AudioLog.Once("classic:" + fileName, $"built-in sound {fileName} is missing from both {onDisk} and the assembly");
        return embedded;
    }

    internal static PcmBuffer? LoadEmbeddedClassic(string fileName)
    {
        using var stream = typeof(SoundLibrary).Assembly.GetManifestResourceStream(EmbeddedResourceName(fileName));
        if (stream is null)
            return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return WavDecoder.TryDecode(memory.GetBuffer().AsSpan(0, (int)memory.Length), out var pcm, out _) ? pcm : null;
    }

    // Must match the LogicalName in YAPA.Avalonia.csproj.
    internal static string EmbeddedResourceName(string fileName) => "YAPA.Avalonia.Assets." + fileName;
}
