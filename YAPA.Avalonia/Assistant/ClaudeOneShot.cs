using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Assistant;

/// <summary>What a one-off request came back with: the structured answer, or why there is none.</summary>
/// <param name="SignedOut">It failed on the sign-in: signing in again is the fix.</param>
public sealed record OneShotResult(JsonNode? Answer, string? Error, bool SignedOut = false);

/// <summary>
/// A single request to Claude with a structured answer and nothing else: no tools, no MCP
/// servers, none of the user's settings, no transcript kept. For work the app does on its
/// own (writing up the journal), apart from the conversation in the panel.
/// </summary>
public static class ClaudeOneShot
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    public static async Task<OneShotResult> AskAsync(string system, string prompt, string schema, string model, ClaudeAccount account,
        CancellationToken cancel = default)
    {
        var executable = ClaudeCli.Locate();
        if (executable == null) return new OneShotResult(null, "Claude Code isn't installed");

        // YAPA's own sign-in first; Claude Code's when that's missing or refused.
        var tokens = account.Token is { } t ? new[] { t.Value, null } : new string?[] { null };
        OneShotResult last = new(null, "no answer");
        foreach (var token in tokens)
        {
            last = await RunAsync(executable, system, prompt, schema, model, token, cancel);
            if (!last.SignedOut) return last;
        }
        return last;
    }

    private static async Task<OneShotResult> RunAsync(string executable, string system, string prompt, string schema, string model, string? token,
        CancellationToken cancel)
    {
        // An empty folder of its own, so Claude Code finds no CLAUDE.md or project settings.
        var directory = Path.Combine(AppPaths.CacheDirectory, "assistant");
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[]
                 {
                     "--print", "--output-format", "json", "--model", model, "--system-prompt", system, "--json-schema", schema,
                     "--tools", "", "--strict-mcp-config", "--mcp-config", """{"mcpServers":{}}""", "--setting-sources", "",
                     "--disable-slash-commands", "--no-session-persistence", "--permission-prompts", "none",
                 })
            info.ArgumentList.Add(a);
        ClaudeCli.Sanitize(info.Environment);
        if (token != null) info.Environment[ClaudeAuth.TokenVariable] = token;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Timeout);
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                return new OneShotResult(null, cancel.IsCancellationRequested ? "cancelled" : "Claude took too long");
            }

            var text = await output;
            var stderr = await errors;
            JsonNode? result = null;
            try { result = JsonNode.Parse(text); } catch (JsonException) { }
            var message = result?["result"]?.GetValue<string>() ?? stderr;
            if (result?["is_error"]?.GetValue<bool>() != false || result?["structured_output"] is not JsonObject answer)
            {
                Trace.WriteLine($"[claude] one-shot failed: {Clip(message)}");
                return new OneShotResult(null, Clip(message) is { Length: > 0 } m ? m : "Claude didn't answer", ClaudeAuth.IsFailure(message));
            }
            return new OneShotResult(answer, null);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new OneShotResult(null, e.Message);
        }
    }

    private static string Clip(string s)
    {
        var t = s.Trim();
        return t.Length > 200 ? t[..200] + "…" : t;
    }
}
