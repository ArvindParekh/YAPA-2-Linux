using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// The conversation behind the assistant panel: a Claude Code process holding it, and the
/// tool server it calls back into. Both start when the panel first needs them (they are
/// warming up while the user types) and stop a few minutes after the panel goes away, so
/// a Node process isn't kept around all day for a timer.
///
/// Everything here runs on the UI thread; events from the process are marshalled to it.
/// </summary>
public sealed class AssistantService : IDisposable
{
    /// <summary>How long a hidden panel keeps its conversation, to carry on where it left off.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromMinutes(5);

    private readonly AssistantSettings _settings;
    private readonly AppTools _app;
    private readonly AssistantToolbox _toolbox;
    private readonly DispatcherTimer _idle;
    private McpServer? _server;
    private ClaudeSession? _session;
    private string? _configPath;

    public AssistantService(AssistantSettings settings, AppTools app, ChangeJournal journal)
    {
        _settings = settings;
        _app = app;
        _toolbox = new AssistantToolbox(app.All(), journal, f => Dispatcher.UIThread.InvokeAsync(f).GetTask());
        _toolbox.Activity += a => Dispatcher.UIThread.Post(() => ToolActivity?.Invoke(a));

        _idle = new DispatcherTimer { Interval = KeepAlive };
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (!IsBusy) End();
        };
        // A different model needs a new process; the next request starts one.
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistantSettings.Model) && !IsBusy) End();
        };
    }

    public ChangeJournal Journal => _toolbox.Journal;

    public AppTools App => _app;

    internal AssistantToolbox Toolbox => _toolbox;

    /// <summary>Raised on the UI thread for everything the conversation streams back.</summary>
    public event Action<ClaudeEvent>? Event;

    public event Action<ToolActivity>? ToolActivity;

    /// <summary>The process died on its own; the text is what it last wrote to stderr.</summary>
    public event Action<string>? Crashed;

    private int _outstanding;

    /// <summary>A request is under way (sent, and its result not in yet).</summary>
    public bool IsBusy => _outstanding > 0;

    public bool IsRunning => _session is { HasExited: false };

    public static bool IsInstalled => ClaudeCli.Locate() != null;

    /// <summary>Starts Claude Code unless it is already running.</summary>
    /// <returns>False when Claude Code isn't installed.</returns>
    public bool EnsureStarted()
    {
        _idle.Stop();
        if (IsRunning) return true;
        End();

        var executable = ClaudeCli.Locate();
        if (executable == null) return false;

        // An empty directory of our own: Claude Code looks around its working directory
        // (CLAUDE.md files, project settings), and there must be nothing to find.
        var directory = Path.Combine(AppPaths.CacheDirectory, "assistant");
        Directory.CreateDirectory(directory);

        _server = McpServer.Start(_toolbox);
        // The token is a secret: readable by the user only, and gone when the conversation is.
        _configPath = Path.Combine(directory, $"mcp-{Environment.ProcessId}.json");
        using (var file = new FileStream(_configPath, new FileStreamOptions
               {
                   Mode = FileMode.Create,
                   Access = FileAccess.Write,
                   UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
               }))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(_server.ClientConfig());
        }

        var session = ClaudeSession.Start(executable,
            ClaudeCli.Arguments(_settings.Model, AssistantPrompt.System(ClockFormat.Is24Hour), _configPath),
            directory);
        session.EventReceived += e => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(session, _session)) return; // from a conversation already ended
            if (e is ClaudeTurnResult) _outstanding = Math.Max(0, _outstanding - 1);
            Event?.Invoke(e);
        });
        session.Exited += tail => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(session, _session)) return;
            Trace.WriteLine($"[assistant] claude exited: {tail}");
            End();
            Crashed?.Invoke(tail);
        });
        _session = session;
        Trace.WriteLine($"[assistant] started claude ({_settings.Model}), tools on port {_server.Port}");
        return true;
    }

    /// <summary>Sends the user's words, with the live state attached, as a new request.</summary>
    private readonly System.Collections.Generic.List<string> _notes = [];

    /// <summary>Something the model should hear with the next request (it can't see the panel).</summary>
    public void Note(string text) => _notes.Add(text);

    /// <returns>False when Claude Code can't be started (no longer installed).</returns>
    public bool Ask(string text)
    {
        if (!EnsureStarted()) return false;
        Journal.BeginTurn();
        _outstanding++;
        _session!.Send(AssistantPrompt.Message(text, _app.State(), _notes.ToArray()));
        _notes.Clear();
        return true;
    }

    /// <summary>Stops the request under way; the conversation stays.</summary>
    public void Interrupt()
    {
        if (IsBusy) _session?.Interrupt();
    }

    /// <summary>The panel went away: keep the conversation a while in case it comes back.</summary>
    public void PanelHidden()
    {
        if (IsRunning) _idle.Start();
    }

    /// <summary>Ends the conversation: the next request starts a new one.</summary>
    public void End()
    {
        _idle.Stop();
        var session = _session;
        _session = null;
        session?.Dispose();
        _server?.Dispose();
        _server = null;
        if (_configPath != null)
        {
            try { File.Delete(_configPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _configPath = null;
        }
        _outstanding = 0;
        _notes.Clear();
    }

    public void Dispose() => End();
}
