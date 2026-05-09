using System;

namespace YAPA.Avalonia.Windows;

public sealed record Quote(string Text, string Source);

public static class Quotes
{
    private static readonly (string Text, string Source)[] All =
    [
        ("Yesterday, you said tomorrow.", ""),
        ("Don't compare your beginning to someone else's middle.", ""),
        ("The wisest mind has something yet to learn.", ""),
        ("Be the change you wish to see in the world.", "Gandhi"),
        ("When you're going through hell, keep going.", "Winston Churchill"),
        ("Don't let perfection become procrastination. Do it now.", ""),
        ("Launch and learn. Everything is progress.", ""),
        ("A year from now you will wish you had started today.", "Karen Lamb"),
        ("Failure is success if you learn from it.", ""),
        ("If you don't like where you are, change it.", ""),
        ("Stay hungry; stay foolish.", ""),
        ("You got this. Make it happen.", ""),
        ("Care about what other people think and you will always be their prisoner.", "Lao Tzu"),
        ("Do a little more of what you want to do every day.", ""),
        ("Progress is impossible without change.", ""),
        ("Be kind; everyone you meet is fighting a hard battle.", ""),
        ("No one saves us but ourselves.", "Buddha"),
        ("Never give up. Never let things out of your control dictate who you are.", ""),
        ("Do more of what makes you happy.", ""),
        ("Don't blame others as an excuse for not working hard enough.", ""),
        ("The secret of getting ahead is getting started.", "Mark Twain"),
        ("It always seems impossible until it's done.", "Nelson Mandela"),
        ("Your time is limited, so don't waste it living someone else's life.", "Steve Jobs"),
        ("Success is not final; failure is not fatal.", "Winston Churchill"),
    ];

    private static readonly Random _rng = new();

    public static Quote Random()
    {
        var (text, source) = All[_rng.Next(All.Length)];
        return new Quote(text, source);
    }
}
