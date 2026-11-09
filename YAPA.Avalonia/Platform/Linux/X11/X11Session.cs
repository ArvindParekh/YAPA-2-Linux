using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>An X protocol error raised on one of our own connections.</summary>
internal readonly record struct X11Error(byte ErrorCode, byte RequestCode, IntPtr ResourceId);

/// <summary>
/// Environment checks and connection plumbing shared by the X11 helpers.
/// </summary>
internal static class X11Session
{
    /// <summary>
    /// True for a real X11 desktop session. False under Wayland, even though
    /// DISPLAY is set there for Xwayland: Xwayland has no shell stage window,
    /// and on Wayland the compositor routes clicks to shell chrome correctly anyway.
    /// </summary>
    public static bool IsX11Desktop =>
        IsXServerConfigured
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && !string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when an X server is configured at all (X11, or Xwayland under Wayland).</summary>
    public static bool IsXServerConfigured =>
        OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>
    /// Opens a private connection with an error trap attached. Returns
    /// IntPtr.Zero when there is no X server or libX11 isn't installed.
    /// </summary>
    public static IntPtr TryOpen(Action<X11Error> onError)
    {
        if (!IsXServerConfigured) return IntPtr.Zero;
        try
        {
            EnsureThreadsInitialized();
            var display = Xlib.XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                Trace.WriteLine("[x11] XOpenDisplay failed");
                return IntPtr.Zero;
            }
            X11ErrorTrap.Register(display, onError);
            return display;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[x11] libX11 unavailable: {e.Message}");
            return IntPtr.Zero;
        }
    }

    public static void Close(IntPtr display)
    {
        if (display == IntPtr.Zero) return;
        X11ErrorTrap.Unregister(display);
        Xlib.XCloseDisplay(display);
    }

    /// <summary>True when the server is Xwayland, whatever the environment variables claim.</summary>
    public static bool IsXwayland(IntPtr display) => Xlib.XQueryExtension(display, "XWAYLAND", out _, out _, out _);

    private static bool s_threadsInitialized;

    // The region monitor's connection lives on its own thread while the UI
    // thread opens short-lived ones for the window helpers. Avalonia already
    // calls XInitThreads, and libX11 >= 1.8 does so implicitly, but tests and
    // older libraries get neither. A repeated call is harmless.
    private static void EnsureThreadsInitialized()
    {
        if (s_threadsInitialized) return;
        Xlib.XInitThreads();
        s_threadsInitialized = true;
    }
}

/// <summary>
/// Keeps X protocol errors on our connections from killing the process.
///
/// Xlib's default error handler prints and calls exit(). The handler is
/// process-global, and Avalonia installs its own, so this one chains: errors on
/// a display we registered go to that display's sink, and everything else goes
/// to whichever handler was installed before us.
/// </summary>
internal static class X11ErrorTrap
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<IntPtr, Action<X11Error>> Sinks = new();

    // Held in static fields so the GC never collects the delegates native code calls.
    private static readonly Xlib.XErrorHandler HandlerDelegate = Handle;
    private static readonly IntPtr HandlerPointer = Marshal.GetFunctionPointerForDelegate(HandlerDelegate);
    private static Xlib.XErrorHandler? s_previousDelegate;

    public static void Register(IntPtr display, Action<X11Error> sink)
    {
        lock (Gate)
        {
            // Re-install on every registration. If someone replaced the handler
            // since (a toolkit re-initialising), we go back on top and chain to them.
            var previous = Xlib.XSetErrorHandler(HandlerPointer);
            if (previous != HandlerPointer && previous != IntPtr.Zero)
            {
                s_previousDelegate = Marshal.GetDelegateForFunctionPointer<Xlib.XErrorHandler>(previous);
            }
        }
        Sinks[display] = sink;
    }

    public static void Unregister(IntPtr display) => Sinks.TryRemove(display, out _);

    private static int Handle(IntPtr display, IntPtr errorEvent)
    {
        // An exception unwinding into Xlib's C frames would take the process
        // down, so nothing escapes this method.
        try
        {
            if (Sinks.TryGetValue(display, out var sink))
            {
                sink(new X11Error(
                    Marshal.ReadByte(errorEvent, Xlib.ErrErrorCode),
                    Marshal.ReadByte(errorEvent, Xlib.ErrRequestCode),
                    Marshal.ReadIntPtr(errorEvent, Xlib.ErrResourceId)));
                return 0;
            }
            return s_previousDelegate?.Invoke(display, errorEvent) ?? 0;
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[x11] error handler failed: {e.Message}");
            return 0;
        }
    }
}
