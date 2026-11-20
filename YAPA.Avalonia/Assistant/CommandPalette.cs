using System;
using System.Collections.Generic;
using System.Linq;

namespace YAPA.Avalonia.Assistant;

/// <summary>Something the command bar can do: an action, a question to ask, or asking YAPA what was typed.</summary>
/// <param name="Keywords">Other words it answers to ("break" for Skip the break, "stats" for Insights).</param>
/// <param name="Icon">A 24-unit line icon's path data.</param>
/// <param name="Group">The heading it sits under when nothing is typed.</param>
/// <param name="Keys">Its shortcut, drawn as keycaps ("Ctrl D", "G T").</param>
/// <param name="Question">A suggested question: shown before anything is typed, never matched.</param>
/// <param name="Ask">Asks YAPA what was typed.</param>
/// <param name="Stays">The panel stays open after it runs (a new conversation, a question).</param>
public sealed record PaletteCommand(
    string Title,
    string Keywords,
    string Icon,
    Action Run,
    string Group = "",
    string Keys = "",
    bool Question = false,
    bool Ask = false,
    bool Stays = false);

/// <summary>
/// The command bar's list: what matches what's been typed, best first, with asking YAPA always
/// on offer. A request that reads like a sentence ("log the hour I spent at the library") puts
/// asking first; a word or two that names an action ("pause", "week") puts the action first,
/// so Enter does the obvious thing either way.
/// </summary>
public static class CommandPalette
{
    public const int MostShown = 8;

    /// <param name="commands">Everything on offer right now, in the order to show when nothing is typed.</param>
    /// <param name="ask">Makes the "Ask YAPA" entry for the text; null where asking isn't on offer.</param>
    public static IReadOnlyList<PaletteCommand> Rank(string text, IReadOnlyList<PaletteCommand> commands, Func<string, PaletteCommand>? ask)
    {
        var query = text.Trim();
        if (query.Length == 0) return commands.Take(MostShown).ToList();

        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = commands
            .Where(c => !c.Question)
            .Select((c, i) => (Command: c, Score: Score(c, words), Order: i))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Order)
            .Select(m => m.Command)
            .Take(MostShown - 1)
            .ToList();
        if (ask == null) return matches;

        var asking = ask(query);
        // A sentence, a question, or nothing to run: asking is what's meant.
        var sentence = words.Length >= 3 || query.Contains('?') || matches.Count == 0;
        return sentence ? [asking, .. matches] : [.. matches, asking];
    }

    /// <summary>
    /// How well a command answers to the typed words: each word must start a word of its title or
    /// keywords. A match on the title's first word counts most, so "st" finds Start before Insights.
    /// </summary>
    internal static int Score(PaletteCommand command, IReadOnlyList<string> words)
    {
        var title = Words(command.Title);
        var keywords = Words(command.Keywords);
        var score = 0;
        foreach (var w in words)
        {
            if (title.Length > 0 && title[0].StartsWith(w, StringComparison.Ordinal)) score += 4;
            else if (title.Any(t => t.StartsWith(w, StringComparison.Ordinal))) score += 3;
            else if (keywords.Any(k => k.StartsWith(w, StringComparison.Ordinal))) score += 2;
            else return 0;
        }
        return score;
    }

    private static string[] Words(string text)
        => text.ToLowerInvariant().Split([' ', '-', '…', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>A command as the bar shows it: with its group's heading when it starts one, and whether it's the one Enter runs.</summary>
public sealed partial class PaletteRow(PaletteCommand command, string? header) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public PaletteCommand Command { get; } = command;

    /// <summary>The group's heading, on the first row of each group; null otherwise.</summary>
    public string? Header { get; } = header;

    public bool HasHeader => Header != null;

    public string Title => Command.Title;

    public global::Avalonia.Media.Geometry Icon { get; } = global::Avalonia.Media.Geometry.Parse(command.Icon);

    /// <summary>The shortcut's keys, one keycap each.</summary>
    public string[] Keys { get; } = command.Keys.Length == 0 ? [] : command.Keys.Split(' ');

    public bool HasKeys => Keys.Length > 0;

    public bool IsAsk => Command.Ask;

    public bool IsQuestion => Command.Question;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isSelected;
}
