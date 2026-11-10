using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Audio.Library;

public enum MediaKind
{
    Radio,

    /// <summary>A file in the user's Sounds folder.</summary>
    Library,

    /// <summary>A file anywhere else, chosen with the file picker.</summary>
    File,
}

/// <param name="Reference">What gets stored in settings: "radio:fluid", or a file path.</param>
/// <param name="Location">What the player opens: a stream URL or an absolute path.</param>
public sealed record MediaItem(string Reference, string Name, MediaKind Kind, string Location, string? Description = null)
{
    public override string ToString() => Name;
}

/// <summary>
/// Everything that can play as background music or ambience without the user
/// hunting for a file:
/// <list type="bullet">
/// <item>internet radio: free, listener-supported stations meant for focus;</item>
/// <item>the Sounds folder: any audio file dropped there appears by name.</item>
/// </list>
/// Arbitrary files remain possible, as the fallback.
/// </summary>
public static class MediaLibrary
{
    private const string RadioPrefix = "radio:";

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".opus", ".ogg", ".oga", ".mp3", ".m4a", ".aac", ".flac", ".wav", ".webm", ".mka",
    };

    /// <summary>~/.local/share/YAPA2/Sounds (under YAPA_HOME when isolated).</summary>
    public static string Folder => Path.Combine(AppPaths.DataDirectory, "Sounds");

    /// <summary>
    /// SomaFM and Radio Paradise are listener-supported and explicitly free to stream from
    /// any player. Chosen for focus: mostly instrumental, no talk, no ads.
    /// </summary>
    public static IReadOnlyList<MediaItem> Radio { get; } =
    [
        Station("fluid", "Fluid", "Instrumental hip-hop and lo-fi beats · SomaFM", "https://ice1.somafm.com/fluid-128-mp3"),
        Station("groove-salad", "Groove Salad", "Chilled ambient and downtempo · SomaFM", "https://ice1.somafm.com/groovesalad-128-mp3"),
        Station("drone-zone", "Drone Zone", "Atmospheric ambient, no beats · SomaFM", "https://ice1.somafm.com/dronezone-128-mp3"),
        Station("deep-space-one", "Deep Space One", "Deep ambient electronic · SomaFM", "https://ice1.somafm.com/deepspaceone-128-mp3"),
        Station("synphaera", "Synphaera", "Modern ambient electronic · SomaFM", "https://ice1.somafm.com/synphaera-128-mp3"),
        Station("rp-mellow", "Radio Paradise Mellow", "Eclectic, mellow, human-curated", "https://stream.radioparadise.com/mellow-128"),
    ];

    private static MediaItem Station(string id, string name, string description, string url)
        => new(RadioPrefix + id, name, MediaKind.Radio, url, description);

    /// <summary>Audio files in the Sounds folder, by name. Creates the folder so "Open folder" always works.</summary>
    public static IReadOnlyList<MediaItem> LibraryFiles()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            return Directory.EnumerateFiles(Folder)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                .Select(f => new MediaItem(f, NameFor(f), MediaKind.Library, f))
                .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"[library] can't read {Folder}: {e.Message}");
            return [];
        }
    }

    /// <summary>"enrico-rain.opus" reads as "Enrico rain".</summary>
    public static string NameFor(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ').Trim();
        while (name.Contains("  ", StringComparison.Ordinal)) name = name.Replace("  ", " ");
        return name.Length == 0 ? Path.GetFileName(path) : char.ToUpper(name[0]) + name[1..];
    }

    /// <summary>
    /// Turns a stored reference into something playable. Accepts radio references and
    /// paths (absolute paths are also what older versions stored). Null when it can't play:
    /// an unknown station, or a file that has gone.
    /// </summary>
    public static MediaItem? Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        if (reference.StartsWith(RadioPrefix, StringComparison.Ordinal))
            return Radio.FirstOrDefault(r => r.Reference == reference);

        var path = reference.StartsWith("file:", StringComparison.Ordinal) ? reference["file:".Length..] : reference;
        if (!File.Exists(path)) return null;
        var inLibrary = string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Folder), StringComparison.Ordinal);
        return new MediaItem(path, NameFor(path), inLibrary ? MediaKind.Library : MediaKind.File, path);
    }
}
