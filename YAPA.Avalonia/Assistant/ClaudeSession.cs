using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace YAPA.Avalonia.Assistant;

public abstract record ClaudeEvent;

/// <summary>Claude Code is up; <paramref name="ToolsConnected"/> is false when it couldn't reach YAPA.</summary>
public sealed record ClaudeReady(string Model, bool ToolsConnected) : ClaudeEvent;

/// <summary>A new reply message began. Text shown so far belonged to an earlier step of the turn.</summary>
public sealed record ClaudeMessageStarted : ClaudeEvent;

public sealed record ClaudeTextDelta(string Text) : ClaudeEvent;

public sealed record ClaudeThinking : ClaudeEvent;

/// <summary>The model began a tool call (it runs in YAPA's own server, which reports it in detail).</summary>
public sealed record ClaudeToolCall(string Name) : ClaudeEvent;

/// <summary>A reply message is complete; <paramref name="StopReason"/> "end_turn" means the turn's answer is in.</summary>
public sealed record ClaudeMessageEnded(string? StopReason) : ClaudeEvent;

/// <summary>The turn is over. Arrives a moment after the answer (Claude Code summarises the turn first).</summary>
public sealed record ClaudeTurnResult(bool IsError, string? Subtype, string? Text) : ClaudeEvent;

/// <param name="ResetsAt">When the limit lifts, if known.</param>
public sealed record ClaudeRateLimited(DateTimeOffset? ResetsAt) : ClaudeEvent;

/// <summary>Reads Claude Code's <c>stream-json</c> output, one JSON object per line.</summary>
public static class ClaudeStream
{
    public static IEnumerable<ClaudeEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) yield break;

        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { yield break; }
        if (node is not JsonObject message) yield break;

        switch (Str(message, "type"))
        {
            case "system" when Str(message, "subtype") == "init":
                var connected = message["mcp_servers"] is JsonArray servers && servers.OfType<JsonObject>()
                    .Any(s => Str(s, "name") == McpServer.ServerName && Str(s, "status") == "connected");
                yield return new ClaudeReady(Str(message, "model") ?? "", connected);
                break;

            case "stream_event" when message["event"] is JsonObject ev:
                switch (Str(ev, "type"))
                {
                    case "message_start":
                        yield return new ClaudeMessageStarted();
                        break;
                    case "content_block_start" when ev["content_block"] is JsonObject block:
                        if (Str(block, "type") == "thinking") yield return new ClaudeThinking();
                        else if (Str(block, "type") == "tool_use") yield return new ClaudeToolCall(Str(block, "name") ?? "");
                        break;
                    case "content_block_delta" when ev["delta"] is JsonObject delta && Str(delta, "type") == "text_delta":
                        if (Str(delta, "text") is { Length: > 0 } text) yield return new ClaudeTextDelta(text);
                        break;
                    case "message_delta" when ev["delta"] is JsonObject delta:
                        yield return new ClaudeMessageEnded(Str(delta, "stop_reason"));
                        break;
                }
                break;

            case "result":
                yield return new ClaudeTurnResult(
                    message["is_error"]?.GetValue<bool>() == true || Str(message, "subtype") is { } sub && sub != "success",
                    Str(message, "subtype"),
                    Str(message, "result") ?? (message["errors"] as JsonArray)?.OfType<JsonNode>().Select(e => e.ToString()).FirstOrDefault());
                break;

            case "rate_limit_event" when message["rate_limit_info"] is JsonObject info && Str(info, "status") == "rejected":
                DateTimeOffset? resets = info["resetsAt"] is JsonValue v && v.TryGetValue<long>(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : null;
                yield return new ClaudeRateLimited(resets);
                break;
        }
    }

    private static string? Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A user message, as stream-json input takes it.</summary>
    public static string UserMessage(string text) => new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    }.ToJsonString();

    /// <summary>Stops the turn under way; the conversation carries on.</summary>
    public static string Interrupt(string requestId) => new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = new JsonObject { ["subtype"] = "interrupt" },
    }.ToJsonString();
}

/// <summary>
/// One running <c>claude</c> process holding one conversation. Messages go in on stdin and
/// events come back from stdout; both are JSON lines. Events are raised on a background
/// thread.
/// </summary>
public sealed class ClaudeSession : IDisposable
{
    private const int StderrLinesKept = 20;

    private readonly Process _process;
    private readonly object _stdinGate = new();
    private readonly Queue<string> _stderr = new();
    private int _interrupts;
    private int _disposed;

    private ClaudeSession(Process process) => _process = process;

    public event Action<ClaudeEvent>? EventReceived;

    /// <summary>The process ended (crashed, or was killed); the text is the tail of its stderr.</summary>
    public event Action<string>? Exited;

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public static ClaudeSession Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // Its terminal UI must never appear, and it shouldn't think a human is at a TTY.
        info.Environment["CI"] = "1";
        info.Environment["NO_COLOR"] = "1";
        // Set when YAPA itself was started from a Claude Code shell; this is not a nested session.
        info.Environment.Remove("CLAUDECODE");
        info.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var session = new ClaudeSession(process);
        process.Start();
        process.StandardInput.AutoFlush = true;

        new Thread(session.ReadStdout) { IsBackground = true, Name = "claude stdout" }.Start();
        new Thread(session.ReadStderr) { IsBackground = true, Name = "claude stderr" }.Start();
        return session;
    }

    public void Send(string text) => WriteLine(ClaudeStream.UserMessage(text));

    public void Interrupt() => WriteLine(ClaudeStream.Interrupt($"interrupt-{Interlocked.Increment(ref _interrupts)}"));

    private void WriteLine(string json)
    {
        lock (_stdinGate)
        {
            if (_disposed != 0 || HasExited) return;
            try { _process.StandardInput.WriteLine(json); }
            catch (IOException) { } // it just died; Exited reports it
            catch (ObjectDisposedException) { }
        }
    }

    private void ReadStdout()
    {
        try
        {
            var stdout = _process.StandardOutput;
            while (stdout.ReadLine() is { } line)
            {
                foreach (var ev in ClaudeStream.Parse(line))
                {
                    try { EventReceived?.Invoke(ev); }
                    catch (Exception e) { Trace.WriteLine($"[assistant] event handler failed: {e}"); }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        if (_disposed == 0)
        {
            string tail;
            lock (_stderr) tail = string.Join("\n", _stderr);
            Exited?.Invoke(tail);
        }
    }

    private void ReadStderr()
    {
        try
        {
            while (_process.StandardError.ReadLine() is { } line)
            {
                lock (_stderr)
                {
                    _stderr.Enqueue(line);
                    while (_stderr.Count > StderrLinesKept) _stderr.Dequeue();
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Off the caller's (UI) thread: killing and reaping a Node process can take a moment,
        // and a Process disposed before it has been waited for stays a zombie.
        var process = _process;
        Task.Run(() =>
        {
            try
            {
                lock (_stdinGate)
                {
                    try { process.StandardInput.Close(); } catch (IOException) { }
                }
                // Closing stdin ends the conversation cleanly; give it a moment before forcing it.
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException) { } // never started, or already reaped
            catch (Exception e)
            {
                Trace.WriteLine($"[assistant] stopping claude failed: {e.Message}");
            }
            finally
            {
                process.Dispose();
            }
        });
    }
}
