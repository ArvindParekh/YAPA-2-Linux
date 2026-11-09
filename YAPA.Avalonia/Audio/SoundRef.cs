using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace YAPA.Avalonia.Audio;

public enum SoundRefKind
{
    None,
    BuiltIn,
    File,
}

/// <summary>
/// A user's choice of sound, as stored in settings.
///
/// Serialized forms: <c>""</c> (no sound), <c>"builtin:&lt;kebab-name&gt;"</c> and
/// <c>"file:&lt;path&gt;"</c>. Anything unrecognised parses to <see cref="None"/> so a
/// corrupted or hand-edited settings file degrades to silence instead of an exception.
/// </summary>
public readonly record struct SoundRef
{
    private const string BuiltInPrefix = "builtin:";
    private const string FilePrefix = "file:";

    // Windows paths are recognised on every OS because settings written by the WPF app
    // on Windows can end up being read here (synced config folders, copied profiles).
    private static readonly Regex WindowsAbsolutePath = new(@"^([A-Za-z]:[\\/]|\\\\)", RegexOptions.CultureInvariant);

    private readonly BuiltInSound _builtIn;
    private readonly string? _path;

    private SoundRef(SoundRefKind kind, BuiltInSound builtIn, string? path)
    {
        Kind = kind;
        _builtIn = builtIn;
        _path = path;
    }

    /// <summary>The default value is <see cref="None"/>, so an uninitialised field means "no sound".</summary>
    public static SoundRef None => default;

    public static IReadOnlyList<SoundRef> BuiltIns { get; } =
        Enum.GetValues<BuiltInSound>().Select(BuiltIn).ToArray();

    public SoundRefKind Kind { get; }

    public bool IsNone => Kind == SoundRefKind.None;

    public BuiltInSound? AsBuiltIn => Kind == SoundRefKind.BuiltIn ? _builtIn : null;

    public string? FilePath => Kind == SoundRefKind.File ? _path : null;

    public static SoundRef BuiltIn(BuiltInSound sound)
        => Enum.IsDefined(sound) ? new SoundRef(SoundRefKind.BuiltIn, sound, null) : None;

    /// <summary>
    /// The path is stored verbatim (not normalised against the current directory) so that
    /// <see cref="Serialize"/> and <see cref="Parse"/> round-trip exactly.
    /// </summary>
    public static SoundRef File(string path)
        => string.IsNullOrWhiteSpace(path) ? None : new SoundRef(SoundRefKind.File, default, path);

    public string Serialize() => Kind switch
    {
        SoundRefKind.BuiltIn => BuiltInPrefix + BuiltInSoundNames.ToKebab(_builtIn),
        SoundRefKind.File => FilePrefix + _path,
        _ => "",
    };

    public string DisplayName => Kind switch
    {
        SoundRefKind.BuiltIn => BuiltInSoundNames.DisplayName(_builtIn),
        SoundRefKind.File => FileNameOf(_path!),
        _ => "None",
    };

    public static SoundRef Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return None;

        var s = text.Trim();

        if (s.StartsWith(BuiltInPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return BuiltInSoundNames.TryParseKebab(s[BuiltInPrefix.Length..].Trim(), out var sound)
                ? BuiltIn(sound)
                : None;
        }

        if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.IsFile
                ? FromPath(uri.LocalPath)
                : None;
        }

        if (s.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
            return FromPath(s[FilePrefix.Length..]);

        // Legacy settings stored a raw path. Only accept absolute ones: a relative string is
        // far more likely to be garbage than a sound the user picked.
        return IsAbsolutePath(s) ? FromPath(s) : None;
    }

    public override string ToString() => Serialize();

    private static SoundRef FromPath(string path)
    {
        // Old builds stored the absolute path of the shipped tick/ding files. Those paths
        // break whenever the install location changes (a previous build lost the files
        // entirely), so map them back to the built-ins they always meant.
        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith("/Assets/tick.wav", StringComparison.OrdinalIgnoreCase))
            return BuiltIn(BuiltInSound.ClassicTick);
        if (normalized.EndsWith("/Assets/ding.wav", StringComparison.OrdinalIgnoreCase))
            return BuiltIn(BuiltInSound.ClassicDing);

        return File(path);
    }

    private static bool IsAbsolutePath(string s)
        => System.IO.Path.IsPathFullyQualified(s) || WindowsAbsolutePath.IsMatch(s);

    private static string FileNameOf(string path)
    {
        var separator = path.LastIndexOfAny(new[] { '/', '\\' });
        return separator >= 0 && separator < path.Length - 1 ? path[(separator + 1)..] : path;
    }
}
