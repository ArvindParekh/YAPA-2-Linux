using System;
using System.Collections.Generic;
using System.Linq;

namespace YAPA.Avalonia.SingleInstance;

public enum AppCommand
{
    Show,
    Hide,
    ToggleVisibility,
    Start,
    Pause,
    Toggle,
    Stop,
    Skip,
    LongBreak,
    ToggleAmbience,
    Reset,
    MarkDone,
    Settings,
    Statistics,
    Status,
    StatusJson,
    Quit,
}

public sealed record ParsedCommandLine(IReadOnlyList<AppCommand> Commands, IReadOnlyList<string> Unknown, bool Help, bool Version)
{
    /// <summary>
    /// Commands that only make sense against a running timer. Launching the app
    /// just to answer them would be wrong: a status-bar script polling
    /// <c>--status</c> must not start YAPA.
    /// </summary>
    public bool IsQueryOnly => Commands.Count > 0 && Commands.All(c => c is AppCommand.Status or AppCommand.StatusJson or AppCommand.Quit);
}

public static class CommandLine
{
    private static readonly Dictionary<string, AppCommand> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["show"] = AppCommand.Show,
        ["hide"] = AppCommand.Hide,
        ["toggle-window"] = AppCommand.ToggleVisibility,
        ["start"] = AppCommand.Start,
        ["resume"] = AppCommand.Start,
        ["pause"] = AppCommand.Pause,
        ["toggle"] = AppCommand.Toggle,
        ["stop"] = AppCommand.Stop,
        ["skip"] = AppCommand.Skip,
        ["long-break"] = AppCommand.LongBreak,
        ["ambience"] = AppCommand.ToggleAmbience,
        ["reset"] = AppCommand.Reset,
        ["mark-done"] = AppCommand.MarkDone,
        ["settings"] = AppCommand.Settings,
        ["stats"] = AppCommand.Statistics,
        ["statistics"] = AppCommand.Statistics,
        ["status"] = AppCommand.Status,
        ["quit"] = AppCommand.Quit,
        ["exit"] = AppCommand.Quit,
    };

    public const string Usage = """
        Usage: yapa2 [command] [--json]

        With no command, starts YAPA 2 or brings the running timer to the front.

        Timer:     --start  --pause  --toggle  --stop  --skip  --long-break  --reset
                   --mark-done         count a pomodoro and its break done off the timer
        Sound:     --ambience          switch the rain / ticking on or off
        Windows:   --show  --hide  --toggle-window  --settings  --stats
        Scripting: --status [--json]   print the running timer's state
                   --quit              quit the running instance

        Bind `yapa2 --toggle` to a keyboard shortcut (GNOME Settings → Keyboard →
        Custom Shortcuts) to start and pause from anywhere.
        The original /start style also works.
        """;

    public static ParsedCommandLine Parse(IEnumerable<string> args)
    {
        var commands = new List<AppCommand>();
        var unknown = new List<string>();
        bool help = false, version = false, json = false;

        foreach (var raw in args)
        {
            var arg = raw.TrimStart('-', '/');
            if (arg.Length == 0) continue;

            switch (arg.ToLowerInvariant())
            {
                case "h" or "help" or "?":
                    help = true;
                    continue;
                case "v" or "version":
                    version = true;
                    continue;
                case "json":
                    json = true;
                    continue;
            }

            if (Map.TryGetValue(arg, out var command)) commands.Add(command);
            else unknown.Add(raw);
        }

        if (json)
        {
            var i = commands.IndexOf(AppCommand.Status);
            if (i >= 0) commands[i] = AppCommand.StatusJson;
            else commands.Add(AppCommand.StatusJson);
        }

        return new ParsedCommandLine(commands, unknown, help, version);
    }
}
