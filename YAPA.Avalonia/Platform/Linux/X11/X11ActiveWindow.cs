using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <param name="Class">WM_CLASS's class part ("zen", "com.anthropic.Claude"); the instance part when there's no class.</param>
/// <param name="Instance">WM_CLASS's instance part, which tells a browser driven by an agent from the user's own.</param>
public sealed record ActiveWindow(string Class, string Instance, string Title, int ProcessId);

/// <summary>
/// The window in front and how long since the user last touched the keyboard or mouse,
/// read from the X server on a connection of its own. Null off X11 (a Wayland session
/// keeps both to the compositor).
/// </summary>
public sealed class X11ActiveWindow : IDisposable
{
    private const string Xss = "libXss.so.1";

    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly IntPtr _activeAtom, _nameAtom, _utf8Atom, _pidAtom;
    private bool _idleUnavailable;

    private static readonly IntPtr XA_STRING = 31;
    private static readonly IntPtr XA_WM_NAME = 39;
    private static readonly IntPtr XA_WM_CLASS = 67;

    private X11ActiveWindow(IntPtr display)
    {
        _display = display;
        _root = Xlib.XDefaultRootWindow(display);
        _activeAtom = Xlib.XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
        _nameAtom = Xlib.XInternAtom(display, "_NET_WM_NAME", false);
        _utf8Atom = Xlib.XInternAtom(display, "UTF8_STRING", false);
        _pidAtom = Xlib.XInternAtom(display, "_NET_WM_PID", false);
    }

    public static X11ActiveWindow? TryCreate()
    {
        if (!OperatingSystem.IsLinux() || !X11Session.IsX11Desktop) return null;
        try
        {
            // A window can close between being named active and being read; that's an error to ignore.
            var display = X11Session.TryOpen(_ => { });
            return display == IntPtr.Zero ? null : new X11ActiveWindow(display);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[x11] active window unavailable: {e.Message}");
            return null;
        }
    }

    /// <summary>The window in front, or null (the desktop, a menu, or nothing).</summary>
    public ActiveWindow? Current()
    {
        var active = X11Window.ReadLongs(_display, _root, _activeAtom);
        if (active.Count == 0 || active[0] == IntPtr.Zero) return null;
        var window = active[0];

        var title = ReadText(window, _nameAtom, _utf8Atom) ?? ReadText(window, XA_WM_NAME, XA_STRING) ?? "";
        var (instance, cls) = ReadClass(window);
        var pid = X11Window.ReadLongs(_display, window, _pidAtom) is { Count: > 0 } p ? (int)p[0] : 0;
        return new ActiveWindow(cls.Length > 0 ? cls : instance, instance, title, pid);
    }

    /// <summary>Time since the last keyboard or mouse input; null when the server can't say.</summary>
    public TimeSpan? Idle()
    {
        if (_idleUnavailable) return null;
        try
        {
            var info = XScreenSaverAllocInfo();
            if (info == IntPtr.Zero) return null;
            try
            {
                if (XScreenSaverQueryInfo(_display, _root, info) == 0) return null;
                // XScreenSaverInfo: Window, int state, int kind, unsigned long til_or_since, unsigned long idle, ...
                var idle = Marshal.ReadIntPtr(info, IntPtr.Size + 4 + 4 + IntPtr.Size);
                return TimeSpan.FromMilliseconds((long)idle);
            }
            finally
            {
                Xlib.XFree(info);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _idleUnavailable = true;
            Trace.WriteLine($"[x11] idle time unavailable: {e.Message}");
            return null;
        }
    }

    private string? ReadText(IntPtr window, IntPtr property, IntPtr type)
    {
        var status = Xlib.XGetWindowProperty(_display, window, property, IntPtr.Zero, (IntPtr)1024, false, type,
            out _, out var format, out var count, out _, out var data);
        try
        {
            if (status != Xlib.Success || data == IntPtr.Zero || format != 8 || count == IntPtr.Zero) return null;
            var bytes = new byte[(int)count];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            if (data != IntPtr.Zero) Xlib.XFree(data);
        }
    }

    /// <summary>WM_CLASS: two NUL-terminated strings, instance then class.</summary>
    private (string Instance, string Class) ReadClass(IntPtr window)
    {
        var raw = ReadText(window, XA_WM_CLASS, XA_STRING);
        if (raw == null) return ("", "");
        var parts = raw.Split('\0');
        return (parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "");
    }

    [DllImport(Xss)] private static extern IntPtr XScreenSaverAllocInfo();
    [DllImport(Xss)] private static extern int XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);

    public void Dispose() => X11Session.Close(_display);
}
