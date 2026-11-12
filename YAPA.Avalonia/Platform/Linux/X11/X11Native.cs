using System;
using System.Runtime.InteropServices;

namespace YAPA.Avalonia.Platform.Linux.X11;

// Raw bindings. Versioned sonames on purpose: the unversioned libX11.so etc.
// come from -dev packages and are missing on most user machines.
//
// Xlib types on LP64 Linux: Window/Atom/XID/Time/unsigned long are 64-bit
// (nint), Bool/int are 32-bit. Events are read from a raw 192-byte XEvent
// buffer at fixed offsets. That keeps this file free of `unsafe`, so the
// project needs no AllowUnsafeBlocks.

internal static class Xlib
{
    private const string Lib = "libX11.so.6";

    public const int Success = 0;
    public const int BadWindow = 3;

    public const long StructureNotifyMask = 1L << 17;
    public const long SubstructureNotifyMask = 1L << 19;
    public const long SubstructureRedirectMask = 1L << 20;

    public const int DestroyNotify = 17;
    public const int ConfigureNotify = 22;
    public const int ClientMessage = 33;

    public const int PropModeReplace = 0;

    // Predefined atoms (X.h / Xatom.h).
    public static readonly IntPtr XA_ATOM = 4;
    public static readonly IntPtr XA_CARDINAL = 6;
    public static readonly IntPtr AnyPropertyType = IntPtr.Zero;

    public const int XEventSize = 192;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void XIOErrorExitHandler(IntPtr display, IntPtr userData);

    [DllImport(Lib)] public static extern int XInitThreads();
    [DllImport(Lib)] public static extern IntPtr XOpenDisplay(string? displayName);
    [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] public static extern int XConnectionNumber(IntPtr display);
    [DllImport(Lib)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(Lib)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] public static extern int XFlush(IntPtr display);
    [DllImport(Lib)] public static extern int XSync(IntPtr display, bool discard);
    [DllImport(Lib)] public static extern int XPending(IntPtr display);
    [DllImport(Lib)] public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(Lib)] public static extern int XFree(IntPtr data);

    [DllImport(Lib)] public static extern IntPtr XSetErrorHandler(IntPtr handler);

    // libX11 >= 1.8 only. Per-display, so unlike XSetIOErrorHandler it can't
    // clobber the toolkit's handler, and it lets us survive a dead connection
    // instead of Xlib calling exit().
    [DllImport(Lib)] public static extern void XSetIOErrorExitHandler(IntPtr display, XIOErrorExitHandler handler, IntPtr userData);

    [DllImport(Lib)] public static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);
    [DllImport(Lib)] public static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr selection);
    [DllImport(Lib)] public static extern int XSelectInput(IntPtr display, IntPtr window, IntPtr eventMask);

    [DllImport(Lib)]
    public static extern bool XQueryExtension(IntPtr display, string name,
        out int majorOpcode, out int firstEvent, out int firstError);

    [DllImport(Lib)]
    public static extern int XQueryTree(IntPtr display, IntPtr window,
        out IntPtr root, out IntPtr parent, out IntPtr children, out uint childCount);

    [DllImport(Lib)]
    public static extern bool XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest,
        int srcX, int srcY, out int destX, out int destY, out IntPtr child);

    [DllImport(Lib)]
    public static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, IntPtr[] data, int elementCount);

    [DllImport(Lib)] public static extern int XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);

    [DllImport(Lib)]
    public static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property,
        IntPtr longOffset, IntPtr longLength, bool delete, IntPtr reqType,
        out IntPtr actualType, out int actualFormat, out IntPtr itemCount, out IntPtr bytesAfter, out IntPtr prop);

    [DllImport(Lib)]
    public static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, IntPtr eventMask,
        ref XClientMessageEvent sendEvent);

    // Offsets into XEvent (LP64): common header, then per-type fields.
    public const int EvType = 0;
    public const int EvWindow = 32;               // XAnyEvent.window, XClientMessageEvent.window
    public const int EvDestroyWindow = 40;        // XDestroyWindowEvent.window
    public const int EvConfigureWindow = 40;      // XConfigureEvent.window
    public const int EvShapeKind = 40;            // XShapeEvent.kind
    public const int EvClientMessageType = 40;    // XClientMessageEvent.message_type
    public const int EvClientData = 56;           // XClientMessageEvent.data.l[0]

    // Offsets into XErrorEvent (LP64).
    public const int ErrResourceId = 16;
    public const int ErrErrorCode = 32;
    public const int ErrRequestCode = 33;
}

/// <summary>
/// Client-message XEvent. Sized to a full XEvent because XSendEvent takes an
/// XEvent*; padding to 192 bytes means Xlib can never read past our buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Xlib.XEventSize)]
internal struct XClientMessageEvent
{
    public int type;
    public IntPtr serial;
    public int send_event;
    public IntPtr display;
    public IntPtr window;
    public IntPtr message_type;
    public int format;
    public IntPtr l0, l1, l2, l3, l4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XRectangle
{
    public short x, y;
    public ushort width, height;
}

internal static class XShape
{
    private const string Lib = "libXext.so.6";

    public const int ShapeInput = 2;
    public const int ShapeNotify = 0; // relative to the extension's event base
    public const long ShapeNotifyMask = 1L << 0;

    [DllImport(Lib)] public static extern bool XShapeQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(Lib)] public static extern void XShapeSelectInput(IntPtr display, IntPtr window, IntPtr mask);

    public const int ShapeSet = 0;
    public const int Unsorted = 0;

    [DllImport(Lib)]
    public static extern void XShapeCombineRectangles(IntPtr display, IntPtr dest, int destKind, int xOff, int yOff,
        XRectangle[] rectangles, int count, int op, int ordering);

    /// <summary>With <c>src</c> None (zero) and ShapeSet, resets the shape to the whole window.</summary>
    [DllImport(Lib)]
    public static extern void XShapeCombineMask(IntPtr display, IntPtr dest, int destKind, int xOff, int yOff, IntPtr src, int op);

    /// <returns>Array of XRectangle, freed with XFree.</returns>
    [DllImport(Lib)]
    public static extern IntPtr XShapeGetRectangles(IntPtr display, IntPtr window, int kind, out int count, out int ordering);
}

internal static class XComposite
{
    private const string Lib = "libXcomposite.so.1";

    [DllImport(Lib)] public static extern bool XCompositeQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(Lib)] public static extern IntPtr XCompositeGetOverlayWindow(IntPtr display, IntPtr window);
    [DllImport(Lib)] public static extern void XCompositeReleaseOverlayWindow(IntPtr display, IntPtr window);
}

internal static class Libc
{
    private const string Lib = "libc";

    public const int EFD_NONBLOCK = 0x800;
    public const int EFD_CLOEXEC = 0x80000;
    public const short POLLIN = 0x1;
    public const int EINTR = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport(Lib, SetLastError = true)] public static extern int eventfd(uint initval, int flags);
    [DllImport(Lib, SetLastError = true)] public static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);
    [DllImport(Lib, SetLastError = true)] public static extern nint write(int fd, ref ulong value, nint count);
    [DllImport(Lib, SetLastError = true)] public static extern int close(int fd);
}
