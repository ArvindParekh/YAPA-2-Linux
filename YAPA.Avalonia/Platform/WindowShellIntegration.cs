using Avalonia.Controls;
using YAPA.Avalonia.Specifics;

namespace YAPA.Avalonia.Platform;

/// <summary>
/// Window-manager hints Avalonia doesn't expose (workspace stickiness, shadow
/// suppression). Dispatches to the platform implementation; no-op elsewhere.
/// </summary>
public static class WindowShellIntegration
{
    public static void Apply(Window window, GeneralSettings settings)
    {
        // Filled in by the X11 integration.
    }
}
