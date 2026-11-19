using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Journal;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// Pages visited, from the history of Firefox-family (Firefox, Zen, LibreWolf, Floorp) and
/// Chromium-family (Chrome, Chromium, Brave, Edge, Vivaldi) browsers. A running browser
/// keeps its database locked and half of it in a write-ahead log, so each one is copied,
/// log included, into a private temporary folder and read there.
/// </summary>
public sealed class BrowserHistory
{
    // Chrome counts microseconds from 1601; Firefox from 1970.
    private const long ChromeEpochOffsetSeconds = 11_644_473_600;

    private readonly string _home;
    private readonly string _scratch;

    public BrowserHistory(string? home = null, string? scratch = null)
    {
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _scratch = scratch ?? Path.Combine(AppPaths.CacheDirectory, "journal", "browsers");
    }

    private sealed record Profile(string Database, bool Chromium);

    /// <summary>Every profile with a history database, whether or not it's recent.</summary>
    private IEnumerable<Profile> Profiles()
    {
        string[] firefoxRoots =
        [
            ".mozilla/firefox", ".zen", ".librewolf", ".floorp", ".waterfox", "snap/firefox/common/.mozilla/firefox",
            ".var/app/org.mozilla.firefox/.mozilla/firefox", ".var/app/app.zen_browser.zen/.zen", ".var/app/io.gitlab.librewolf-community/.librewolf",
        ];
        foreach (var root in firefoxRoots.Select(r => Path.Combine(_home, r)).Where(Directory.Exists))
            foreach (var profile in SafeDirectories(root))
                if (File.Exists(Path.Combine(profile, "places.sqlite"))) yield return new Profile(Path.Combine(profile, "places.sqlite"), false);

        string[] chromiumRoots =
        [
            ".config/google-chrome", ".config/google-chrome-beta", ".config/chromium", ".config/BraveSoftware/Brave-Browser",
            ".config/microsoft-edge", ".config/vivaldi", "snap/chromium/common/chromium",
            ".var/app/com.google.Chrome/config/google-chrome", ".var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser",
        ];
        foreach (var root in chromiumRoots.Select(r => Path.Combine(_home, r)).Where(Directory.Exists))
            foreach (var profile in SafeDirectories(root))
            {
                var name = Path.GetFileName(profile);
                if ((name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal)) && File.Exists(Path.Combine(profile, "History")))
                    yield return new Profile(Path.Combine(profile, "History"), true);
            }
    }

    public bool IsAvailable => Profiles().Any();

    public IReadOnlyList<Visit> Read(DateTime fromUtc, DateTime toUtc)
    {
        var visits = new List<Visit>();
        foreach (var profile in Profiles())
        {
            try
            {
                var latest = new[] { profile.Database, profile.Database + "-wal" }.Where(File.Exists).Max(File.GetLastWriteTimeUtc);
                if (latest < fromUtc) continue;
                visits.AddRange(ReadProfile(profile, fromUtc, toUtc));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
            {
                Trace.WriteLine($"[journal] can't read {profile.Database}: {e.Message}");
            }
        }
        return visits.OrderBy(v => v.AtUtc).ToList();
    }

    private IEnumerable<Visit> ReadProfile(Profile profile, DateTime fromUtc, DateTime toUtc)
    {
        var folder = Path.Combine(_scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var copy = Path.Combine(folder, "history.db");
        try
        {
            File.Copy(profile.Database, copy);
            if (File.Exists(profile.Database + "-wal")) File.Copy(profile.Database + "-wal", copy + "-wal");

            var from = new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds() * 1000;
            var to = new DateTimeOffset(toUtc).ToUnixTimeMilliseconds() * 1000;
            var sql = profile.Chromium
                ? "SELECT v.visit_time, u.url, u.title FROM visits v JOIN urls u ON u.id = v.url WHERE v.visit_time BETWEEN $from AND $to ORDER BY v.visit_time"
                : "SELECT v.visit_date, p.url, p.title FROM moz_historyvisits v JOIN moz_places p ON p.id = v.place_id WHERE v.visit_date BETWEEN $from AND $to ORDER BY v.visit_date";
            if (profile.Chromium)
            {
                from += ChromeEpochOffsetSeconds * 1_000_000;
                to += ChromeEpochOffsetSeconds * 1_000_000;
            }

            var result = new List<Visit>();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$from", from);
                command.Parameters.AddWithValue("$to", to);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var micros = reader.GetInt64(0) - (profile.Chromium ? ChromeEpochOffsetSeconds * 1_000_000 : 0);
                    var url = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var title = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                    var site = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
                    if (site.StartsWith("www.", StringComparison.Ordinal)) site = site[4..];
                    result.Add(new Visit(DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).UtcDateTime, site, title));
                }
            }
            return result;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
