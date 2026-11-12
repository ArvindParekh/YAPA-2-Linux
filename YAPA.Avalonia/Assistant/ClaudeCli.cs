using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YAPA.Avalonia.Audio;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// The Claude Code command line, which the assistant runs on the user's own sign-in
/// (a Claude subscription), so the app needs no API key of its own.
/// </summary>
public static class ClaudeCli
{
    /// <summary>Points at a specific executable; the tests use it to run a fake one.</summary>
    public const string OverrideVariable = "YAPA_CLAUDE";

    /// <summary>
    /// Absolute path of <c>claude</c>, or null. Looked up on every call, not cached: it is
    /// cheap next to starting it, and installing Claude Code shouldn't need a restart of YAPA.
    /// </summary>
    public static string? Locate()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return ExecutableResolver.IsExecutableFile(overridden) ? overridden : null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // The app grid gives GNOME apps a minimal PATH, so the usual install places are added.
        var directories = ExecutableResolver.BuildSearchPath(Environment.GetEnvironmentVariable("PATH"), home)
            .Concat(
            [
                Path.Combine(home, ".claude", "local"), // the "migrate-installer" location
                Path.Combine(home, ".npm-global", "bin"),
                Path.Combine(home, ".bun", "bin"),
                Path.Combine(home, ".volta", "bin"),
            ]);
        return directories
            .Select(d => Path.Combine(d, "claude"))
            .FirstOrDefault(ExecutableResolver.IsExecutableFile);
    }

    /// <summary>
    /// Arguments for one long-lived conversation: messages in and events out as JSON lines,
    /// with nothing of Claude Code's own (no built-in tools, none of the user's MCP servers,
    /// plugins, hooks, skills or CLAUDE.md files). It can only reach YAPA, through
    /// <paramref name="mcpConfigPath"/>.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string model, string systemPrompt, string mcpConfigPath) =>
    [
        "--print",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose", // stream-json output requires it
        "--include-partial-messages",
        "--model", model,
        "--system-prompt", systemPrompt,
        "--tools", "",
        "--strict-mcp-config",
        "--mcp-config", mcpConfigPath,
        "--allowedTools", "mcp__" + McpServer.ServerName,
        // Nobody can answer a permission prompt here; anything not allowed above is refused.
        "--permission-prompts", "none",
        "--setting-sources", "",
        "--disable-slash-commands",
        "--no-session-persistence",
    ];
}
