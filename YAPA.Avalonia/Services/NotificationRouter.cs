using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Platform;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Windows;

namespace YAPA.Avalonia.Services;

/// <summary>
/// Where YAPA's notices go. Reminders and actions the assistant scheduled appear, with the
/// "timer" style (the default), as a bubble on the timer, and on the desktop only while the
/// timer is hidden; with "desktop", on the desktop. They bring their own chime either way.
/// Interval endings always go to the desktop: the cue and the pulsing timer already announce
/// them, and the desktop keeps their buttons around while the timer is covered.
/// </summary>
public sealed class NotificationRouter : IDesktopNotifier
{
    private readonly IDesktopNotifier _desktop;
    private readonly GeneralSettings _general;
    private readonly AppNavigator _navigator;
    private readonly Func<SoundEffects> _sounds;
    private NotificationBubble? _bubble;

    /// <param name="sounds">Lazy: sound effects resolve the notifier's neighbours, and only a chime needs them.</param>
    public NotificationRouter(IDesktopNotifier desktop, GeneralSettings general, AppNavigator navigator, Func<SoundEffects> sounds)
    {
        _desktop = desktop;
        _general = general;
        _navigator = navigator;
        _sounds = sounds;
        _desktop.ActionInvoked += key => ActionInvoked?.Invoke(key);
    }

    public bool SupportsActions => _desktop.SupportsActions;

    public event Action<string>? ActionInvoked;

    public Task ShowAsync(DesktopNotification notification)
    {
        if (notification.Chime) Dispatcher.UIThread.Post(() => _sounds().Play(SoundCue.Reminder));

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (notification.Category == DesktopNotification.ReminderCategory && _general.NotificationStyle == "timer"
                && _navigator.IsTimerShowing && _navigator.TimerWindow is { } timer)
            {
                _bubble ??= new NotificationBubble();
                _bubble.Present(notification, timer, key => ActionInvoked?.Invoke(key));
                return;
            }
            await _desktop.ShowAsync(notification);
        });
    }

    /// <summary>The timer's own notice is obsolete (the user moved on); a reminder bubble stays until read.</summary>
    public Task DismissAsync() => _desktop.DismissAsync();

    public void Dispose()
    {
        _bubble?.Close();
        _desktop.Dispose();
    }
}
