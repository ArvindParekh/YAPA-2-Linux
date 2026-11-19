using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// Whether a site or a title is adult content, for "Leave out adult sites": a bundled list of
/// about 8,000 adult sites (AdultSites.txt says where it comes from), the names that give a
/// site away (anything with "porn" or "xnxx" in it, which reaches the long tail the list
/// can't), and in titles, the words and site names that do. No list is complete; Forget is
/// for what this misses.
/// </summary>
public static class AdultSites
{
    private static readonly Lazy<HashSet<string>> Hosts = new(Load);

    // In a host name. "xxx" but not XXXLutz, the furniture shop.
    private static readonly Regex Named = new(
        @"porn|xxx(?!lutz)|hentai|xvideo|xnxx|xhamster|onlyfans|nsfw|rule34|chaturbate|stripchat|brazzers|spankbang|youjizz|redtube|youporn",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Whole words in a title: what says it outright, and the sites that put their name there.
    private static readonly Regex Words = new(
        @"\b(?:porn\w*|xxx|nsfw|hentai|nhentai|onlyfans|fansly|pornhub|xvideos|xnxx|xhamster|chaturbate|stripchat|spankbang|eporner|youporn|redtube|" +
        @"youjizz|rule\s?34|e621|redgifs|erome|motherless|brazzers|bangbros|literotica|fetlife|missav|f95zone|camsoda|bongacams|livejasmin|myfreecams)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A site's address inside a title ("pornpics.com - …").
    private static readonly Regex Address = new(@"\b[a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A host on the list (or under one of its domains), or one whose name gives it away.</summary>
    public static bool IsAdultHost(string host)
    {
        if (host.Length == 0) return false;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        var colon = h.IndexOf(':');
        if (colon >= 0) h = h[..colon];
        if (Named.IsMatch(h)) return true;
        var hosts = Hosts.Value;
        // The host and each domain above it, down to (not including) the top-level one.
        for (var part = h; part.Contains('.'); part = part[(part.IndexOf('.') + 1)..])
            if (hosts.Contains(part)) return true;
        return false;
    }

    /// <summary>A window or page title that names adult content or an adult site.</summary>
    public static bool IsAdultText(string text)
    {
        if (text.Length == 0) return false;
        if (Words.IsMatch(text)) return true;
        foreach (Match m in Address.Matches(text))
            if (IsAdultHost(m.Value)) return true;
        return false;
    }

    public static int Count => Hosts.Value.Count;

    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var stream = typeof(AdultSites).Assembly.GetManifestResourceStream("YAPA.Avalonia.Journal.AdultSites.txt");
        if (stream == null) return set;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var host = line.Trim();
            if (host.Length > 0 && host[0] != '#') set.Add(host);
        }
        return set;
    }

    /// <summary>For tests: every listed host is a well-formed lowercase name.</summary>
    internal static IEnumerable<string> Listed => Hosts.Value.OrderBy(h => h, StringComparer.Ordinal);
}
