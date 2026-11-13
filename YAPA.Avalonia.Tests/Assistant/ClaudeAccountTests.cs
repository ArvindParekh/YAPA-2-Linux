using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Assistant;

namespace YAPA.Avalonia.Tests.Assistant;

[TestClass]
public class ClaudeAuthTests
{
    [TestMethod]
    public void Sign_in_failures_are_told_apart_from_other_errors()
    {
        // What the panel showed when the sign-in lapsed, and its relatives.
        Assert.IsTrue(ClaudeAuth.IsFailure("Failed to authenticate: OAuth session expired and could not be refreshed"));
        Assert.IsTrue(ClaudeAuth.IsFailure("OAuth access token could not be refreshed: another Claude Code process is holding the refresh lock"));
        Assert.IsTrue(ClaudeAuth.IsFailure("Not logged in · Please run /login"));
        Assert.IsTrue(ClaudeAuth.IsFailure("API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"Invalid bearer token\"}}"));

        Assert.IsFalse(ClaudeAuth.IsFailure("API Error: 529 Overloaded"));
        Assert.IsFalse(ClaudeAuth.IsFailure("fetch failed"));
        Assert.IsFalse(ClaudeAuth.IsFailure(null));
    }

    [TestMethod]
    public void The_token_is_read_from_the_terminal_screen_once_it_is_whole()
    {
        const string token = "sk-ant-oat01-AbC_dEf-123456789012345678901234567890xyzAA";
        var screen = "\x1b[?25l\x1b[2K\x1b[1G✓ Long-lived authentication token created successfully!\r\n\r\n"
                     + "Your OAuth token (valid for 1 year):\r\n\r\n\x1b[33m" + token + "\x1b[39m\r\n\r\n"
                     + "Store this token securely. You won't be able to see it again.\r\n";
        Assert.AreEqual(token, ClaudeAuth.FindToken(screen));

        // A read can end in the middle of it: not yet.
        Assert.IsNull(ClaudeAuth.FindToken("Your OAuth token (valid for 1 year):\r\n\r\n" + token[..30]));
        Assert.IsNull(ClaudeAuth.FindToken("Your OAuth token (valid for 1 year):\r\n\r\n" + token));
        // The usage line names the variable, not a token.
        Assert.IsNull(ClaudeAuth.FindToken("Use this token by setting: export CLAUDE_CODE_OAUTH_TOKEN=<token>\n"));
    }

    [TestMethod]
    public void Auth_status_is_read_from_its_json()
    {
        Assert.AreEqual(false, ClaudeAuth.ParseStatus("{\"loggedIn\": false, \"authMethod\": \"none\"}"));
        Assert.AreEqual(true, ClaudeAuth.ParseStatus("{\"loggedIn\": true, \"authMethod\": \"claude.ai\"}"));
        Assert.IsNull(ClaudeAuth.ParseStatus("Not JSON"));
    }

    [TestMethod]
    public void A_sign_in_inherited_from_a_claude_code_session_is_not_borrowed()
    {
        // YAPA restarted by an install script run in a Claude Code session.
        var inherited = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "claude-desktop",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "the session's",
            ["CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH"] = "1",
            ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:1234",
            ["CLAUDE_DESKTOP_STARTED_PATH"] = "/x",
        };
        ClaudeCli.Sanitize(inherited);
        CollectionAssert.AreEquivalent(new[] { "PATH", "CI", "NO_COLOR" }, new List<string>(inherited.Keys));

        // Set by the user for themselves: left alone.
        var own = new Dictionary<string, string?> { ["ANTHROPIC_BASE_URL"] = "https://gateway.example", ["CLAUDE_CODE_OAUTH_TOKEN"] = "theirs" };
        ClaudeCli.Sanitize(own);
        Assert.AreEqual("https://gateway.example", own["ANTHROPIC_BASE_URL"]);
        Assert.AreEqual("theirs", own["CLAUDE_CODE_OAUTH_TOKEN"]);
    }
}

/// <summary>Signing in through a stand-in for the claude command line, in a real pseudo-terminal.</summary>
[TestClass]
public class ClaudeAccountTests
{
    private const string Token = "sk-ant-oat01-FAKEfakeFAKEfakeFAKEfake0123456789-abcdefAA";
    private string _dir = "";
    private string? _savedOverride;

    [TestInitialize]
    public void Setup()
    {
        if (!File.Exists("/usr/bin/script") && !File.Exists("/bin/script")) Assert.Inconclusive("needs script(1) from util-linux");
        _dir = Path.Combine(Path.GetTempPath(), "yapa-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _savedOverride = Environment.GetEnvironmentVariable(ClaudeCli.OverrideVariable);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(ClaudeCli.OverrideVariable, _savedOverride);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A fake claude: each case is a shell snippet for one subcommand.</summary>
    private void FakeClaude(string setupToken, string authLogin = "exit 1", string authStatus = "echo '{\"loggedIn\": false}'")
    {
        var path = Path.Combine(_dir, "claude");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            [ -t 1 ] || { echo "not a terminal" >&2; exit 3; }
            case "$1 $2" in
              "setup-token ") {{setupToken}} ;;
              "auth login") {{authLogin}} ;;
              "auth status") {{authStatus}} ;;
            esac
            """.Replace("\r", ""));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable(ClaudeCli.OverrideVariable, path);
    }

    [TestMethod]
    public async Task A_long_lived_token_is_captured_kept_privately_and_remembered()
    {
        // It prints the token, then waits for a key press that never comes.
        FakeClaude($"printf 'Opening browser to sign in…\\r\\n'; sleep 0.3; printf 'Your OAuth token (valid for 1 year):\\r\\n\\r\\n{Token}\\r\\n\\r\\nPress Enter to continue'; sleep 60");
        var account = new ClaudeAccount(_dir);
        var changes = 0;
        account.Changed += () => changes++;

        var started = DateTime.UtcNow;
        var outcome = await account.SignInAsync();

        Assert.IsInstanceOfType<SignInOutcome.GotToken>(outcome);
        Assert.AreEqual(Token, account.Token!.Value);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "it doesn't wait for the key press");
        Assert.IsFalse(account.IsSigningIn);
        Assert.AreEqual(2, changes, "signing in, then signed in");

        var file = Path.Combine(_dir, "claude-token.json");
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        var reloaded = new ClaudeAccount(_dir);
        Assert.AreEqual(Token, reloaded.Token!.Value);
        Assert.IsFalse(reloaded.RenewalDue(DateTime.UtcNow));
        Assert.IsTrue(reloaded.RenewalDue(DateTime.UtcNow + ClaudeToken.Lifetime - TimeSpan.FromDays(3)));

        reloaded.SignOut();
        Assert.IsNull(reloaded.Token);
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public async Task An_account_without_long_lived_tokens_gets_the_regular_sign_in()
    {
        FakeClaude(
            setupToken: "echo 'Long-lived tokens are not available for your organization'; exit 1",
            authLogin: "echo 'Login successful.'; exit 0",
            authStatus: "echo '{\"loggedIn\": true}'");
        var account = new ClaudeAccount(_dir);

        var outcome = await account.SignInAsync();

        Assert.IsInstanceOfType<SignInOutcome.SignedIn>(outcome);
        Assert.IsNull(account.Token);
    }

    [TestMethod]
    public async Task A_sign_in_that_fails_says_why()
    {
        FakeClaude(setupToken: "exit 1", authLogin: "echo 'Sign-in was denied in the browser.'; exit 1");
        var outcome = await new ClaudeAccount(_dir).SignInAsync();

        Assert.IsInstanceOfType<SignInOutcome.Failed>(outcome);
        Assert.AreEqual("Sign-in was denied in the browser", ((SignInOutcome.Failed)outcome).Reason);
    }

    [TestMethod]
    public async Task Cancelling_stops_the_wait_for_the_browser()
    {
        FakeClaude(setupToken: "echo 'Opening browser to sign in…'; sleep 60");
        var account = new ClaudeAccount(_dir);
        var signIn = account.SignInAsync();
        await Task.Delay(500);
        Assert.IsTrue(account.IsSigningIn);

        account.CancelSignIn();
        var finished = await Task.WhenAny(signIn, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.AreSame(signIn, finished, "cancelling ends it promptly");
        Assert.IsInstanceOfType<SignInOutcome.Cancelled>(await signIn);
        Assert.IsNull(account.Token);
    }

    [TestMethod]
    public async Task Claude_codes_own_sign_in_is_asked_about_without_a_terminal()
    {
        var path = Path.Combine(_dir, "claude");
        File.WriteAllText(path, "#!/bin/sh\n[ \"$1 $2 $3\" = 'auth status --json' ] && echo '{\"loggedIn\": false, \"authMethod\": \"none\"}'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable(ClaudeCli.OverrideVariable, path);

        Assert.AreEqual(false, await ClaudeAccount.IsClaudeCodeSignedInAsync());
    }
}
