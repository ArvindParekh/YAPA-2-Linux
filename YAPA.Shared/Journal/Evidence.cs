#nullable enable
using System;
using System.Collections.Generic;

namespace YAPA.Shared.Journal;

/// <summary>
/// A stretch of the day as the journal sees it: a pomodoro, or time at the computer off the
/// timer (a break, or the time before, between and after sessions).
/// </summary>
/// <param name="PomodoroId">The pomodoro's id; 0 for the one under way; negative off the timer (see <see cref="OffTimerId"/>).</param>
/// <param name="StartUtc">When it started, pauses included; from the event log when it knows, else end minus length.</param>
/// <param name="Cycle">Its cycle that day, when the event log recorded one.</param>
/// <param name="OffTimer">Not a pomodoro: time at the computer outside one.</param>
public sealed record WorkBlock(int PomodoroId, DateTime StartUtc, DateTime EndUtc, int Minutes, int? Cycle, bool OffTimer = false)
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// An id for a stretch off the timer, from the minute it starts: stable as long as its
    /// start is (so its write-up and any name the user gives it stay), never a pomodoro's.
    /// </summary>
    public static int OffTimerId(DateTime startUtc) => -(int)((startUtc - Epoch).Ticks / TimeSpan.TicksPerMinute) - 1;
}

/// <summary>Something typed into a Claude Code conversation.</summary>
public sealed record AgentPrompt(DateTime AtUtc, string Text);

/// <summary>
/// One Claude Code conversation, as far as it went on in the time asked about.
/// </summary>
/// <param name="Project">The folder it ran in, by name.</param>
/// <param name="Title">What Claude Code calls it (the user's own title first, then its own).</param>
/// <param name="Prompts">What the user typed, not what tools or schedules sent.</param>
/// <param name="Activity">When anything happened in it (replies, tool calls): it worked on while the user watched.</param>
/// <param name="Folder">The full path it ran in, where its commits may be.</param>
public sealed record AgentSession(string Id, string Project, string Title, IReadOnlyList<AgentPrompt> Prompts, IReadOnlyList<DateTime> Activity,
    string Folder = "");

public sealed record Commit(string Repo, string Hash, DateTime AtUtc, string Subject);

/// <param name="Site">The host without "www.".</param>
public sealed record Visit(DateTime AtUtc, string Site, string Title);

/// <summary>A stretch with one window in front, while the user was at the computer.</summary>
public sealed record WindowSpan(DateTime StartUtc, DateTime EndUtc, string App, string Title)
{
    public TimeSpan Length => EndUtc - StartUtc;
}

/// <summary>Everything found for a stretch of time, before it's matched to pomodoros.</summary>
public sealed record EvidenceSet(
    IReadOnlyList<AgentSession> Sessions,
    IReadOnlyList<Commit> Commits,
    IReadOnlyList<Visit> Visits,
    IReadOnlyList<WindowSpan> Windows)
{
    public static readonly EvidenceSet Empty = new([], [], [], []);
}

/// <summary>A Claude Code conversation within one pomodoro.</summary>
/// <param name="Minutes">Roughly how long it was in use, from minutes with activity.</param>
/// <param name="Place">Where it ran, shortened ("~/Documents/NEU/CLOUD/gate-demo"): the folders around a project say what it's part of.</param>
public sealed record SessionUse(string Project, string Title, int Minutes, IReadOnlyList<AgentPrompt> Prompts, string Place = "");

/// <summary>Time in one app within one pomodoro, and what was in its title most.</summary>
public sealed record AppUse(string App, int Minutes, IReadOnlyList<string> Titles);

/// <summary>Pages of one site within one pomodoro.</summary>
public sealed record SiteUse(string Site, int Visits, IReadOnlyList<string> Titles);

/// <summary>What one pomodoro has on record.</summary>
public sealed record BlockEvidence(
    IReadOnlyList<SessionUse> Sessions,
    IReadOnlyList<Commit> Commits,
    IReadOnlyList<SiteUse> Sites,
    IReadOnlyList<AppUse> Apps)
{
    public static readonly BlockEvidence None = new([], [], [], []);

    public bool IsEmpty => Sessions.Count == 0 && Commits.Count == 0 && Sites.Count == 0 && Apps.Count == 0;

    /// <summary>Project names, most used first: conversations' folders, then repositories.</summary>
    public IReadOnlyList<string> Projects
    {
        get
        {
            var seen = new List<string>();
            void Add(string p)
            {
                if (p.Length > 0 && !seen.Exists(s => string.Equals(s, p, StringComparison.OrdinalIgnoreCase))) seen.Add(p);
            }
            foreach (var s in Sessions) Add(s.Project);
            foreach (var c in Commits) Add(c.Repo);
            return seen;
        }
    }
}
