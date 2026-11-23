using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <param name="Prompt">What it puts in the prompt.</param>
/// <param name="Send">Runs at once; otherwise it fills the prompt to finish or change first
/// (anything with a number in it the user may want to change, or that writes history).</param>
public sealed record Suggestion(string Label, string Prompt, bool Send);

/// <summary>
/// One line of an answer: a paragraph, or a list item drawn with its bullet hanging, so
/// a wrapped item lines up under its own text rather than under the bullet.
/// </summary>
public sealed partial class AnswerBlock : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isBullet;

    /// <summary>A blank line before it: a new paragraph, with room to breathe.</summary>
    [ObservableProperty] private bool _startsParagraph;
}

/// <summary>One request and its answer.</summary>
public sealed partial class Exchange : ObservableObject
{
    public Exchange(string question) => Question = question;

    /// <summary>Said around forgetting: stays on screen, but is never archived or sent to Claude again.</summary>
    public bool NotArchived { get; set; }

    public string Question { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer))]
    private string _answer = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus), nameof(CanCopy), nameof(CanRetry), nameof(HasFooter))]
    private bool _isPending = true;

    public bool HasAnswer => Answer.Length > 0;

    /// <summary>The answer, line by line, kept in step with <see cref="Answer"/> as it streams in.</summary>
    public ObservableCollection<AnswerBlock> Blocks { get; } = [];

    // Streaming changes the last line or adds one; the earlier ones are updated in place,
    // not rebuilt, so the text already on screen doesn't flicker.
    partial void OnAnswerChanged(string value)
    {
        var lines = new List<(string Text, bool Bullet, bool Paragraph)>();
        var gap = false;
        foreach (var raw in value.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                gap = lines.Count > 0;
                continue;
            }
            var bullet = line.StartsWith("• ", StringComparison.Ordinal);
            lines.Add((bullet ? line[2..].TrimStart() : line, bullet, gap));
            gap = false;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == Blocks.Count) Blocks.Add(new AnswerBlock());
            var (text, bullet, paragraph) = lines[i];
            Blocks[i].Text = text;
            Blocks[i].IsBullet = bullet;
            // A list follows its lead-in closely; a paragraph after a paragraph gets a gap.
            Blocks[i].StartsParagraph = i > 0 && (paragraph || !bullet && Blocks[i - 1].IsBullet);
        }
        while (Blocks.Count > lines.Count) Blocks.RemoveAt(Blocks.Count - 1);
    }

    /// <summary>Copied to the clipboard a moment ago: the button says so.</summary>
    [ObservableProperty] private bool _copied;

    public bool CanCopy => !IsPending && Answer.Length > 0 && !IsError;

    /// <summary>It failed: the question can be sent again as it was.</summary>
    public bool CanRetry => !IsPending && IsError && !AwaitsSignIn;

    /// <summary>Anything under the answer: Undo, Undone, Copy, Try again.</summary>
    public bool HasFooter => CanCopy || CanRetry || CanUndo || WasUndone;

    /// <summary>Held until the user signs in to Claude; it's sent then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus), nameof(CanRetry))]
    private bool _awaitsSignIn;

    /// <summary>What it's doing while no words are coming: thinking, or a tool's step. Null while it speaks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    private AgentStatus? _status;

    public bool ShowStatus => IsPending && !AwaitsSignIn && Status != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy), nameof(CanRetry), nameof(HasFooter))]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    private bool _wasUndone;
    [ObservableProperty] private bool _isLatest = true;

    /// <summary>What it changed, one line each ("Logged 3 pomodoros").</summary>
    public ObservableCollection<string> Actions { get; } = [];

    public bool HasActions => Actions.Count > 0;

    internal void AddAction(string text)
    {
        Actions.Add(text);
        OnPropertyChanged(nameof(HasActions));
    }
}

/// <summary>
/// The assistant panel: the conversation, the caption under the orb and the orb's mood.
/// Turns Claude's stream and the tools' activity into what the panel shows.
/// </summary>
public sealed partial class AssistantViewModel : ObservableObject
{
    private readonly AssistantService _service;
    private readonly IPomodoroEngine _engine;
    private readonly IPomodoroRepository _history;
    private readonly Specifics.GeneralSettings _general;
    private readonly AssistantMemory _memory;
    private readonly ClaudeAccount _account;
    private string _greeting = "";
    private DateTime _startedUtc = DateTime.UtcNow;
    private Exchange? _current;
    private bool _replaceOnNextText; // the text so far led up to a tool call; the answer comes after it
    private bool _interrupted;
    private bool _toolsUnreachable;
    private bool _forgetAfterTurn;

    // Requests sent whose result hasn't arrived. Claude Code queues a request sent while an
    // earlier one winds down, so anything streamed while more than one is outstanding still
    // belongs to an earlier, finished exchange.
    private int _outstanding;

    public AssistantViewModel(AssistantService service, IPomodoroEngine engine, IPomodoroRepository history, Specifics.GeneralSettings general,
        AssistantMemory memory, YAPA.Avalonia.Journal.JournalService? journal = null)
    {
        // Forgetting can happen on any thread (a tool, the Day window's worker).
        if (journal != null) journal.Forgotten += o => Dispatcher.UIThread.Post(() => OnForgotten(o));
        _memory = memory;
        _service = service;
        _engine = engine;
        _history = history;
        _general = general;
        _account = service.Account;
        _account.Changed += OnAccountChanged;
        _account.SignedIn += OnSignedIn;
        PickVoice();
        _service.Event += OnEvent;
        _service.ToolActivity += OnToolActivity;
        _service.Crashed += OnCrashed;
        _service.Journal.Changed += RefreshUndo;
        _engine.PropertyChanged += OnEngineChanged;
        UpdatePalette();
        UpdateSuggestions();
    }

    public ObservableCollection<Exchange> Exchanges { get; } = [];

    public ObservableCollection<Suggestion> Suggestions { get; } = [];

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string _placeholder = "";
    [ObservableProperty] private OrbMood _mood = OrbMood.Idle;
    [ObservableProperty] private OrbPalette _palette = OrbPalette.Calm;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasConversation;
    [ObservableProperty] private bool _isUnavailable;

    /// <summary>No sign-in works: the prompt makes way for a Sign in button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn), nameof(ShowPrompt), nameof(SignInButton))]
    private bool _needsSignIn;

    /// <summary>YAPA's token runs out soon; the button offers a new one, and the prompt stays.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    private bool _renewSoon;

    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string _signInText = "";

    public bool ShowSignIn => NeedsSignIn || RenewSoon;

    public string SignInButton => NeedsSignIn ? "Sign in" : "Renew";

    public bool ShowPrompt => !IsUnavailable && !NeedsSignIn;

    /// <summary>The orb should ripple: something was just done.</summary>
    public event Action? ActionCompleted;

    /// <summary>An answer is complete: the orb settles with a soft ripple.</summary>
    public event Action? Answered;

    /// <summary>A piece of the answer arrived: the orb moves with it, like a voice.</summary>
    public event Action? Spoke;

    /// <summary>The panel wants to scroll to the newest text (if the reader is following it).</summary>
    public event Action? ContentGrew;

    /// <summary>A new question went in: the panel scrolls to it whatever was being read.</summary>
    public event Action? Asked;

    public bool ShowSuggestions => !HasConversation && !IsUnavailable;

    partial void OnHasConversationChanged(bool value) => OnPropertyChanged(nameof(ShowSuggestions));

    partial void OnIsUnavailableChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSuggestions));
        OnPropertyChanged(nameof(ShowPrompt));
    }

    /// <summary>The panel is opening: warm Claude Code up while the user types.</summary>
    /// <summary>A fresh greeting and placeholder for the moment.</summary>
    private void PickVoice()
    {
        var line = Voice.For(_engine.Phase, _engine.PausedPhase, DateTime.Now, _history.CompletedToday(), _general.DailyGoal);
        _greeting = line.Greeting;
        Placeholder = line.Placeholder;
    }

    private string ReadyCaption => _greeting;

    public void Opened()
    {
        UpdatePalette();
        if (!IsBusy) PickVoice();
        if (!HasConversation) UpdateSuggestions();
        if (!AssistantService.IsInstalled)
        {
            IsUnavailable = true;
            Mood = OrbMood.Error;
            Caption = "I need Claude Code to think. Install it from claude.com/code and sign in, then open me again.";
            return;
        }
        IsUnavailable = false;
        if (Mood == OrbMood.Error && !IsBusy) Mood = OrbMood.Idle;
        // Back after a while: last time's "Undone." or error is stale.
        if (!IsBusy && !NeedsSignIn && !IsSigningIn) Caption = HasConversation ? "" : ReadyCaption;
        RenewSoon = !NeedsSignIn && _account.RenewalDue(DateTime.UtcNow);
        if (RenewSoon && !IsSigningIn) SignInText = RenewText;
        _service.EnsureStarted();
        // Found out now rather than after the user has typed a question.
        if (_account.Token == null && !NeedsSignIn && !IsSigningIn) _ = CheckSignInAsync();
    }

    private async System.Threading.Tasks.Task CheckSignInAsync()
    {
        if (await ClaudeAccount.IsClaudeCodeSignedInAsync() != false || _account.Token != null || IsBusy || NeedsSignIn) return;
        AskToSignIn("Sign in once and I'm good for a year.");
    }

    public void Hidden() => _service.PanelHidden();

    [RelayCommand]
    private void Submit()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsUnavailable) return;
        Input = "";
        Ask(text);
    }

    /// <returns>True when it was sent; false when it only filled the prompt.</returns>
    public bool UseSuggestion(Suggestion suggestion)
    {
        if (suggestion.Send)
        {
            Ask(suggestion.Prompt);
            return true;
        }
        Input = suggestion.Prompt;
        return false;
    }

    private void Ask(string text)
    {
        // A new request replaces one still under way, as speaking over someone does.
        if (IsBusy) Stop();

        if (_current != null) _current.IsLatest = false;
        // The conversation behind the panel ended (idle, or a crash) but the panel still shows
        // it: a new one starts with a recap, so "as I said" still means something.
        if (!_service.HasContext && Exchanges.Count > 0) _service.Note(Recap());
        if (Exchanges.Count == 0) _startedUtc = DateTime.UtcNow;
        _current = new Exchange(text);
        Exchanges.Add(_current);
        HasConversation = true;
        _replaceOnNextText = false;
        _interrupted = false;
        IsBusy = true;
        Mood = OrbMood.Thinking;
        // What it's doing shows in the conversation, under the question, where the eye is now.
        Caption = "";
        _current.Status = AgentStatus.Thinking;
        _outstanding++;
        Asked?.Invoke();
        if (!_service.Ask(text))
        {
            _outstanding--;
            Finish(_current, "I can't find Claude Code any more. Install it from claude.com/code and sign in, then ask me again.");
            return;
        }
        ContentGrew?.Invoke();
    }

    /// <summary>Sends a failed question again, as it was.</summary>
    [RelayCommand]
    private void Retry(Exchange exchange)
    {
        if (!exchange.CanRetry) return;
        // The failed attempt makes way for the new one, rather than the question showing twice.
        var wasLatest = ReferenceEquals(exchange, _current);
        Exchanges.Remove(exchange);
        if (wasLatest) _current = Exchanges.LastOrDefault();
        Ask(exchange.Question);
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsBusy || _current == null) return;
        _interrupted = true;
        _service.Interrupt();
        Finish(_current, error: null);
        if (_current.Answer.Length == 0) _current.Answer = "Stopped.";
    }

    [RelayCommand]
    private void Undo(Exchange exchange)
    {
        var undone = _service.Journal.Undo();
        if (undone == null) return;
        _service.Note($"The user clicked Undo, which reverted your change: {undone}.");
        App.Bootstrapper?.Resolve<YAPA.Avalonia.Services.AppNavigator>().RefreshSettings();
        exchange.WasUndone = true;
        exchange.CanUndo = false;
        Caption = "Undone.";
        Mood = OrbMood.Idle;
        ActionCompleted?.Invoke();
        RefreshUndo();
    }

    /// <summary>Starts over: a fresh conversation. The old one is archived, for recall_conversations.</summary>
    [RelayCommand]
    private void NewConversation()
    {
        if (IsBusy) Stop();
        Archive();
        _service.End();
        _outstanding = 0;
        Exchanges.Clear();
        _current = null;
        HasConversation = false;
        Caption = NeedsSignIn ? "My Claude sign-in ran out." : ReadyCaption;
        Mood = OrbMood.Idle;
        UpdateSuggestions();
        _service.EnsureStarted();
    }

    /// <summary>
    /// Time was forgotten. The conversation on screen may have read about it, and Claude Code
    /// keeps all of it in mind, so it's let go: the process ends, and nothing said so far is
    /// archived. If forgetting was this request's own doing, that happens once its answer is
    /// in, and the answer stays on screen (but is never archived or sent again).
    /// </summary>
    private void OnForgotten(YAPA.Avalonia.Journal.ForgetOutcome outcome)
    {
        if (!outcome.WithConversations) return;
        if (IsBusy || _current is { IsPending: true })
        {
            _forgetAfterTurn = true;
            return;
        }
        LetGo(keepLast: false);
    }

    private void LetGo(bool keepLast)
    {
        var last = keepLast ? Exchanges.LastOrDefault() : null;
        _service.End();
        _outstanding = 0;
        Exchanges.Clear();
        _current = null;
        if (last != null)
        {
            last.NotArchived = true;
            last.CanUndo = false;
            Exchanges.Add(last);
        }
        _startedUtc = DateTime.UtcNow;
        HasConversation = Exchanges.Count > 0;
        UpdateSuggestions();
    }

    /// <summary>Keeps the conversation on screen in the archive (on a new conversation, and when YAPA quits).</summary>
    public void Archive()
    {
        var done = Exchanges.Where(x => !x.IsPending && !x.NotArchived).ToList();
        if (done.Count == 0) return;
        _memory.Archive(new ArchivedConversation(_startedUtc,
            done.Select(x => new ArchivedExchange(DateTime.UtcNow, x.Question, x.Answer, x.Actions.ToList())).ToList()));
    }

    private string Recap()
    {
        static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
        var lines = Exchanges.Where(x => !x.IsPending && !x.NotArchived).TakeLast(6).Select(x =>
            $"- The user asked: \"{Clip(x.Question, 200)}\". You answered: \"{Clip(x.Answer, 300)}\"" +
            (x.Actions.Count > 0 ? $" and changed: {string.Join("; ", x.Actions)}" : "") + (x.WasUndone ? " (later undone)" : ""));
        return "Your conversation process restarted; this is what was said before it, still on the user's screen:\n" + string.Join("\n", lines);
    }

    // ── Stream ───────────────────────────────────────────────────────────────

    private void OnEvent(ClaudeEvent e)
    {
        if (e is ClaudeTurnResult)
        {
            _outstanding = Math.Max(0, _outstanding - 1);
            if (_outstanding > 0) return; // an earlier request's
        }
        else if (_outstanding > 1)
        {
            return;
        }

        switch (e)
        {
            case ClaudeReady ready:
                _toolsUnreachable = !ready.ToolsConnected;
                if (_toolsUnreachable) System.Diagnostics.Trace.WriteLine("[assistant] Claude Code could not connect to YAPA's tools");
                break;

            case ClaudeMessageStarted:
                if (_current is { IsPending: true }) _replaceOnNextText = _current.Answer.Length > 0;
                break;

            case ClaudeThinking when IsBusy:
                Mood = OrbMood.Thinking;
                ShowStatus(AgentStatus.Thinking);
                break;

            // Words that led up to a tool stay on screen; the status comes back under them.
            // The tool's own step ("Reading your day") follows once YAPA's tools hear the call.
            case ClaudeToolCall when IsBusy:
                Mood = OrbMood.Acting;
                if (_current is { Status: null }) ShowStatus(AgentStatus.Thinking);
                break;

            case ClaudeTextDelta delta when _current is { IsPending: true } exchange && !_interrupted:
                if (_replaceOnNextText)
                {
                    exchange.Answer = "";
                    _replaceOnNextText = false;
                }
                exchange.Answer = Tidy(exchange.Answer + delta.Text, final: false);
                exchange.Status = null; // the words are the sign of life now
                Mood = OrbMood.Speaking;
                Spoke?.Invoke();
                Caption = "";
                ContentGrew?.Invoke();
                break;

            case ClaudeMessageEnded { StopReason: "end_turn" } when _current is { IsPending: true } exchange && !_interrupted:
                // The answer is complete; the turn's result follows a moment later.
                Finish(exchange, error: null);
                break;

            case ClaudeRateLimited limited when _current is { IsPending: true } exchange:
                var when = limited.ResetsAt is { } at ? " until " + ClockFormat.ShortTime(at.LocalDateTime) : " for now";
                Finish(exchange, $"You've reached your Claude usage limit{when}.");
                break;

            case ClaudeTurnResult result:
                if (_current is { IsPending: true } failed && !_interrupted && result.IsError && ClaudeAuth.IsFailure(result.Text)
                    && Recover(failed))
                    break;
                if (_current is { IsPending: true } pending)
                {
                    if (_interrupted) Finish(pending, error: null);
                    else if (result.IsError) Finish(pending, Explain(result.Text));
                    else Finish(pending, error: null);
                }
                _interrupted = false;
                IsBusy = false;
                if (Mood is OrbMood.Thinking or OrbMood.Acting or OrbMood.Speaking) Mood = OrbMood.Idle;
                if (_forgetAfterTurn)
                {
                    _forgetAfterTurn = false;
                    LetGo(keepLast: true);
                }
                break;
        }
    }

    private void OnToolActivity(ToolActivity a)
    {
        if (_current is not { IsPending: true } exchange) return;
        switch (a.Kind)
        {
            case ToolActivityKind.Started:
                Mood = OrbMood.Acting;
                if (a.Text.Length > 0) ShowStatus(new AgentStatus(a.Text, Acting: true));
                break;
            case ToolActivityKind.Finished when a.Mutated && a.Text.Length > 0:
                exchange.AddAction(a.Text);
                ActionCompleted?.Invoke();
                Mood = OrbMood.Thinking;
                ShowStatus(AgentStatus.Thinking);
                ContentGrew?.Invoke();
                break;
            case ToolActivityKind.Finished:
            case ToolActivityKind.Failed:
                Mood = OrbMood.Thinking;
                ShowStatus(AgentStatus.Thinking);
                break;
        }
    }

    /// <summary>Puts the status under the current request; appearing, it may need scrolling into view.</summary>
    private void ShowStatus(AgentStatus status)
    {
        if (_current is not { IsPending: true } exchange) return;
        var appeared = exchange.Status == null;
        exchange.Status = status;
        if (appeared) ContentGrew?.Invoke();
    }

    private void OnCrashed(string stderr)
    {
        _outstanding = 0;
        if (_current is { IsPending: true } failed && ClaudeAuth.IsFailure(stderr) && Recover(failed)) return;
        if (_current is { IsPending: true } exchange)
            Finish(exchange, Explain(stderr));
        IsBusy = false;
        if (!_current?.IsError ?? true) Mood = OrbMood.Idle;
    }

    // ── Sign-in ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The request failed on its sign-in. It tries the other sign-in by itself; when none
    /// works, the question waits for the user to sign in and is sent then, so a lapsed
    /// sign-in costs a click, not the question. Never for a request that already changed
    /// something: sending it again would do that twice.
    /// </summary>
    /// <returns>True when it's handled (retrying, or waiting for the sign-in).</returns>
    private bool Recover(Exchange exchange)
    {
        if (exchange.HasActions) return false;
        exchange.Answer = "";
        _replaceOnNextText = false;
        if (_service.RetryOnAnotherSignIn(exchange.Question, Recap()))
        {
            _outstanding++;
            IsBusy = true;
            Mood = OrbMood.Thinking;
            exchange.Status = AgentStatus.Thinking;
            return true;
        }
        exchange.AwaitsSignIn = true;
        AskToSignIn("Sign in and I'll answer this, then stay signed in for a year.");
        return true;
    }

    private void AskToSignIn(string text)
    {
        NeedsSignIn = true;
        RenewSoon = false;
        IsBusy = false;
        Mood = OrbMood.Idle;
        Caption = "My Claude sign-in ran out.";
        SignInText = text;
        ContentGrew?.Invoke();
    }

    private string RenewText => _account.Token is { } t
        ? $"My sign-in runs out on {t.ExpiresUtc.ToLocalTime():d MMM}. Renew it now and forget about it for a year."
        : "";

    private bool _signInFromPanel;

    [RelayCommand]
    private async System.Threading.Tasks.Task SignIn()
    {
        if (_account.IsSigningIn) return;
        _signInFromPanel = true;
        var outcome = await _account.SignInAsync();
        _signInFromPanel = false;
        if (outcome is SignInOutcome.Cancelled or SignInOutcome.Failed)
        {
            Caption = NeedsSignIn ? "My Claude sign-in ran out." : HasConversation ? "" : ReadyCaption;
            SignInText = outcome is SignInOutcome.Failed failed ? $"That sign-in didn't go through: {failed.Reason}. Try again?"
                : NeedsSignIn ? "Not signed in yet. One click when you're ready."
                : RenewText;
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _account.CancelSignIn();

    private void OnAccountChanged()
    {
        IsSigningIn = _account.IsSigningIn;
        if (IsSigningIn)
        {
            Caption = "Over to your browser…";
            SignInText = "Finish signing in on claude.com. I'll pick up from here.";
            Mood = OrbMood.Thinking;
        }
        else if (Mood == OrbMood.Thinking && !IsBusy)
        {
            Mood = OrbMood.Idle;
        }
    }

    /// <summary>Signed in, here or in Settings: whatever waited for it goes out now.</summary>
    private void OnSignedIn(SignInOutcome outcome)
    {
        NeedsSignIn = false;
        RenewSoon = false;
        Caption = outcome is SignInOutcome.GotToken { Token: var t } ? $"Signed in until {t.ExpiresUtc.ToLocalTime():d MMM yyyy}." : "Signed in.";
        Mood = OrbMood.Idle;
        ActionCompleted?.Invoke();

        if (_current is { IsPending: true, AwaitsSignIn: true } waiting)
        {
            waiting.AwaitsSignIn = false;
            waiting.Status = AgentStatus.Thinking;
            IsBusy = true;
            Mood = OrbMood.Thinking;
            _outstanding++;
            if (!_service.ResendAfterSignIn(waiting.Question, Recap()))
            {
                _outstanding--;
                Finish(waiting, "I can't find Claude Code any more. Install it from claude.com/code, then ask me again.");
            }
        }
        else
        {
            _service.End(); // the conversation warming up was started on the old sign-in
            _service.EnsureStarted();
        }
        // The browser has the focus, and the answer is on its way here.
        if (_signInFromPanel && App.Bootstrapper?.Resolve<YAPA.Avalonia.Services.AppNavigator>() is { IsAssistantShowing: false } navigator)
            navigator.ShowAssistant();
        ContentGrew?.Invoke();
    }

    private void Finish(Exchange exchange, string? error)
    {
        if (!exchange.IsPending) return;
        exchange.IsPending = false;
        exchange.Status = null;
        if (error != null)
        {
            exchange.IsError = true;
            exchange.Answer = exchange.Answer.Length > 0 ? exchange.Answer + "\n" + error : error;
            Mood = OrbMood.Error;
        }
        else
        {
            exchange.Answer = Tidy(exchange.Answer, final: true);
            if (exchange.Answer.Length == 0 && exchange.HasActions) exchange.Answer = "Done.";
            Mood = OrbMood.Idle;
        }

        if (_service.Journal.EndTurn()) exchange.CanUndo = true;
        if (error == null) Answered?.Invoke();
        RefreshUndo();
        Caption = error != null ? "Something went wrong." : "";
        IsBusy = false;
        ContentGrew?.Invoke();
    }

    /// <summary>Only the newest change can be undone from its own reply; older ones go back in order.</summary>
    private void RefreshUndo()
    {
        var latestUndoable = Exchanges.LastOrDefault(x => x.CanUndo);
        foreach (var x in Exchanges)
            if (x.CanUndo && !ReferenceEquals(x, latestUndoable)) x.CanUndo = false;
        if (latestUndoable != null && !_service.Journal.CanUndo) latestUndoable.CanUndo = false;
    }

    private string Explain(string? text)
    {
        var t = text ?? "";
        if (ClaudeAuth.IsFailure(t))
        {
            AskToSignIn("Sign in, then ask me again. What I'd already done is above.");
            return "I lost my Claude sign-in partway through.";
        }
        if (KnownFailure(t) is { } known) return known;
        if (_toolsUnreachable)
            return "I couldn't reach the timer from Claude Code. Close me and try again.";
        return t.Length is > 0 and < 160 ? $"That didn't work: {t.Trim()}" : "That didn't work. Try again.";
    }

    /// <summary>What a failure that isn't the user's doing means, in their words; null for anything else.</summary>
    internal static string? KnownFailure(string text)
    {
        if (text.Contains("ENOTFOUND", StringComparison.Ordinal) || text.Contains("ECONNREFUSED", StringComparison.Ordinal)
            || text.Contains("network", StringComparison.OrdinalIgnoreCase) || text.Contains("fetch failed", StringComparison.OrdinalIgnoreCase))
            return "I couldn't reach Claude. Check the connection and try again.";
        if (text.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            return "Claude is overloaded right now. Try again in a moment.";
        // Claude's own servers failed (a 5xx): nothing the user did, and usually gone on a retry.
        if (ServerError.IsMatch(text))
            return "Claude ran into a problem on its side. Try again in a moment.";
        return null;
    }

    private static readonly Regex ServerError = new(@"\b5\d\d\b|internal server error|api error", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Models drift into markdown now and then; the panel is plain text.
    private static readonly Regex Emphasis = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^#{1,6}\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Bullet = new(@"^\s*[-*]\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Code = new(@"`([^`]+)`", RegexOptions.Compiled);

    internal static string Tidy(string text, bool final)
    {
        var t = Emphasis.Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        t = Heading.Replace(t, "");
        t = Bullet.Replace(t, "• ");
        t = Code.Replace(t, "$1");
        return final ? t.Trim() : t.TrimStart();
    }

    // ── Context ──────────────────────────────────────────────────────────────

    private void OnEngineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPomodoroEngine.Phase) or nameof(IPomodoroEngine.PausedPhase)) UpdatePalette();
    }

    private void UpdatePalette()
    {
        var phase = _engine.Phase == PomodoroPhase.Pause ? _engine.PausedPhase : _engine.Phase;
        Palette = phase switch
        {
            PomodoroPhase.Work or PomodoroPhase.WorkEnded => OrbPalette.Focus,
            PomodoroPhase.Break or PomodoroPhase.BreakEnded => OrbPalette.Rest,
            _ => OrbPalette.Calm,
        };
    }

    /// <summary>A few things worth asking right now, so the panel never opens blank.</summary>
    private void UpdateSuggestions()
    {
        var phase = _engine.Phase;
        var today = new Suggestion("How's today going?", "How's today going?", true);
        var goal = new Suggestion("When will I hit my goal?", "When will I hit my goal?", true);
        var stopAt = new Suggestion("If I stop at 6…", "If I stop at 6 pm, how many will I have done?", false);
        IEnumerable<Suggestion> pick = phase switch
        {
            PomodoroPhase.Work => [new("5 more minutes", "Give me 5 more minutes", true), today, stopAt],
            PomodoroPhase.Break => [new("Longer break…", "Extend my break by 5 minutes", false), today, goal],
            PomodoroPhase.WorkEnded => [new("Start my break", "Start my break", true), today, goal],
            PomodoroPhase.BreakEnded => [new("Start the next one", "Start the next pomodoro", true), today, goal],
            PomodoroPhase.Pause => [new("Resume", "Resume", true), today, goal],
            _ => [new("Log time away…", "I worked for an hour away from the timer, log it", false), today,
                  new("When do I focus best?", "When do I focus best?", true)],
        };
        Suggestions.Clear();
        foreach (var s in pick) Suggestions.Add(s);
    }
}
