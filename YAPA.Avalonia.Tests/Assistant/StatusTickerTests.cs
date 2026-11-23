using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Assistant;

namespace YAPA.Avalonia.Tests.Assistant;

[TestClass]
public sealed class StatusTickerTests
{
    private static readonly AgentStatus Day = new("Reading your day", true);
    private static readonly AgentStatus History = new("Looking through your history", true);
    private static readonly AgentStatus Log = new("Logging a pomodoro", true);

    [TestMethod]
    public void AQuickToolIsStillShownForItsHold()
    {
        var ticker = new StatusTicker();
        ticker.Push(AgentStatus.Thinking, 0);
        ticker.Push(Day, 1.0);
        ticker.Push(AgentStatus.Thinking, 1.01); // the tool answered in 10 ms

        Assert.AreEqual(Day, ticker.Current);
        Assert.IsFalse(ticker.Advance(1.5));
        Assert.IsTrue(ticker.Advance(1.0 + StatusTicker.Hold));
        Assert.AreEqual(AgentStatus.Thinking, ticker.Current);
        Assert.AreEqual(Day, ticker.Previous);
    }

    [TestMethod]
    public void ThinkingBetweenTwoStepsGivesWayToTheNextStep()
    {
        var ticker = new StatusTicker();
        ticker.Push(AgentStatus.Thinking, 0);
        ticker.Push(Day, 1);
        ticker.Push(AgentStatus.Thinking, 1.01);
        ticker.Push(History, 1.02);
        ticker.Push(AgentStatus.Thinking, 1.03);
        ticker.Push(Log, 1.04);

        ticker.Advance(2);
        Assert.AreEqual(History, ticker.Current);
        ticker.Advance(3);
        Assert.AreEqual(Log, ticker.Current);
        Assert.IsFalse(ticker.HasWaiting);
    }

    [TestMethod]
    public void ABacklogKeepsTheNewestThree()
    {
        var ticker = new StatusTicker();
        ticker.Push(AgentStatus.Thinking, 0);
        ticker.Push(Day, 1);
        var steps = new[] { "a", "b", "c", "d", "e" }.Select(t => new AgentStatus(t, false)).ToArray();
        foreach (var step in steps) ticker.Push(step, 1.1);

        var shown = new System.Collections.Generic.List<string>();
        for (var t = 2.0; ticker.Advance(t); t += 1) shown.Add(ticker.Current!.Text);
        CollectionAssert.AreEqual(new[] { "c", "d", "e" }, shown);
    }

    [TestMethod]
    public void AStatusReplacedAsTheLineAppearsIsSwappedWithoutARoll()
    {
        var ticker = new StatusTicker();
        ticker.Push(AgentStatus.Thinking, 0);
        ticker.Push(Day, 0.05);

        Assert.AreEqual(Day, ticker.Current);
        Assert.IsNull(ticker.Previous);
        Assert.IsFalse(ticker.HasWaiting);
    }

    [TestMethod]
    public void AnInstantStepIsNotSwappedAwayUnseen()
    {
        // Words, then a tool that answers at once: the line appears with Thinking, the step
        // takes its place, and Thinking after it waits its turn.
        var ticker = new StatusTicker();
        ticker.Push(AgentStatus.Thinking, 0);
        ticker.Push(Log, 0.01);
        ticker.Push(AgentStatus.Thinking, 0.02);

        Assert.AreEqual(Log, ticker.Current);
        Assert.IsTrue(ticker.HasWaiting);
        Assert.IsTrue(ticker.Advance(StatusTicker.Hold));
        Assert.AreEqual(AgentStatus.Thinking, ticker.Current);
    }

    [TestMethod]
    public void TheSameStatusAgainChangesNothing()
    {
        var ticker = new StatusTicker();
        ticker.Push(Day, 0);
        ticker.Push(new AgentStatus("Reading your day", true), 2);

        Assert.IsFalse(ticker.HasWaiting);
        Assert.IsFalse(ticker.Advance(5));
    }
}
