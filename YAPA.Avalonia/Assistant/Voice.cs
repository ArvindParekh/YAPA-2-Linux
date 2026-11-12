using System;
using System.Collections.Generic;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// What the panel says before anyone types: a greeting under the orb and the prompt's
/// placeholder, picked for the moment (time of day, mid-pomodoro, on a break). The voice is
/// the user's own on a good day: confident, quick, a little cheeky, never saccharine.
/// </summary>
public static class Voice
{
    public readonly record struct Line(string Greeting, string Placeholder);

    private static readonly Random Pick = new();

    public static Line For(PomodoroPhase phase, PomodoroPhase pausedPhase, DateTime now, int doneToday, int goal)
    {
        var greeting = One(Greetings(phase, pausedPhase, now, doneToday, goal));
        var placeholder = One(Placeholders(phase, pausedPhase));
        return new Line(greeting, placeholder);
    }

    private static string One(IReadOnlyList<string> lines) => lines[Pick.Next(lines.Count)];

    private static IReadOnlyList<string> Greetings(PomodoroPhase phase, PomodoroPhase paused, DateTime now, int done, int goal)
    {
        if (goal > 0 && done >= goal)
            return ["Goal's done. Anything past this is a flex.", $"{done} of {goal}. Victory lap, or more?", "Goal hit. What's the encore?"];

        switch (phase)
        {
            case PomodoroPhase.Work:
                return ["Deep in it. Make it quick, we're busy.", "In the zone. What do you need?", "Focus mode. I'll keep it short."];
            case PomodoroPhase.Break:
                return ["On a break. Stretch, sip, then rule the world.", "Resting is training. What's up?", "Break time. Your brain says thanks."];
            case PomodoroPhase.WorkEnded:
                return ["Nailed that one. Break, or are we on a roll?", "Another one in the bank. What next?", "Done. Future you is impressed."];
            case PomodoroPhase.BreakEnded:
                return ["Break's over, champ. Next one?", "Recharged. Let's go again.", "Back to it whenever you are."];
            case PomodoroPhase.Pause:
                return paused == PomodoroPhase.Break
                    ? ["The break's on hold. What's up?"]
                    : ["Paused, not stopped. What's going on?", "Holding the clock. Talk to me."];
        }

        return now.Hour switch
        {
            < 5 => ["Up late, or up early? Either way, let's make it count.", "The quiet hours. Best ideas live here."],
            < 12 => ["Morning. Let's make today embarrassingly productive.", "Fresh day, clean slate. What's the plan?", "Morning. The day is yours to win."],
            < 17 => done > 0
                ? ["Afternoon. You've got momentum, use it.", "Solid so far. What's next on the list?"]
                : ["Afternoon. Not too late for a great day.", "Let's turn this afternoon into something."],
            < 21 => ["Evening session? Respect. What's the plan?", "Still at it. Let's finish strong."],
            _ => ["Burning the midnight oil? Let's make it count.", "Late one. Let's be brilliant, then sleep."],
        };
    }

    private static IReadOnlyList<string> Placeholders(PomodoroPhase phase, PomodoroPhase paused) => phase switch
    {
        PomodoroPhase.Work => ["Need more time? Say the word", "Quick question, then back to it", "Bend time a little"],
        PomodoroPhase.Break => ["Five more minutes? I won't tell", "Plan the next one while you rest"],
        PomodoroPhase.Pause when paused == PomodoroPhase.Work => ["Resume? Log it? Your call", "Tell me what happened"],
        _ =>
        [
            "What are we conquering?",
            "Your move, genius",
            "Plan it, log it, bend time",
            "Ask anything. You'll crush it anyway",
            "Tell me the plan",
            "Ambition loading. Type it out",
        ],
    };
}
