using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YAPA.Avalonia.Platform;

/// <summary>
/// A button on a desktop notification. <paramref name="Key"/> comes back through
/// <see cref="IDesktopNotifier.ActionInvoked"/>. The key <c>"default"</c> is not shown as a
/// button: servers fire it when the notification body is clicked, and only if it is in the list.
/// </summary>
public sealed record NotificationAction(string Key, string Label);

/// <param name="Persistent">
/// True: never expires and stays in the notification list until the user acts on it or it is
/// replaced. False: transient, so it disappears once the banner hides.
/// </param>
/// <param name="IconName">Themed icon name or absolute file path; null or empty for none.</param>
/// <param name="Category">freedesktop category hint; vendor categories use the <c>x-</c> prefix.</param>
public sealed record DesktopNotification(
    string Title,
    string Body,
    IReadOnlyList<NotificationAction> Actions,
    bool Persistent = false,
    string? IconName = "yapa2",
    string Category = "x-yapa.timer");

public interface IDesktopNotifier : IDisposable
{
    /// <summary>Whether the notification server shows action buttons. False until connected.</summary>
    bool SupportsActions { get; }

    /// <summary>
    /// Shows a notification, replacing the previous YAPA notification (YAPA shows one at a time).
    /// Never throws, and returns within a bounded time even if the server hangs.
    /// </summary>
    Task ShowAsync(DesktopNotification notification);

    /// <summary>Closes the current notification, if any. Never throws.</summary>
    Task DismissAsync();

    /// <summary>
    /// Raised on a background thread with the action key (<c>"default"</c> = body clicked).
    /// Only fires for the notification currently shown by this notifier. Consumers marshal to UI.
    /// </summary>
    event Action<string>? ActionInvoked;
}
