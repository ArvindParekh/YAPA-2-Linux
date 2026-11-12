using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>
/// Notices a mouse press anywhere on the screen outside a given area, for a popover that
/// should close when you click away. Losing focus isn't enough on GNOME: a click on the top
/// bar, the dock or the bare desktop goes to the shell and leaves focus where it was.
///
/// It reads the pointer's button state (XQueryPointer) a few times a second, only while
/// started. That needs no grab, so the click still goes wherever it was aimed. Null off X11.
/// </summary>
public sealed class X11PointerWatch : IDisposable
{
    private const uint AnyButton = (1u << 8) | (1u << 9) | (1u << 10); // Button1..3Mask
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(35);

    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly DispatcherTimer _timer;
    private Func<PixelRect>? _inside;
    private Action? _outside;
    private bool _wasPressed;

    private X11PointerWatch(IntPtr display)
    {
        _display = display;
        _root = Xlib.XDefaultRootWindow(display);
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = Interval };
        _timer.Tick += (_, _) => Poll();
    }

    public static X11PointerWatch? TryCreate()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return null;
        try
        {
            var display = X11Session.TryOpen(_ => { });
            return display == IntPtr.Zero ? null : new X11PointerWatch(display);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[x11] pointer watch unavailable: {e.Message}");
            return null;
        }
    }

    /// <param name="inside">Where presses are welcome, in screen pixels; asked at each press.</param>
    /// <param name="outside">Called (on the UI thread) for a press anywhere else.</param>
    public void Start(Func<PixelRect> inside, Action outside)
    {
        _inside = inside;
        _outside = outside;
        // A button already down (the double-click that opened the panel) isn't a new press.
        _wasPressed = Pressed(out _, out _);
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var pressed = Pressed(out var x, out var y);
        if (pressed && !_wasPressed && _inside is { } inside && !inside().Contains(new PixelPoint(x, y)))
        {
            _timer.Stop();
            _outside?.Invoke();
        }
        _wasPressed = pressed;
    }

    private bool Pressed(out int x, out int y)
    {
        x = y = 0;
        try
        {
            if (!XQueryPointer(_display, _root, out _, out _, out x, out y, out _, out _, out var mask)) return false;
            return (mask & AnyButton) != 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _timer.Stop();
            return false;
        }
    }

    [DllImport("libX11.so.6")]
    private static extern bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    public void Dispose()
    {
        _timer.Stop();
        X11Session.Close(_display);
    }
}
