using System;
using Avalonia.Threading;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Services;

/// <summary>
/// Desktop notifications at the end of each interval, with buttons that act on
/// the timer directly, so the next step is one click even while the timer
/// window is hidden or covered.
/// </summary>
public sealed class NotificationEffects : IDisposable
{
    private const string StartBreak = "start-break";
    private const string SkipBreak = "skip-break";
    private const string StartWork = "start-work";
    private const string LongBreak = "long-break";
    private const string ShowTimer = "default";

    private readonly IPomodoroEngine _engine;
    private readonly PomodoroEngineSettings _engineSettings;
    private readonly GeneralSettings _general;
    private readonly IDesktopNotifier _notifier;
    private readonly IAppNavigator _navigator;

    public NotificationEffects(IPomodoroEngine engine, PomodoroEngineSettings engineSettings, GeneralSettings general,
        IDesktopNotifier notifier, IAppNavigator navigator)
    {
        _engine = engine;
        _engineSettings = engineSettings;
        _general = general;
        _notifier = notifier;
        _navigator = navigator;

        _engine.Transitioned += OnTransition;
        _notifier.ActionInvoked += OnAction;
    }

    private void OnTransition(PomodoroTransition t)
    {
        if (!_general.DesktopNotifications) return;

        switch (t.Kind)
        {
            case PomodoroTransitionKind.WorkCompleted:
                Show(WorkCompleted());
                break;

            case PomodoroTransitionKind.BreakCompleted:
                Show(BreakCompleted());
                break;

            case PomodoroTransitionKind.BreakStarted when t.Automatic:
            case PomodoroTransitionKind.WorkStarted when t.Automatic:
                // The completion notification already announced this; it's now just stale buttons.
                // Replace it with a quiet, self-expiring note.
                Show(new DesktopNotification(
                    t.Kind == PomodoroTransitionKind.BreakStarted ? BreakTitle(t.IsLongBreak) + " started" : "Pomodoro started",
                    t.Kind == PomodoroTransitionKind.BreakStarted ? $"Back to work in {Minutes(_engine.BreakTime)}." : $"Focus for {Minutes(_engine.WorkTime)}.",
                    [new NotificationAction(ShowTimer, "Show timer")]));
                break;

            default:
                // Anything the user did themselves makes an earlier prompt obsolete.
                if (!t.Automatic) _ = _notifier.DismissAsync();
                break;
        }
    }

    private DesktopNotification WorkCompleted()
    {
        var longBreak = _engine.IsLongBreak;
        if (_engineSettings.AutoStartBreak)
        {
            return new DesktopNotification("Pomodoro complete",
                $"{BreakTitle(longBreak)} of {Minutes(_engine.BreakTime)} starts now.",
                [new NotificationAction(SkipBreak, "Skip break"), new NotificationAction(ShowTimer, "Show timer")]);
        }

        // Offer the long break early unless it's already the long break's turn.
        NotificationAction[] actions = longBreak
            ? [new(StartBreak, "Start long break"), new(SkipBreak, "Skip break"), new(ShowTimer, "Show timer")]
            : [new(StartBreak, "Start break"), new(LongBreak, "Long break"), new(SkipBreak, "Skip break"), new(ShowTimer, "Show timer")];

        return new DesktopNotification("Pomodoro complete",
            $"Time for a {Minutes(_engine.BreakTime)} {(longBreak ? "long break" : "break")}.",
            actions,
            Persistent: true);
    }

    private DesktopNotification BreakCompleted()
    {
        if (_engineSettings.AutoStartWork)
        {
            return new DesktopNotification("Break's over", $"Next pomodoro started: {Minutes(_engine.WorkTime)} of focus.",
                [new NotificationAction(ShowTimer, "Show timer")]);
        }

        return new DesktopNotification("Break's over", "Ready for the next pomodoro?",
            [
                new NotificationAction(StartWork, "Start pomodoro"),
                new NotificationAction(LongBreak, "Long break"),
                new NotificationAction(ShowTimer, "Show timer"),
            ],
            Persistent: true);
    }

    private void OnAction(string key)
    {
        // Raised on a D-Bus thread; the engine lives on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            switch (key)
            {
                case StartBreak when _engine.Phase == PomodoroPhase.WorkEnded:
                case StartWork when _engine.Phase == PomodoroPhase.BreakEnded:
                    _engine.Start();
                    break;
                case SkipBreak:
                    _engine.Skip();
                    break;
                case LongBreak:
                    _engine.StartLongBreak();
                    break;
                case ShowTimer:
                    _navigator.ShowTimer();
                    break;
            }
        });
    }

    private void Show(DesktopNotification notification) => _ = _notifier.ShowAsync(notification);

    private static string BreakTitle(bool longBreak) => longBreak ? "Long break" : "Break";

    private static string Minutes(int seconds)
    {
        var minutes = (int)Math.Round(seconds / 60.0);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    public void Dispose()
    {
        _engine.Transitioned -= OnTransition;
        _notifier.ActionInvoked -= OnAction;
    }
}
