using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YAPA.Avalonia.Assistant;
using YAPA.Avalonia.Persistence;
using YAPA.Shared.Contracts;
using YAPA.Shared.Journal;

namespace YAPA.Avalonia.Journal;

/// <summary>One pomodoro in the journal, or one stretch at the computer off the timer.</summary>
/// <param name="Session">The session it's in; for time off the timer, the session it's a break in, else 0.</param>
/// <param name="Summary">What Claude wrote, when it's been written for this evidence.</param>
/// <param name="UserTitle">What the user called it; it wins.</param>
/// <param name="InProgress">The pomodoro running now, or the stretch off the timer going on now: shown, not yet written up.</param>
/// <param name="OffRecord">A pomodoro (nearly) all of whose time is off the record: it counts, but nothing about it is kept or sent.</param>
/// <param name="Paused">The pomodoro under way, sitting paused.</param>
public sealed record JournalEntry(WorkBlock Block, int Session, BlockEvidence Evidence, string Fingerprint, PomodoroSummary? Summary,
    string? UserTitle, bool InProgress = false, bool OffRecord = false, bool Paused = false)
{
    public bool OffTimer => Block.OffTimer;

    public string Title => OffRecord ? "" : UserTitle ?? (Summary is { Title.Length: > 0 } s ? s.Title : JournalBuilder.FallbackTitle(Evidence));

    public string Detail => Summary?.Detail ?? "";

    public IReadOnlyList<string> Projects => Summary is { Projects.Count: > 0 } s ? s.Projects : Evidence.Projects.Take(2).ToList();
}

/// <param name="Summary">Claude's write-up of the day; null until written (or without summaries).</param>
/// <param name="Entries">Pomodoros and stretches off the timer, in order.</param>
/// <param name="Stale">Some entry isn't written up for its current evidence.</param>
/// <param name="Sources">What had anything to say, for the footer ("From Claude Code, git and Zen").</param>
public sealed record JournalDay(DateOnly Date, IReadOnlyList<JournalEntry> Entries, IReadOnlyList<WorkSession> Sessions, DaySummary? Summary,
    bool Stale, IReadOnlyList<string> Sources)
{
    public int Minutes => Pomodoros.Sum(e => e.Block.Minutes);
    public int Count => Pomodoros.Count();

    /// <summary>Roughly how long at the computer outside pomodoros; only meaningful when <see cref="WindowsRecorded"/>.</summary>
    public int OffTimerMinutes => Entries.Where(e => e.OffTimer).Sum(e => e.Block.Minutes);

    /// <summary>
    /// The windows in front were recorded: without them (days before recording, Wayland), time
    /// off the timer is only the moments something left a trace, not how long it lasted.
    /// </summary>
    public bool WindowsRecorded => Sources.Contains(JournalService.WindowsSource);

    private IEnumerable<JournalEntry> Pomodoros => Entries.Where(e => !e.OffTimer && !e.InProgress);

    /// <summary>Entries that get written up: finished, and not off the record.</summary>
    public IEnumerable<JournalEntry> Writable => Entries.Where(e => !e.InProgress && !e.OffRecord);
    public SessionSummary? SessionSummary(int number) => Summary?.Sessions.FirstOrDefault(s => s.Number == number);
}

/// <summary>Why a write-up didn't happen.</summary>
public sealed record JournalProblem(string Message, bool SignedOut);

/// <summary>What forgetting a stretch of time did.</summary>
/// <param name="Entries">Pomodoros and stretches off the timer it reached into.</param>
/// <param name="WithConversations">Ask YAPA's conversations were to go too: the saved ones since, and the one on screen.</param>
/// <param name="Conversations">Saved Ask YAPA conversations deleted with it.</param>
public sealed record ForgetOutcome(DateTime FromUtc, DateTime ToUtc, int Entries, bool WithConversations, int Conversations);

/// <summary>
/// The day journal: a day's pomodoros, and the time at the computer around them, with what
/// was going on in each, put together from the timer's history, Claude Code's transcripts,
/// git, browser history and the windows recorded; then written up by Claude, and kept so it's
/// only rewritten when the evidence changes. Everything stays on this computer except the
/// write-up request, which goes through the user's own Claude sign-in. Time that is off the
/// record (Tor Browser open, an adult site in front, gone off the record, or forgotten
/// afterwards) has nothing on record from any source.
/// </summary>
public sealed class JournalService : IDisposable
{
    /// <summary>A pomodoro's commits often land in the break after it; the write-up waits for them.</summary>
    public static readonly TimeSpan WriteDelay = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan EvidenceMargin = TimeSpan.FromMinutes(45);

    private readonly IPomodoroRepository _history;
    private readonly TimerEventLog _events;
    private readonly IPomodoroEngine? _engine;
    private readonly JournalStore _store;
    private readonly JournalSettings _settings;
    private readonly ClaudeAccount _account;
    private readonly ClaudeCodeHistory _claude;
    private readonly GitHistory _git;
    private readonly BrowserHistory _browsers;
    private readonly OffTheRecord _offRecord;
    private readonly AssistantMemory? _memory;
    private readonly TimeZoneInfo _zone;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private CancellationTokenSource? _pendingWrite;

    public JournalService(IPomodoroRepository history, TimerEventLog events, IPomodoroEngine? engine, JournalStore store, JournalSettings settings,
        ClaudeAccount account, ClaudeCodeHistory? claude = null, GitHistory? git = null, BrowserHistory? browsers = null, TimeZoneInfo? zone = null,
        Func<DateTime>? utcNow = null, OffTheRecord? offRecord = null, AssistantMemory? memory = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _history = history;
        _events = events;
        _engine = engine;
        _store = store;
        _settings = settings;
        _account = account;
        _claude = claude ?? new ClaudeCodeHistory();
        _git = git ?? new GitHistory();
        _browsers = browsers ?? new BrowserHistory();
        _offRecord = offRecord ?? new OffTheRecord(store, _utcNow);
        _memory = memory;
        _zone = zone ?? TimeZoneInfo.Local;
        _history.Changed += OnHistoryChanged;
    }

    public const string WindowsSource = "windows in front";

    public ClaudeAccount Account => _account;

    /// <summary>A day's journal changed (written up in the background, retitled). Raised on any thread.</summary>
    public event Action<DateOnly>? DayChanged;

    /// <summary>
    /// Time was forgotten and its traces are gone. Raised on the caller's thread; the assistant
    /// drops the conversation on screen, which may have seen it.
    /// </summary>
    public event Action<ForgetOutcome>? Forgotten;

    public OffTheRecord OffRecord => _offRecord;

    /// <summary>A write-up is being made.</summary>
    public bool IsWriting => _writing.CurrentCount == 0;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_utcNow(), _zone));

    public (DateTime From, DateTime To) Bounds(DateOnly day)
    {
        var from = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), _zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), _zone);
        return (from, to);
    }

    /// <summary>Days before <paramref name="day"/> and after it that have pomodoros, for stepping through.</summary>
    public (DateOnly? Previous, DateOnly? Next) Neighbours(DateOnly day)
    {
        var (from, to) = Bounds(day);
        var all = _history.All();
        DateOnly Local(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone));
        var before = all.LastOrDefault(p => p.DateTime < from);
        var after = all.FirstOrDefault(p => p.DateTime >= to);
        return (before != null ? Local(before.DateTime) : null, after != null ? Local(after.DateTime) : null);
    }

    /// <summary>What a day of the calendar has: pomodoros done, and whether windows were recorded (at the computer).</summary>
    public readonly record struct DayMark(int Pomodoros, bool AtComputer);

    /// <summary>Each day in [first, last] that has anything, for the calendar. Reads the database: off the UI thread.</summary>
    public IReadOnlyDictionary<DateOnly, DayMark> Marks(DateOnly first, DateOnly last)
    {
        var (from, _) = Bounds(first);
        var (_, to) = Bounds(last);
        DateOnly Local(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone));
        var marks = new Dictionary<DateOnly, DayMark>();
        foreach (var p in _history.Between(from, to))
        {
            var day = Local(p.DateTime);
            marks[day] = marks.GetValueOrDefault(day) with { Pomodoros = marks.GetValueOrDefault(day).Pomodoros + Math.Max(1, p.Count) };
        }
        foreach (var hour in _store.RecordedHours(from, to))
        {
            var day = Local(hour);
            marks[day] = marks.GetValueOrDefault(day) with { AtComputer = true };
        }
        return marks;
    }

    // ── Reading a day ────────────────────────────────────────────────────────

    /// <summary>The day with its evidence and whatever was written up before. Reads files: call off the UI thread.</summary>
    public JournalDay Load(DateOnly day)
    {
        var watch = Stopwatch.StartNew();
        var (dayStart, dayEnd) = Bounds(day);
        var now = _utcNow();
        var until = dayEnd < now ? dayEnd : now;
        var pomodoros = Blocks(day);
        var inProgress = day == Today ? Running() : null;
        if (inProgress != null) pomodoros = [.. pomodoros, inProgress];

        // The whole day, and a little either side of its pomodoros: a prompt typed just before one says what it's for.
        var from = pomodoros.Count > 0 && pomodoros[0].StartUtc - EvidenceMargin < dayStart ? pomodoros[0].StartUtc - EvidenceMargin : dayStart;
        var to = pomodoros.Count > 0 && pomodoros[^1].EndUtc + JournalBuilder.CommitGrace > until ? pomodoros[^1].EndUtc + JournalBuilder.CommitGrace : until;
        var (evidence, paused) = to > from
            ? WithoutPaused(Gather(from, to), _store.OffRecord(from, to).Select(r => (r.StartUtc, r.EndUtc < now ? r.EndUtc : now)))
            : (EvidenceSet.Empty, []);

        var offTimer = _settings.WholeDay && until > dayStart ? JournalBuilder.OffTimer(pomodoros, evidence, dayStart, until, paused) : [];
        var claimed = JournalBuilder.Claimed(pomodoros, evidence.Commits).Select(c => c.Hash).ToHashSet();
        var attributed = JournalBuilder.Attribute(pomodoros, evidence)
            .Concat(JournalBuilder.Attribute(offTimer, evidence with { Commits = evidence.Commits.Where(c => !claimed.Contains(c.Hash)).ToList() }))
            .ToList();
        var blocks = pomodoros.Concat(offTimer).ToList();
        var sessions = JournalBuilder.Sessions(pomodoros);
        var stored = _store.Day(day);
        var titles = _store.Titles(blocks.Select(b => b.PomodoroId));

        var entries = new List<JournalEntry>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var print = JournalPrompt.Fingerprint(attributed[i]);
            // A stretch with no pomodoro after it that's still going is like the pomodoro under way: written once it's over.
            var isRunning = ReferenceEquals(block, inProgress)
                            || (block.OffTimer && day == Today && now - block.EndUtc < JournalBuilder.Quiet && !pomodoros.Any(p => p.StartUtc >= block.EndUtc));
            // Off the timer there's nothing left once the time is gone; a pomodoro stays, as a pomodoro and nothing more.
            if (!block.OffTimer && Uncovered(block.StartUtc, block.EndUtc, paused) < OffRecordSlack)
            {
                entries.Add(new JournalEntry(block, SessionOf(block, sessions), BlockEvidence.None, "", null, null, isRunning, OffRecord: true,
                    Paused: ReferenceEquals(block, inProgress) && TimerPaused));
                continue;
            }
            PomodoroSummary? summary = null;
            if (!isRunning && stored != null && stored.Fingerprints.TryGetValue(block.PomodoroId, out var was) && was == print)
                summary = stored.Summary.Pomodoros.FirstOrDefault(p => p.PomodoroId == block.PomodoroId);
            entries.Add(new JournalEntry(block, SessionOf(block, sessions), attributed[i], print, summary, titles.GetValueOrDefault(block.PomodoroId), isRunning,
                Paused: ReferenceEquals(block, inProgress) && TimerPaused));
        }
        entries.Sort((a, b) => a.Block.StartUtc.CompareTo(b.Block.StartUtc));
        // An entry not written for its evidence, or the day's own lines cleared by forgetting.
        var stale = entries.Any(e => !e.InProgress && !e.OffRecord && e.Summary == null)
                    || (stored is { Summary.Headline.Length: 0 } && entries.Any(e => !e.InProgress && !e.OffRecord));

        var sources = new List<string>();
        if (evidence.Sessions.Count > 0) sources.Add("Claude Code");
        if (evidence.Commits.Count > 0) sources.Add("git");
        if (evidence.Visits.Count > 0) sources.Add("browser history");
        if (evidence.Windows.Count > 0) sources.Add(WindowsSource);
        Trace.WriteLine($"[journal] {day:yyyy-MM-dd}: {pomodoros.Count} pomodoros, {offTimer.Count} off the timer, {evidence.Sessions.Count} conversations, " +
                        $"{evidence.Commits.Count} commits, {evidence.Visits.Count} visits, {evidence.Windows.Count} windows, {paused.Count} paused in {watch.ElapsedMilliseconds} ms");
        return new JournalDay(day, entries, sessions, stored?.Summary, stale, sources);
    }

    /// <summary>A pomodoro with less than this of its time on the record is off the record as a whole.</summary>
    private static readonly TimeSpan OffRecordSlack = TimeSpan.FromMinutes(1);

    /// <summary>How much of [start, end] lies outside every one of <paramref name="ranges"/> (which may overlap).</summary>
    public static TimeSpan Uncovered(DateTime start, DateTime end, IReadOnlyList<(DateTime Start, DateTime End)> ranges)
    {
        var free = TimeSpan.Zero;
        var at = start;
        foreach (var (s, e) in ranges.Where(r => r.End > start && r.Start < end).OrderBy(r => r.Start))
        {
            if (s > at) free += s - at;
            if (e > at) at = e;
            if (at >= end) break;
        }
        if (end > at) free += end - at;
        return free;
    }

    /// <summary>A pomodoro's session; for time off the timer, the session it's a break inside of, else 0.</summary>
    private static int SessionOf(WorkBlock block, IReadOnlyList<WorkSession> sessions)
    {
        if (!block.OffTimer) return sessions.First(s => s.Blocks.Contains(block)).Number;
        var inside = sessions.FirstOrDefault(s => s.StartUtc <= block.StartUtc && block.EndUtc <= s.EndUtc);
        return inside?.Number ?? 0;
    }

    /// <summary>The day's pomodoros as stretches of time, with when each really started and its cycle.</summary>
    private IReadOnlyList<WorkBlock> Blocks(DateOnly day)
    {
        var (from, to) = Bounds(day);
        var pomodoros = _history.Between(from, to);
        if (pomodoros.Count == 0) return [];
        var events = _events.Between(from - TimeSpan.FromHours(4), to + TimeSpan.FromMinutes(5));
        var starts = events.Where(e => e.Kind == nameof(PomodoroTransitionKind.WorkStarted)).Select(e => e.AtUtc).ToList();
        var completions = events.Where(e => e.Kind == nameof(PomodoroTransitionKind.WorkCompleted)).ToList();

        var blocks = new List<WorkBlock>();
        foreach (var p in pomodoros)
        {
            var end = DateTime.SpecifyKind(p.DateTime, DateTimeKind.Utc);
            var minutes = Math.Max(1, p.DurationMin * Math.Max(1, p.Count));
            var nominal = end - TimeSpan.FromMinutes(minutes);
            // Its WorkStarted: the latest one before at least half of it had passed, not too long before (pauses).
            var started = starts.LastOrDefault(s => s <= end - TimeSpan.FromMinutes(minutes / 2.0) && s >= nominal - TimeSpan.FromMinutes(90));
            var start = started != default ? started : nominal;
            if (blocks.Count > 0 && start < blocks[^1].EndUtc) start = blocks[^1].EndUtc > nominal ? blocks[^1].EndUtc : nominal;
            var cycle = completions.Where(c => Math.Abs((c.AtUtc - end).TotalMinutes) <= 3).Select(c => (int?)c.Cycle).FirstOrDefault();
            blocks.Add(new WorkBlock(p.Id, start, end, minutes, cycle));
        }
        return blocks;
    }

    /// <summary>The pomodoro under way, as far as it's got; null when none is.</summary>
    private WorkBlock? Running()
    {
        if (_engine == null) return null;
        var phase = _engine.Phase == PomodoroPhase.Pause ? _engine.PausedPhase : _engine.Phase;
        if (phase != PomodoroPhase.Work || _engine.Elapsed <= 0) return null;
        var now = _utcNow();
        var elapsed = TimeSpan.FromSeconds(_engine.Elapsed);
        // When it really started: "now less the time worked" drifts later for every minute it sits
        // paused. The WorkStarted it began with says, if it's in the log and old enough to be it.
        var started = _events.Between(now - elapsed - TimeSpan.FromHours(6), now - elapsed)
            .LastOrDefault(e => e.Kind == nameof(PomodoroTransitionKind.WorkStarted))?.AtUtc;
        return new WorkBlock(0, started ?? now - elapsed, now, Math.Max(1, _engine.Elapsed / 60), _engine.Cycle);
    }

    private bool TimerPaused => _engine?.Phase == PomodoroPhase.Pause;

    private EvidenceSet Gather(DateTime fromUtc, DateTime toUtc)
    {
        Task<T> Try<T>(bool on, Func<T> read, T none, string name) => !on ? Task.FromResult(none) : Task.Run(() =>
        {
            try { return read(); }
            catch (Exception e)
            {
                // One source failing leaves the others; the journal is evidence, not an all-or-nothing report.
                Trace.WriteLine($"[journal] {name} unreadable: {e}");
                return none;
            }
        });

        var sessions = Try(_settings.ClaudeCode, () => _claude.Read(fromUtc, toUtc), (IReadOnlyList<AgentSession>)[], "Claude Code");
        var visits = Try(_settings.Browsers, () => _browsers.Read(fromUtc, toUtc), (IReadOnlyList<Visit>)[], "browser history");
        var windows = Try(true, () => _store.Spans(fromUtc, toUtc), (IReadOnlyList<WindowSpan>)[], "windows");
        var commits = Try(_settings.Git, () => _git.Read(fromUtc, toUtc, sessions.Result.Select(s => s.Folder)), (IReadOnlyList<Commit>)[], "git");
        Task.WaitAll(sessions, visits, windows, commits);
        return Private(new EvidenceSet(sessions.Result, commits.Result, visits.Result, windows.Result));
    }

    /// <summary>
    /// The skip list: whatever matches is left out, as if it never happened. Adult pages are
    /// left out too, and the time an adult window was in front becomes paused time (windows
    /// recorded before the filter, or with it off then), so nothing else from it is used.
    /// </summary>
    private EvidenceSet Private(EvidenceSet e)
    {
        var skip = _settings.IgnoredTerms.Count > 0;
        var adult = _settings.SkipAdult;
        if (!skip && !adult) return e;
        bool Hidden(params string[] texts) => skip && _settings.IsIgnored(texts);
        bool Adult(Visit v) => adult && (AdultSites.IsAdultHost(v.Site) || AdultSites.IsAdultText(v.Title));
        return new EvidenceSet(
            e.Sessions.Where(s => !Hidden(s.Title, s.Project, s.Folder)).Select(s => s with { Prompts = s.Prompts.Where(p => !Hidden(p.Text)).ToList() }).ToList(),
            e.Commits.Where(c => !Hidden(c.Repo, c.Subject)).ToList(),
            e.Visits.Where(v => !Hidden(v.Site, v.Title) && !Adult(v)).ToList(),
            e.Windows.Where(w => w.App == JournalStore.Paused || !Hidden(w.App, w.Title))
                .Select(w => adult && w.App != JournalStore.Paused && AdultSites.IsAdultText(w.Title) ? w with { App = JournalStore.Paused, Title = "" } : w)
                .ToList());
    }

    /// <summary>
    /// The time off the record (the journal paused while an app like Tor Browser was open, or
    /// an adult site in front, and <paramref name="offRecord"/>: gone off the record, or
    /// forgotten), and the evidence without anything from it: no page, prompt, commit or
    /// window from any source in that time.
    /// </summary>
    public static (EvidenceSet Evidence, IReadOnlyList<(DateTime Start, DateTime End)> Paused) WithoutPaused(EvidenceSet e,
        IEnumerable<(DateTime Start, DateTime End)>? offRecord = null)
    {
        var paused = e.Windows.Where(w => w.App == JournalStore.Paused).Select(w => (Start: w.StartUtc, End: w.EndUtc))
            .Concat(offRecord ?? []).OrderBy(p => p.Start).ToList();
        var windows = e.Windows.Where(w => w.App != JournalStore.Paused).ToList();
        if (paused.Count == 0) return (e with { Windows = windows }, paused);

        // A sample lands every few seconds: the edges are only that exact.
        var slack = TimeSpan.FromSeconds(10);
        bool In(DateTime t) => paused.Any(p => t >= p.Start - slack && t <= p.End + slack);
        var clean = new EvidenceSet(
            e.Sessions.Select(s => s with { Prompts = s.Prompts.Where(p => !In(p.AtUtc)).ToList(), Activity = s.Activity.Where(t => !In(t)).ToList() })
                .Where(s => s.Prompts.Count > 0 || s.Activity.Count > 0).ToList(),
            e.Commits.Where(c => !In(c.AtUtc)).ToList(),
            e.Visits.Where(v => !In(v.AtUtc)).ToList(),
            windows.SelectMany(w => Outside(w, paused)).ToList());
        return (clean, paused);
    }

    private static IEnumerable<WindowSpan> Outside(WindowSpan w, IReadOnlyList<(DateTime Start, DateTime End)> paused)
    {
        var start = w.StartUtc;
        foreach (var p in paused.Where(p => p.End > w.StartUtc && p.Start < w.EndUtc))
        {
            if (p.Start > start) yield return w with { StartUtc = start, EndUtc = p.Start };
            if (p.End > start) start = p.End;
        }
        if (w.EndUtc > start) yield return w with { StartUtc = start };
    }

    // ── Writing it up ────────────────────────────────────────────────────────

    /// <summary>
    /// Has Claude write up whatever isn't written for its current evidence (and the sessions and
    /// day around it), keeps it, and returns the day again. A day that's current is returned as is.
    /// </summary>
    public async Task<(JournalDay Day, JournalProblem? Problem)> WriteAsync(DateOnly date, CancellationToken cancel = default)
    {
        await _writing.WaitAsync(cancel);
        try
        {
            var day = await Task.Run(() => Load(date), cancel);
            if (!day.Stale || !_settings.Summaries) return (day, null);

            // Off the record isn't sent at all, not even that it was there.
            var blocks = day.Writable
                .Select(e => new PromptBlock(e.Block, e.Session, e.Evidence, e.Summary, e.UserTitle))
                .ToList();
            var request = JournalPrompt.Build(date, blocks, _zone, day.WindowsRecorded);
            var watch = Stopwatch.StartNew();
            var result = await ClaudeOneShot.AskAsync(JournalPrompt.System, request, JournalPrompt.Schema, "sonnet", _account, cancel);
            if (result.Answer == null || JournalPrompt.Parse(result.Answer) is not { } answer)
                return (day, new JournalProblem(result.Error ?? "Claude's answer didn't make sense", result.SignedOut));
            Trace.WriteLine($"[journal] wrote up {date:yyyy-MM-dd} ({blocks.Count(b => b.Kept == null)} new) in {watch.ElapsedMilliseconds} ms");

            // Kept entries keep their lines; new ones take the answer's.
            var written = answer.Pomodoros.GroupBy(p => p.PomodoroId).ToDictionary(g => g.Key, g => g.First());
            var pomodoros = new List<PomodoroSummary>();
            var prints = new Dictionary<int, string>();
            foreach (var e in day.Writable)
            {
                var summary = e.Summary ?? written.GetValueOrDefault(e.Block.PomodoroId)
                    ?? new PomodoroSummary(e.Block.PomodoroId, "", "", []); // left out: treat as nothing on record
                pomodoros.Add(summary);
                prints[e.Block.PomodoroId] = e.Fingerprint;
            }
            _store.SaveDay(date, answer with { Pomodoros = pomodoros }, prints);
            var fresh = await Task.Run(() => Load(date), cancel);
            DayChanged?.Invoke(date);
            return (fresh, null);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>The user named a pomodoro; blank gives it back to the write-up.</summary>
    public void SetTitle(DateOnly day, int pomodoroId, string? title)
    {
        _store.SetTitle(pomodoroId, title);
        // The sessions and the day were written around the old title: rewrite them.
        if (_store.Day(day) is { } stored)
        {
            var prints = stored.Fingerprints.ToDictionary(p => p.Key, p => p.Value);
            prints[pomodoroId] = "retitled";
            _store.SaveDay(day, stored.Summary, prints);
        }
        DayChanged?.Invoke(day);
        ScheduleWrite(day, TimeSpan.FromSeconds(2));
    }

    /// <summary>Everything the journal recorded, wrote or indexed (the transcript index holds what the user typed).</summary>
    public void Forget()
    {
        _store.Forget();
        try { System.IO.Directory.Delete(System.IO.Path.Combine(YAPA.Avalonia.Platform.AppPaths.CacheDirectory, "journal"), recursive: true); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
        _claude.Reset();
    }

    public string? UserTitle(int pomodoroId) => _store.Titles([pomodoroId]).GetValueOrDefault(pomodoroId);

    // ── Forgetting ───────────────────────────────────────────────────────────

    /// <summary>Ask YAPA conversations that could have seen [fromUtc, …]: those still going on at or after it.</summary>
    public int ConversationsSince(DateTime fromUtc) => _memory?.CountSince(fromUtc) ?? 0;

    /// <summary>
    /// Takes [fromUtc, toUtc] off the record for good and deletes its traces: the windows
    /// recorded in it, the write-ups of everything it reached into (and the day's and
    /// sessions' lines around them), the names given to pomodoros it covers, what the
    /// transcript index copied from it, and, with <paramref name="conversations"/>, the Ask
    /// YAPA conversations since. What other programs keep (the browser's history, Claude
    /// Code's transcripts) stays where it is, and is never read for that time again. The
    /// pomodoros themselves stay: they're the timer's, and they still count.
    /// </summary>
    /// <remarks>Reads and writes files: call off the UI thread.</remarks>
    public ForgetOutcome Forget(DateTime fromUtc, DateTime toUtc, bool conversations)
    {
        var now = _utcNow();
        if (toUtc > now) toUtc = now;
        if (toUtc <= fromUtc) throw new ArgumentException("Nothing to forget: the range is empty or in the future.");

        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, _zone));
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(toUtc, _zone));
        var reached = 0;
        for (var day = first; day <= last; day = day.AddDays(1))
            reached += Load(day).Entries.Count(e => !e.OffRecord && e.Block.EndUtc > fromUtc && e.Block.StartUtc < toUtc);

        // The range first: from here on nothing from that time is read, whatever fails below.
        _offRecord.Forget(fromUtc, toUtc);
        _store.DeleteSpans(fromUtc, toUtc);
        for (var day = first; day <= last; day = day.AddDays(1)) ForgetWriteUps(day, fromUtc, toUtc);

        _claude.Scrub(fromUtc, toUtc);
        var deleted = conversations && _memory != null ? _memory.ForgetSince(fromUtc) : 0;
        var outcome = new ForgetOutcome(fromUtc, toUtc, reached, conversations, deleted);
        Trace.WriteLine($"[journal] forgot {reached} entries and {deleted} conversations");
        Forgotten?.Invoke(outcome);

        for (var day = first; day <= last; day = day.AddDays(1)) DayChanged?.Invoke(day);
        // The day's own lines go with it; written again without that time (the last day here, the others when opened).
        ScheduleWrite(last, TimeSpan.Zero);
        return outcome;
    }

    /// <summary>
    /// Drops the day's written lines that could say anything about the forgotten time: those
    /// of every entry whose evidence changed with it (or that it cut away entirely, like a
    /// stretch off the timer), and the day's and sessions' lines, which may mention any of them.
    /// Lines written for evidence that's still exactly as it was are kept.
    /// </summary>
    private void ForgetWriteUps(DateOnly date, DateTime fromUtc, DateTime toUtc)
    {
        // Read again with the range in place: what's left of the day.
        var day = Load(date);
        // Their names may say what was done; a pomodoro only partly forgotten keeps the name it was given.
        _store.RemoveTitles(day.Entries.Where(e => e.OffRecord && e.Block.PomodoroId > 0 && e.Block.EndUtc > fromUtc && e.Block.StartUtc < toUtc)
            .Select(e => e.Block.PomodoroId));

        if (_store.Day(date) is not { } stored) return;
        var standing = day.Entries
            .Where(e => !e.OffRecord && stored.Fingerprints.TryGetValue(e.Block.PomodoroId, out var print) && print == e.Fingerprint)
            .Select(e => e.Block.PomodoroId)
            .ToHashSet();
        var kept = stored.Summary.Pomodoros.Where(p => standing.Contains(p.PomodoroId)).ToList();
        var prints = stored.Fingerprints.Where(p => standing.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        _store.SaveDay(date, new DaySummary("", "", [], kept), prints);
    }

    private void OnHistoryChanged(HistoryChange change)
    {
        var at = (change.After ?? change.Before)?.DateTime;
        if (at == null) return;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at.Value, DateTimeKind.Utc), _zone));
        DayChanged?.Invoke(day);
        ScheduleWrite(day, WriteDelay);
    }

    /// <summary>Writes the day up a little later, in the background, so it's ready when it's opened.</summary>
    public void ScheduleWrite(DateOnly day, TimeSpan delay)
    {
        if (!_settings.Summaries) return;
        _pendingWrite?.Cancel();
        var cancel = _pendingWrite = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cancel.Token);
                var (_, problem) = await WriteAsync(day, cancel.Token);
                if (problem != null) Trace.WriteLine($"[journal] background write-up of {day:yyyy-MM-dd}: {problem.Message}");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Trace.WriteLine($"[journal] background write-up failed: {e}"); }
        });
    }

    public void Dispose()
    {
        _history.Changed -= OnHistoryChanged;
        _pendingWrite?.Cancel();
    }
}
