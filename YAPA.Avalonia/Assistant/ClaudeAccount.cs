using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Assistant;

/// <summary>A long-lived token from <c>claude setup-token</c>, which YAPA keeps for itself.</summary>
public sealed record ClaudeToken(string Value, DateTime CreatedUtc)
{
    /// <summary>What <c>setup-token</c> promises. If it lapses sooner, the next request asks for a sign-in.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    public DateTime ExpiresUtc => CreatedUtc + Lifetime;
}

public abstract record SignInOutcome
{
    /// <summary>YAPA has a token of its own now.</summary>
    public sealed record GotToken(ClaudeToken Token) : SignInOutcome;

    /// <summary>The account doesn't hand out long-lived tokens; Claude Code itself is signed in again.</summary>
    public sealed record SignedIn : SignInOutcome;

    public sealed record Failed(string Reason) : SignInOutcome;

    public sealed record Cancelled : SignInOutcome;
}

/// <summary>Reading what Claude Code says about sign-ins.</summary>
public static class ClaudeAuth
{
    /// <summary>Claude Code takes a long-lived token from here before its own sign-in.</summary>
    public const string TokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    private static readonly Regex Token = new(@"sk-ant-oat\d+-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled);

    // CSI and OSC sequences (colours, cursor moves, hyperlinks) and the odd two-byte escape.
    private static readonly Regex Escapes = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]", RegexOptions.Compiled);

    private static readonly string[] FailureMarks =
    [
        "Failed to authenticate",
        "could not be refreshed",
        "authentication_error",
        "authentication_failed",
        "Invalid API key",
        "invalid x-api-key",
        "Not logged in",
        "run /login",
        "OAuth token has expired",
        "OAuth token revoked",
    ];

    /// <summary>The request failed because of the sign-in, not the request: another sign-in may fix it.</summary>
    public static bool IsFailure(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var mark in FailureMarks)
            if (text.Contains(mark, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The token in what <c>setup-token</c> printed, once it's whole: something must follow it,
    /// since a read can end halfway through.
    /// </summary>
    public static string? FindToken(string terminalOutput)
    {
        var plain = Escapes.Replace(terminalOutput, "");
        var match = Token.Match(plain);
        return match.Success && match.Index + match.Length < plain.Length ? match.Value : null;
    }

    /// <summary>Whether <c>claude auth status --json</c> reports a sign-in; null when it can't tell.</summary>
    public static bool? ParseStatus(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject o && o["loggedIn"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The sign-in the assistant runs on. Claude Code's own sign-in expires on the account's
/// schedule (an organisation can end sessions after a day or two), and it then fails the
/// request in front of the user. So YAPA keeps a token of its own, made with
/// <c>claude setup-token</c> and good for a year, and signing in is a button in the panel
/// rather than a trip to a terminal.
/// </summary>
public sealed class ClaudeAccount
{
    /// <summary>The panel offers to renew this long before the token lapses.</summary>
    public static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(14);

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

    private readonly string _path;
    private CancellationTokenSource? _signIn;
    private bool _cancelledByUser; // otherwise a cancellation is the timeout

    public ClaudeAccount(string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.Combine(AppPaths.DataDirectory, "assistant"), "claude-token.json");
        Token = Load();
    }

    public ClaudeToken? Token { get; private set; }

    public bool IsSigningIn => _signIn != null;

    /// <summary>The token or the sign-in under way changed. Raised on the caller's thread.</summary>
    public event Action? Changed;

    /// <summary>A sign-in finished; requests can be sent again.</summary>
    public event Action<SignInOutcome>? SignedIn;

    public bool RenewalDue(DateTime utcNow) => Token != null && Token.ExpiresUtc - utcNow < RenewalWindow;

    /// <summary>
    /// Opens Claude's sign-in page in the browser and waits for the user to finish there.
    /// Tries for a long-lived token first; an account that doesn't allow those gets Claude
    /// Code's regular sign-in instead.
    /// </summary>
    public async Task<SignInOutcome> SignInAsync()
    {
        if (_signIn != null) return new SignInOutcome.Cancelled(); // one at a time; the first one carries on
        var executable = ClaudeCli.Locate();
        if (executable == null) return new SignInOutcome.Failed("Claude Code isn't installed");

        using var cancel = new CancellationTokenSource(SignInTimeout);
        _signIn = cancel;
        _cancelledByUser = false;
        Changed?.Invoke();
        SignInOutcome outcome;
        try
        {
            var run = await InTerminal(executable, "setup-token", stopWhen: ClaudeAuth.FindToken, cancel.Token);
            if (run.Found is { } value)
            {
                var token = new ClaudeToken(value, DateTime.UtcNow);
                Save(token);
                Token = token;
                outcome = new SignInOutcome.GotToken(token);
            }
            else if (cancel.IsCancellationRequested)
            {
                outcome = Cancelled();
            }
            else
            {
                Trace.WriteLine($"[assistant] setup-token ended without a token (exit {run.ExitCode}); trying the regular sign-in");
                var login = await InTerminal(executable, "auth login", stopWhen: null, cancel.Token);
                outcome = cancel.IsCancellationRequested ? Cancelled()
                    : login.ExitCode == 0 && await IsClaudeCodeSignedInAsync() != false ? new SignInOutcome.SignedIn()
                    : new SignInOutcome.Failed(LastLine(login.Output) ?? "the browser sign-in didn't complete");
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            outcome = new SignInOutcome.Failed(e.Message);
        }
        finally
        {
            _signIn = null;
        }

        Trace.WriteLine($"[assistant] sign-in: {outcome.GetType().Name}");
        Changed?.Invoke();
        if (outcome is SignInOutcome.GotToken or SignInOutcome.SignedIn) SignedIn?.Invoke(outcome);
        return outcome;
    }

    private SignInOutcome Cancelled() => _cancelledByUser ? new SignInOutcome.Cancelled() : new SignInOutcome.Failed("it timed out");

    public void CancelSignIn()
    {
        if (_signIn is not { } cancel) return;
        _cancelledByUser = true;
        cancel.Cancel();
    }

    /// <summary>Forgets YAPA's token; the assistant falls back to Claude Code's own sign-in.</summary>
    public void SignOut()
    {
        Token = null;
        try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    /// <summary>Whether Claude Code's own sign-in is there; null when it can't tell (not installed, no answer).</summary>
    public static async Task<bool?> IsClaudeCodeSignedInAsync()
    {
        var executable = ClaudeCli.Locate();
        if (executable == null) return null;
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "auth", "status", "--json" }) info.ArgumentList.Add(a);
        ClaudeCli.Sanitize(info.Environment);
        try
        {
            using var process = Process.Start(info)!;
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                return null;
            }
            return ClaudeAuth.ParseStatus(await output);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    private sealed record TerminalRun(int ExitCode, string Output, string? Found);

    /// <summary>
    /// Runs a Claude Code command that insists on a terminal, in a pseudo-terminal made by
    /// <c>script</c>. It opens the browser itself and learns the result through a callback on
    /// localhost, so nothing has to be typed into it. A wide terminal keeps the token on one line.
    /// </summary>
    private static async Task<TerminalRun> InTerminal(string executable, string arguments, Func<string, string?>? stopWhen, CancellationToken cancel)
    {
        var script = new[] { "/usr/bin/script", "/bin/script" }.FirstOrDefaultExecutable()
            ?? throw new InvalidOperationException("the 'script' command (util-linux) is missing");
        var info = new ProcessStartInfo(script)
        {
            RedirectStandardInput = true, // kept open: script ends when its input does
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-q", "-f", "-e", "-c", $"stty cols 1000 rows 50 2>/dev/null; exec {ShellQuote(executable)} {arguments}", "/dev/null" })
            info.ArgumentList.Add(a);
        ClaudeCli.Sanitize(info.Environment);
        // Signing in again must not be overridden by the token being replaced.
        info.Environment.Remove(ClaudeAuth.TokenVariable);
        info.Environment["SHELL"] = "/bin/sh";
        info.Environment["TERM"] = "xterm-256color";

        using var process = new Process { StartInfo = info };
        process.Start();
        // A cancelled read of a pipe doesn't reliably wake up; ending the process closes it.
        await using var stop = cancel.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None); // drained, so it can't block on a full pipe
        var output = new StringBuilder();
        string? found = null;
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await process.StandardOutput.ReadAsync(buffer, cancel);
                if (read == 0) break;
                output.Append(buffer, 0, read);
                // Its spinner redraws for as long as the browser takes; only the recent screen matters.
                if (output.Length > 32_768) output.Remove(0, output.Length - 16_384);
                if (stopWhen?.Invoke(output.ToString()) is { } hit)
                {
                    found = hit;
                    break; // it would wait for a key press to close; the token is all it was for
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        // Waited for, so it's reaped: a disposed Process that was never waited for stays a zombie.
        await process.WaitForExitAsync(CancellationToken.None);
        return new TerminalRun(found != null ? 0 : process.ExitCode, output.ToString(), found);
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string? LastLine(string terminalOutput)
    {
        var plain = Regex.Replace(terminalOutput, @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", "");
        var lines = plain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
            if (lines[i].Length is > 3 and < 160) return lines[i].TrimEnd('.');
        return null;
    }

    private ClaudeToken? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var o = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            var value = o?["token"]?.GetValue<string>();
            var created = o?["createdUtc"]?.GetValue<DateTime>();
            return value is { Length: > 0 } && created is { } c ? new ClaudeToken(value, DateTime.SpecifyKind(c, DateTimeKind.Utc)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Trace.WriteLine($"[assistant] couldn't read the saved sign-in: {e.Message}");
            return null;
        }
    }

    /// <summary>A secret, so readable by the user only, and replaced whole (never half-written).</summary>
    private void Save(ClaudeToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        using (var file = new FileStream(temporary, YAPA.Avalonia.Platform.OwnerOnlyFile.Create()))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(new JsonObject { ["token"] = token.Value, ["createdUtc"] = token.CreatedUtc }.ToJsonString());
        }
        File.Move(temporary, _path, overwrite: true);
    }
}

internal static class PathProbe
{
    public static string? FirstOrDefaultExecutable(this IEnumerable<string> candidates)
    {
        foreach (var c in candidates)
            if (ExecutableResolver.IsExecutableFile(c)) return c;
        return null;
    }
}
