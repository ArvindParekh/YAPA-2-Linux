using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Journal;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// The user's own commits, in every repository under their home folder. The repositories
/// are found by a scan cached for half a day, plus the folders Claude Code worked in; a
/// repository whose reflog hasn't moved since the time asked about is skipped without
/// starting git.
/// </summary>
public sealed class GitHistory
{
    private static readonly TimeSpan ScanAge = TimeSpan.FromHours(12);
    private const int ScanDepth = 6;

    // Folders that hold dependencies, builds or app data, never the user's own repositories.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "node_modules", "snap", "venv", "env", "__pycache__", "target", "build", "dist", "bin", "obj", "vendor",
        "site-packages", "Library", "Applications", "go", "miniconda3", "anaconda3", "Android", "Steam",
    };

    private readonly string _home;
    private readonly string _cacheFile;
    private readonly object _gate = new();
    private string? _git;
    private string[]? _authors;

    public GitHistory(string? home = null, string? cacheFile = null)
    {
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _cacheFile = cacheFile ?? Path.Combine(AppPaths.CacheDirectory, "journal", "repositories.json");
    }

    public bool IsAvailable => Git() != null;

    /// <param name="folders">More places to look (where Claude Code ran), repositories or inside one.</param>
    public IReadOnlyList<Commit> Read(DateTime fromUtc, DateTime toUtc, IEnumerable<string> folders)
    {
        if (Git() is not { } git) return [];
        var repositories = new HashSet<string>(Repositories(), StringComparer.Ordinal);
        foreach (var folder in folders.Where(f => f.Length > 0).Distinct())
            if (TopLevel(folder) is { } top) repositories.Add(top);

        var authors = Authors(git);
        if (authors.Length == 0) return [];
        var commits = new List<Commit>();
        foreach (var repository in repositories)
        {
            if (!MovedSince(repository, fromUtc)) continue;
            var args = new List<string>
            {
                "-C", repository, "-c", "core.quotepath=off", "log", "--all", "--no-merges",
                "--since=" + fromUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                "--until=" + toUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                "--format=%H%x1f%aI%x1f%s",
            };
            args.AddRange(authors.Select(a => "--author=" + a));
            var output = Run(git, args, TimeSpan.FromSeconds(10));
            if (output == null) continue;
            var name = Path.GetFileName(repository);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\x1f');
                if (parts.Length < 3 || !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
                // The author date is when it was written; a rebase moves only the committer's.
                if (at.UtcDateTime < fromUtc || at.UtcDateTime > toUtc) continue;
                commits.Add(new Commit(name, parts[0], at.UtcDateTime, parts[2].Trim()));
            }
        }
        return commits.OrderBy(c => c.AtUtc).ToList();
    }

    private string? Git()
    {
        if (_git != null) return _git.Length > 0 ? _git : null;
        _git = ExecutableResolver.Default.Resolve("git") ?? "";
        return _git.Length > 0 ? _git : null;
    }

    /// <summary>Who "the user" is in commits: their configured email and name.</summary>
    private string[] Authors(string git)
    {
        if (_authors != null) return _authors;
        var found = new List<string>();
        foreach (var key in new[] { "user.email", "user.name" })
            if (Run(git, ["config", "--global", key], TimeSpan.FromSeconds(5))?.Trim() is { Length: > 0 } value)
                found.Add(value);
        return _authors = found.ToArray();
    }

    private static bool MovedSince(string repository, DateTime fromUtc)
    {
        var dotGit = Path.Combine(repository, ".git");
        if (!Directory.Exists(dotGit)) return true; // a worktree or a submodule: just ask git
        foreach (var marker in new[] { Path.Combine(dotGit, "logs", "HEAD"), Path.Combine(dotGit, "index"), Path.Combine(dotGit, "HEAD") })
        {
            try
            {
                if (File.Exists(marker) && File.GetLastWriteTimeUtc(marker) >= fromUtc) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    private string? TopLevel(string folder)
    {
        for (var dir = folder; !string.IsNullOrEmpty(dir) && dir.Length >= _home.Length; dir = Path.GetDirectoryName(dir))
        {
            var marker = Path.Combine(dir, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) return dir;
            if (dir == _home) break;
        }
        return null;
    }

    private IReadOnlyList<string> Repositories()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile) < ScanAge
                    && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_cacheFile)) is { } cached)
                    return cached;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }

            var found = Scan();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                File.WriteAllText(_cacheFile, JsonSerializer.Serialize(found));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return found;
        }
    }

    /// <summary>Repositories under home, without going into hidden folders or into a repository.</summary>
    private List<string> Scan()
    {
        var watch = Stopwatch.StartNew();
        var found = new List<string>();
        var level = new List<string> { _home };
        for (var depth = 0; depth <= ScanDepth && level.Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(30); depth++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                if (depth > 0 && (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))))
                {
                    found.Add(dir);
                    continue;
                }
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(child);
                        if (name.StartsWith('.') || Skipped.Contains(name)) continue;
                        if (new DirectoryInfo(child).LinkTarget != null) continue; // links lead back around
                        next.Add(child);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            level = next;
        }
        Trace.WriteLine($"[journal] found {found.Count} repositories in {watch.ElapsedMilliseconds} ms");
        return found;
    }

    private static string? Run(string executable, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["LC_ALL"] = "C.UTF-8";
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.WaitForExit();
                return null;
            }
            process.WaitForExit(); // the output is complete only after this
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
