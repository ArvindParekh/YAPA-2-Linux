using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Assistant;

namespace YAPA.Avalonia.Tests.Assistant;

[TestClass]
public class CommandPaletteTests
{
    private static readonly List<PaletteCommand> Commands =
    [
        new("Pause", "pause hold wait", "", () => { }, "Timer"),
        new("Start pomodoro", "start begin go play resume", "", () => { }, "Timer"),
        new("Today", "day journal today", "", () => { }, "Go to"),
        new("This week", "week journal review", "", () => { }, "Go to"),
        new("Insights", "statistics stats history", "", () => { }, "Go to"),
        new("Timer settings", "settings preferences durations", "", () => { }, "Settings"),
        new("Appearance", "settings theme look", "", () => { }, "Settings"),
        new("How's today going?", "", "", () => { }, "Ask YAPA", Question: true),
    ];

    private static PaletteCommand Ask(string text) => new(text, "", "", () => { }, "Ask YAPA", Ask: true);

    private static List<string> Titles(string text) => CommandPalette.Rank(text, Commands, Ask).Select(c => c.Title).ToList();

    [TestMethod]
    public void Nothing_typed_shows_everything_in_order()
        => CollectionAssert.AreEqual(Commands.Select(c => c.Title).ToList(), Titles(""));

    [TestMethod]
    public void A_word_naming_an_action_puts_the_action_first_and_asking_last()
    {
        var titles = Titles("week");
        Assert.AreEqual("This week", titles[0]);
        Assert.AreEqual("week", titles[^1]);
    }

    [TestMethod]
    public void A_sentence_puts_asking_first()
        => Assert.AreEqual("log an hour at the library", Titles("log an hour at the library")[0]);

    [TestMethod]
    public void A_question_puts_asking_first()
        => Assert.AreEqual("stats?", Titles("stats?")[0]);

    [TestMethod]
    public void Nothing_to_run_leaves_only_asking()
        => CollectionAssert.AreEqual(new[] { "zebra" }, Titles("zebra"));

    [TestMethod]
    public void Keywords_find_what_titles_dont()
        => Assert.AreEqual("Insights", Titles("stats")[0]);

    [TestMethod]
    public void The_titles_first_word_beats_a_later_word_and_keywords()
    {
        // "Timer settings" starts with the typed word; Appearance only has it as a keyword.
        var titles = Titles("set");
        Assert.AreEqual("Timer settings", titles[0]);
        Assert.IsTrue(titles.IndexOf("Appearance") > 0);
    }

    [TestMethod]
    public void Every_word_must_match()
    {
        Assert.AreEqual("Start pomodoro", Titles("start pom")[0]);
        Assert.IsFalse(Titles("start zebra").Contains("Start pomodoro"));
    }

    [TestMethod]
    public void Suggested_questions_are_never_matched()
        => Assert.IsFalse(Titles("today").Contains("How's today going?"));

    [TestMethod]
    public void Without_asking_only_matches_are_listed()
        => CollectionAssert.AreEqual(new[] { "Pause" }, CommandPalette.Rank("pau", Commands, null).Select(c => c.Title).ToList());
}
