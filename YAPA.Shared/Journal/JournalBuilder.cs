#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace YAPA.Shared.Journal;

/// <summary>A run of pomodoros between long breaks: what the user calls a session.</summary>
public sealed record WorkSession(int Number, IReadOnlyList<WorkBlock> Blocks)
{
    public DateTime StartUtc => Blocks[0].StartUtc;
    public DateTime EndUtc => Blocks[^1].EndUtc;
    public int Minutes => Blocks.Sum(b => b.Minutes);
}

/// <summary>
/// Matches what was found (conversations, commits, pages, windows) to the pomodoros it
/// happened in, and groups the pomodoros into sessions. Pure: the readers live with the app.
/// </summary>
public static class JournalBuilder
{
    /// <summary>A conversation still counts as in use this long after the user last typed into it.</summary>
    public static readonly TimeSpan Engagement = TimeSpan.FromMinutes(30);

    /// <summary>Commits made just after a pomodoro (in the break) are usually its result.</summary>
    public static readonly TimeSpan CommitGrace = TimeSpan.FromMinutes(10);

    /// <summary>Without cycle numbers, a gap this long starts a new session.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(40);

    /// <summary>Off the timer, this long with nothing on record ends one stretch at the computer.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(20);

    private const int PromptsPerSession = 8;
    private const int TitlesPerSite = 4;
    private const int SitesPerBlock = 8;
    private const int TitlesPerApp = 3;
    private const int CommitsPerBlock = 15;

    // Pages that are a step on the way somewhere, never the work itself: sign-in pages.
    private static readonly string[] PassThroughHosts =
    [
        "login.microsoftonline.com", "accounts.google.com", "login.live.com", "appleid.apple.com",
        "duosecurity.com", "okta.com", "auth0.com", "workos.com", "myapps.microsoft.com",
    ];
    private static readonly string[] PassThroughPrefixes = ["login.", "signin.", "sso.", "auth.", "idp.", "cas."];

    // Interstitials and sign-in screens: on the way to the page, not the page.
    private static readonly string[] NoiseTitles =
    [
        "Just a moment", "Sign in", "Sign up", "Log in", "Login", "Redirecting", "Loading", "Two-factor authentication",
        "Verify it's you", "Attention Required",
    ];

    /// <param name="blocks">Oldest first; pomodoros and stretches off the timer are attributed separately.</param>
    public static IReadOnlyList<BlockEvidence> Attribute(IReadOnlyList<WorkBlock> blocks, EvidenceSet evidence)
    {
        var result = new List<BlockEvidence>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var nextStart = i + 1 < blocks.Count ? blocks[i + 1].StartUtc : DateTime.MaxValue;
            // A break's commits are the pomodoro's before it, so time off the timer gets no grace.
            result.Add(For(block, block.OffTimer ? block.EndUtc : nextStart, evidence));
        }
        return result;
    }

    /// <summary>The commits a pomodoro claims: made in it, or in the break just after it.</summary>
    public static IEnumerable<Commit> Claimed(IReadOnlyList<WorkBlock> pomodoros, IEnumerable<Commit> commits)
    {
        var ordered = pomodoros.OrderBy(b => b.StartUtc).ToList();
        return commits.Where(c => ordered.Select((b, i) => (b, next: i + 1 < ordered.Count ? ordered[i + 1].StartUtc : DateTime.MaxValue))
            .Any(x => c.AtUtc >= x.b.StartUtc && c.AtUtc < Min(x.b.EndUtc + CommitGrace, x.next)));
    }

    /// <summary>
    /// The time at the computer outside pomodoros, in [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>]: stretches of anything on record (windows in front, pages,
    /// prompts typed, commits not claimed by a pomodoro) split wherever nothing happened for
    /// <see cref="Quiet"/>, at pomodoros, and at <paramref name="excluded"/> (time nothing may be
    /// recorded in).
    /// </summary>
    public static IReadOnlyList<WorkBlock> OffTimer(IReadOnlyList<WorkBlock> pomodoros, EvidenceSet evidence, DateTime fromUtc, DateTime toUtc,
        IReadOnlyList<(DateTime Start, DateTime End)>? excluded = null)
    {
        var claimed = Claimed(pomodoros, evidence.Commits).Select(c => c.Hash).ToHashSet();
        var moments = new List<(DateTime Start, DateTime End)>();
        moments.AddRange(evidence.Windows.Select(w => (w.StartUtc, w.EndUtc)));
        moments.AddRange(evidence.Visits.Where(Counts).Select(v => (v.AtUtc, v.AtUtc)));
        moments.AddRange(evidence.Sessions.SelectMany(s => s.Prompts).Select(p => (p.AtUtc, p.AtUtc)));
        moments.AddRange(evidence.Commits.Where(c => !claimed.Contains(c.Hash)).Select(c => (c.AtUtc, c.AtUtc)));

        // The gaps: the range less the pomodoros and the excluded time.
        var walls = pomodoros.Select(b => (Start: b.StartUtc, End: b.EndUtc)).Concat(excluded ?? []).OrderBy(w => w.Start).ToList();
        var gaps = new List<(DateTime Start, DateTime End)>();
        var cursor = fromUtc;
        foreach (var wall in walls)
        {
            if (wall.Start > cursor) gaps.Add((cursor, Min(wall.Start, toUtc)));
            if (wall.End > cursor) cursor = wall.End;
            if (cursor >= toUtc) break;
        }
        if (cursor < toUtc) gaps.Add((cursor, toUtc));

        var result = new List<WorkBlock>();
        foreach (var (gapStart, gapEnd) in gaps.Where(g => g.End > g.Start))
        {
            var inside = moments
                .Where(m => m.Start == m.End ? m.Start >= gapStart && m.Start < gapEnd : m.Start < gapEnd && m.End > gapStart)
                .Select(m => (Start: Max(m.Start, gapStart), End: Min(m.End, gapEnd)))
                .OrderBy(m => m.Start)
                .ToList();
            DateTime? start = null, end = null;
            foreach (var m in inside)
            {
                if (start != null && m.Start - end!.Value > Quiet)
                {
                    result.Add(Stretch(start.Value, end.Value, gapEnd));
                    start = null;
                }
                if (start == null)
                {
                    start = m.Start;
                    end = m.End;
                }
                else if (m.End > end) end = m.End;
            }
            if (start != null) result.Add(Stretch(start.Value, end!.Value, gapEnd));
        }
        return result;
    }

    private static WorkBlock Stretch(DateTime start, DateTime end, DateTime limit)
    {
        // A single moment (one page, one prompt) still took a minute.
        end = Min(Max(end, start + TimeSpan.FromMinutes(1)), limit);
        return new WorkBlock(WorkBlock.OffTimerId(start), start, end, Math.Max(1, (int)Math.Round((end - start).TotalMinutes)), null, OffTimer: true);
    }

    // A visit that's the work, not a step on the way to it.
    private static bool Counts(Visit v) => !IsPassThrough(v.Site) && CleanTitle(v.Title) is { Length: > 0 } t && !IsNoise(t);

    private static BlockEvidence For(WorkBlock block, DateTime nextStart, EvidenceSet evidence)
    {
        var from = block.StartUtc;
        var to = block.EndUtc;
        bool Inside(DateTime t) => t >= from && t <= to;

        var sessions = Merge(evidence.Sessions
                .Select(s => Use(s, from, to))
                .Where(u => u != null)
                .Select(u => u!))
            .OrderByDescending(u => u.Minutes)
            .ThenByDescending(u => u.Prompts.Count)
            .ToList();

        var commitsUntil = Min(to + CommitGrace, nextStart);
        var commits = evidence.Commits
            .Where(c => c.AtUtc >= from && c.AtUtc < commitsUntil)
            .GroupBy(c => c.Hash).Select(g => g.First()) // the same commit in a clone or a fork
            .OrderBy(c => c.AtUtc)
            .Take(CommitsPerBlock)
            .ToList();

        var sites = evidence.Visits
            .Where(v => Inside(v.AtUtc) && !IsPassThrough(v.Site))
            .GroupBy(v => v.Site, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SiteUse(g.Key, g.Count(),
                g.Select(v => CleanTitle(v.Title)).Where(t => t.Length > 0 && !IsNoise(t))
                 .GroupBy(t => t).OrderByDescending(t => t.Count()).Select(t => t.Key).Take(TitlesPerSite).ToList()))
            // A site with nothing but untitled or sign-in pages was a redirect or a callback.
            .Where(s => s.Titles.Count > 0)
            .OrderByDescending(s => s.Visits)
            .Take(SitesPerBlock)
            .ToList();

        var apps = evidence.Windows
            .Select(w => Clip(w, from, to))
            .Where(w => w != null && w.Length > TimeSpan.Zero)
            .GroupBy(w => w!.App)
            .Select(g => new AppUse(g.Key, (int)Math.Round(g.Sum(w => w!.Length.TotalMinutes)),
                g.GroupBy(w => CleanTitle(w!.Title)).Where(t => t.Key.Length > 0)
                 .OrderByDescending(t => t.Sum(w => w!.Length.Ticks)).Select(t => t.Key).Take(TitlesPerApp).ToList()))
            .Where(a => a.Minutes > 0)
            .OrderByDescending(a => a.Minutes)
            .ToList();

        return new BlockEvidence(sessions, commits, sites, apps);
    }

    /// <summary>
    /// How a conversation was used in [from, to]: the prompts typed then, and the minutes in
    /// which it was busy while the user had typed into it recently. Activity long after the
    /// last prompt (a scheduled run, a session left going) isn't the user's work.
    /// </summary>
    private static SessionUse? Use(AgentSession session, DateTime from, DateTime to)
    {
        var prompts = session.Prompts.Where(p => p.AtUtc >= from && p.AtUtc <= to).OrderBy(p => p.AtUtc).ToList();
        var promptTimes = session.Prompts.Select(p => p.AtUtc).OrderBy(t => t).ToArray();

        bool Engaged(DateTime t)
        {
            var i = Array.BinarySearch(promptTimes, t);
            if (i < 0) i = ~i - 1; // the last prompt at or before t
            return i >= 0 && t - promptTimes[i] <= Engagement;
        }

        var minutes = session.Activity
            .Where(t => t >= from && t <= to && Engaged(t))
            .Select(t => t.Ticks / TimeSpan.TicksPerMinute)
            .Concat(prompts.Select(p => p.AtUtc.Ticks / TimeSpan.TicksPerMinute))
            .Distinct()
            .Count();
        if (minutes == 0) return null;

        // Several prompts a minute apart say less than a few spread out; keep a spread.
        var kept = prompts.Count <= PromptsPerSession
            ? prompts
            : Enumerable.Range(0, PromptsPerSession).Select(k => prompts[k * (prompts.Count - 1) / (PromptsPerSession - 1)]).Distinct().ToList();
        return new SessionUse(session.Project, session.Title, minutes, kept, Shorten(session.Folder));
    }

    /// <summary>
    /// One conversation can sit in more than one transcript (the desktop app forks and moves
    /// them, keeping the history), so the same prompts show up twice. Uses that share a prompt
    /// are one conversation: the busier one's name, all of their prompts.
    /// </summary>
    private static IEnumerable<SessionUse> Merge(IEnumerable<SessionUse> uses)
    {
        var groups = new List<List<SessionUse>>();
        foreach (var use in uses)
        {
            var keys = use.Prompts.Select(p => (p.AtUtc, p.Text)).ToHashSet();
            var joined = groups.Where(g => g.Any(u => u.Prompts.Any(p => keys.Contains((p.AtUtc, p.Text))))).ToList();
            var group = new List<SessionUse> { use };
            foreach (var g in joined)
            {
                group.AddRange(g);
                groups.Remove(g);
            }
            groups.Add(group);
        }
        foreach (var group in groups)
        {
            var main = group.OrderByDescending(u => u.Minutes).ThenByDescending(u => u.Project.Length > 0).First();
            var prompts = group.SelectMany(u => u.Prompts).DistinctBy(p => (p.AtUtc, p.Text)).OrderBy(p => p.AtUtc).ToList();
            yield return main with { Minutes = group.Max(u => u.Minutes), Prompts = prompts };
        }
    }

    private static string Shorten(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/');
        return home.Length > 0 && folder.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + folder[home.Length..] : folder;
    }

    public static bool IsNoise(string title) => NoiseTitles.Any(n => title.StartsWith(n, StringComparison.OrdinalIgnoreCase));

    private static WindowSpan? Clip(WindowSpan w, DateTime from, DateTime to)
    {
        var start = w.StartUtc > from ? w.StartUtc : from;
        var end = w.EndUtc < to ? w.EndUtc : to;
        return end > start ? w with { StartUtc = start, EndUtc = end } : null;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    public static bool IsPassThrough(string site)
    {
        var s = site.ToLowerInvariant();
        return PassThroughHosts.Any(h => s == h || s.EndsWith("." + h, StringComparison.Ordinal))
               || PassThroughPrefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal));
    }

    // Browser and editor names at the end of titles say nothing the app doesn't.
    private static readonly string[] TitleSuffixes =
    [
        " — Zen Browser", " - Zen Browser", " — Mozilla Firefox", " - Mozilla Firefox", " - Google Chrome", " - Chromium",
        " - Brave", " - Microsoft Edge", " - Visual Studio Code", " - Cursor", " — Mozilla Thunderbird",
    ];

    public static string CleanTitle(string title)
    {
        var t = title.Trim();
        foreach (var suffix in TitleSuffixes)
            if (t.EndsWith(suffix, StringComparison.Ordinal)) t = t[..^suffix.Length].TrimEnd();
        return t is "New Tab" or "New tab" or "Untitled" ? "" : t;
    }

    /// <summary>
    /// Sessions: pomodoros of the same cycle, as the event log numbered them; without cycle
    /// numbers (pomodoros logged by hand, days before the log), a long gap starts a new one.
    /// </summary>
    public static IReadOnlyList<WorkSession> Sessions(IReadOnlyList<WorkBlock> blocks)
    {
        var sessions = new List<WorkSession>();
        var current = new List<WorkBlock>();
        foreach (var block in blocks)
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                var split = last.Cycle is { } a && block.Cycle is { } b
                    ? a != b
                    : block.StartUtc - last.EndUtc > SessionGap;
                if (split)
                {
                    sessions.Add(new WorkSession(sessions.Count + 1, current));
                    current = [];
                }
            }
            current.Add(block);
        }
        if (current.Count > 0) sessions.Add(new WorkSession(sessions.Count + 1, current));
        return sessions;
    }

    /// <summary>
    /// A title from the evidence alone, for when Claude isn't asked (or hasn't answered yet):
    /// the conversation it was mostly in, else the commits, else the pages, else the app.
    /// </summary>
    public static string FallbackTitle(BlockEvidence e)
    {
        if (e.Sessions.Count > 0 && e.Sessions[0].Title.Length > 0) return e.Sessions[0].Title;
        if (e.Commits.Count == 1) return e.Commits[0].Subject;
        if (e.Commits.Count > 1) return $"{e.Commits.Count} commits in {e.Commits.GroupBy(c => c.Repo).OrderByDescending(g => g.Count()).First().Key}";
        if (e.Sites.Count > 0) return e.Sites[0].Titles.FirstOrDefault() ?? e.Sites[0].Site;
        if (e.Apps.Count > 0) return e.Apps[0].Titles.FirstOrDefault() is { } t ? $"{e.Apps[0].App}: {t}" : e.Apps[0].App;
        return "";
    }
}
