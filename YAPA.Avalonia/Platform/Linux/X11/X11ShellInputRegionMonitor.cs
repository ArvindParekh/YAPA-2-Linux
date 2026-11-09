using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;

namespace YAPA.Avalonia.Platform.Linux.X11;

/// <summary>
/// Tracks the input shape of the compositor's stage window on X11.
///
/// GNOME Shell paints everything into a "stage" window that is a child of
/// the Composite Overlay Window (COW). The COW is stacked above every client
/// window, so the stage's input shape decides which clicks the shell takes:
/// the top bar and the dock's whole container (much wider than its visible
/// icons). This class watches that shape through ShapeNotify events, so there
/// is no polling.
///
/// Everything runs on one dedicated thread with a private Display connection,
/// so it never contends with Avalonia's connection. The thread blocks in
/// poll() on the X socket and an eventfd, and Dispose wakes it through the
/// eventfd.
/// </summary>
public sealed class X11ShellInputRegionMonitor : IShellInputRegionMonitor
{
    // How soon to look for the stage again when there is none (no compositor
    // yet, or gnome-shell restarting). Backs off for compositors that never
    // have a stage (e.g. picom), so we don't churn the COW reference forever.
    private const int RediscoverInitialMs = 500;
    private const int RediscoverMaxMs = 30_000;

    private readonly object _lifecycleGate = new();
    private Thread? _thread;
    private int _wakeFd = -1;
    private bool _disposed;
    private volatile bool _stopping;

    private volatile IReadOnlyList<PixelRect> _current = Array.Empty<PixelRect>();

    // Only touched on the monitor thread from here on.
    private IntPtr _display;
    private IntPtr _root;
    private IntPtr _cmSelection;
    private IntPtr _managerAtom;
    private int _shapeEventBase;
    private IntPtr[] _stages = Array.Empty<IntPtr>();
    private bool _regionDirty;
    private bool _stageLost;
    private bool _errorSeen;
    private volatile bool _connectionDead;
    private long _nextDiscoveryAt;
    private int _rediscoverDelayMs = RediscoverInitialMs;
    private bool _loggedNoCompositor;

    // Held in a field so the GC never collects a delegate Xlib can call.
    private readonly Xlib.XIOErrorExitHandler _ioExitHandler;

    public X11ShellInputRegionMonitor()
    {
        _ioExitHandler = (_, _) => _connectionDead = true;
    }

    public IReadOnlyList<PixelRect> Current => _current;

    public event Action? Changed;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_thread is not null || _disposed) return;
            try
            {
                _wakeFd = Libc.eventfd(0, Libc.EFD_CLOEXEC | Libc.EFD_NONBLOCK);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Trace.WriteLine($"[x11] eventfd unavailable: {e.Message}");
                return;
            }
            if (_wakeFd < 0)
            {
                Trace.WriteLine($"[x11] eventfd failed: errno {Marshal.GetLastPInvokeError()}");
                return;
            }
            _thread = new Thread(Run) { IsBackground = true, Name = "x11-shell-region" };
            _thread.Start();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            thread = _thread;
            // Written under the gate because the thread closes the fd under it
            // when it exits. Writing to a closed fd number could hit an
            // unrelated file that reused it.
            if (_wakeFd >= 0)
            {
                ulong one = 1;
                Libc.write(_wakeFd, ref one, sizeof(ulong));
            }
        }

        // A Changed handler that disposes us runs on the monitor thread itself;
        // joining would deadlock, and the loop exits on its own after the handler returns.
        if (thread is null || thread == Thread.CurrentThread) return;
        if (!thread.Join(TimeSpan.FromSeconds(2)))
            Trace.WriteLine("[x11] shell region monitor thread did not stop within 2s");
    }

    private void Run()
    {
        var eventBuffer = IntPtr.Zero;
        try
        {
            _display = X11Session.TryOpen(OnXError);
            if (_display == IntPtr.Zero) return;

            try
            {
                Xlib.XSetIOErrorExitHandler(_display, _ioExitHandler, IntPtr.Zero);
            }
            catch (EntryPointNotFoundException)
            {
                // libX11 < 1.8. If the X server itself dies, Xlib will exit(),
                // but by then the whole session is gone too.
            }

            if (!Initialize()) return;

            eventBuffer = Marshal.AllocHGlobal(Xlib.XEventSize);
            var fds = new[]
            {
                new Libc.PollFd { fd = Xlib.XConnectionNumber(_display), events = Libc.POLLIN },
                new Libc.PollFd { fd = _wakeFd, events = Libc.POLLIN },
            };

            while (!_stopping && !_connectionDead)
            {
                if (_stages.Length == 0 && Environment.TickCount64 >= _nextDiscoveryAt)
                    Discover();

                // Drain Xlib's queue before poll(): replies we waited for may
                // have pulled events off the socket into the queue already, and
                // poll() would not see those.
                while (!_stopping && !_connectionDead && Xlib.XPending(_display) > 0)
                {
                    Xlib.XNextEvent(_display, eventBuffer);
                    HandleEvent(eventBuffer);
                }
                if (_stopping || _connectionDead) break;

                if (_stageLost)
                {
                    LoseStage();
                    continue;
                }
                if (_regionDirty)
                {
                    _regionDirty = false;
                    ReadRegion();
                    if (_stageLost) continue;
                }

                var timeout = _stages.Length == 0
                    ? (int)Math.Clamp(_nextDiscoveryAt - Environment.TickCount64, 0, RediscoverMaxMs)
                    : -1;
                fds[0].revents = fds[1].revents = 0;
                if (Libc.poll(fds, (nuint)fds.Length, timeout) < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    // The runtime's own signals (GC suspension) interrupt poll().
                    if (errno == Libc.EINTR) continue;
                    Trace.WriteLine($"[x11] poll failed: errno {errno}");
                    break;
                }
                if ((fds[1].revents & Libc.POLLIN) != 0) break;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"[x11] shell region monitor disabled, missing library: {e.Message}");
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[x11] shell region monitor crashed: {e}");
        }
        finally
        {
            if (eventBuffer != IntPtr.Zero) Marshal.FreeHGlobal(eventBuffer);
            if (_display != IntPtr.Zero)
            {
                // A dead connection must not be used again, not even to close it.
                if (_connectionDead) X11ErrorTrap.Unregister(_display);
                else X11Session.Close(_display);
                _display = IntPtr.Zero;
            }
            lock (_lifecycleGate)
            {
                if (_wakeFd >= 0) Libc.close(_wakeFd);
                _wakeFd = -1;
            }
            Publish(Array.Empty<PixelRect>());
        }
    }

    private bool Initialize()
    {
        if (X11Session.IsXwayland(_display))
        {
            // Xwayland has no shell stage, and asking for its COW would map a
            // real overlay surface on the Wayland side.
            Trace.WriteLine("[x11] Xwayland server, shell region monitor idle");
            return false;
        }
        if (!XShape.XShapeQueryExtension(_display, out _shapeEventBase, out _))
        {
            Trace.WriteLine("[x11] SHAPE extension missing, shell region monitor idle");
            return false;
        }
        if (!XComposite.XCompositeQueryExtension(_display, out _, out _))
        {
            Trace.WriteLine("[x11] Composite extension missing, shell region monitor idle");
            return false;
        }

        _root = Xlib.XDefaultRootWindow(_display);
        _cmSelection = Xlib.XInternAtom(_display, $"_NET_WM_CM_S{Xlib.XDefaultScreen(_display)}", false);
        _managerAtom = Xlib.XInternAtom(_display, "MANAGER", false);

        // StructureNotify on the root carries ICCCM MANAGER announcements, which
        // a starting compositor may send when it takes the CM selection. That
        // lets us pick up a restarted gnome-shell without waiting for a retry.
        Xlib.XSelectInput(_display, _root, (IntPtr)Xlib.StructureNotifyMask);
        return true;
    }

    /// <summary>
    /// Finds the stage window(s): the children of the COW.
    ///
    /// XCompositeGetOverlayWindow is not a pure query. The COW is reference
    /// counted per client: the server creates and maps it on the first
    /// request and unmaps/destroys it when the last client releases it. While
    /// mapped it sits above every window. Without a compositor holding it, our
    /// request would map an unpainted, input-swallowing window over the whole
    /// screen. So (1) only ask while a compositing manager owns _NET_WM_CM_Sn,
    /// which means the COW already exists and is mapped, and (2) release
    /// immediately. Watching the stage afterwards needs no COW reference.
    /// </summary>
    private void Discover()
    {
        if (Xlib.XGetSelectionOwner(_display, _cmSelection) == IntPtr.Zero)
        {
            if (!_loggedNoCompositor) Trace.WriteLine("[x11] no compositing manager, shell region monitor waiting");
            _loggedNoCompositor = true;
            ScheduleRediscovery();
            return;
        }

        _errorSeen = false;
        var children = Array.Empty<IntPtr>();
        var cow = XComposite.XCompositeGetOverlayWindow(_display, _root);
        try
        {
            if (cow != IntPtr.Zero
                && Xlib.XQueryTree(_display, cow, out _, out _, out var childList, out var count) != 0
                && childList != IntPtr.Zero)
            {
                children = new IntPtr[count];
                for (var i = 0; i < count; i++)
                    children[i] = Marshal.ReadIntPtr(childList, i * IntPtr.Size);
                Xlib.XFree(childList);
            }
        }
        finally
        {
            XComposite.XCompositeReleaseOverlayWindow(_display, _root);
        }

        foreach (var child in children)
        {
            // Our own event masks on the shell's windows: invisible to the shell.
            Xlib.XSelectInput(_display, child, (IntPtr)Xlib.StructureNotifyMask);
            XShape.XShapeSelectInput(_display, child, (IntPtr)XShape.ShapeNotifyMask);
        }
        Xlib.XSync(_display, false);

        if (_errorSeen || children.Length == 0)
        {
            // A stage vanished mid-discovery, or this compositor has no stage
            // child (it draws into the COW itself) and so takes no clicks.
            ScheduleRediscovery();
            return;
        }

        _stages = children;
        _stageLost = false;
        _regionDirty = true;
        _loggedNoCompositor = false;
        _rediscoverDelayMs = RediscoverInitialMs;
        Trace.WriteLine($"[x11] watching shell stage {string.Join(", ", children.Select(c => $"0x{c:x}"))}");
    }

    private void ScheduleRediscovery()
    {
        _nextDiscoveryAt = Environment.TickCount64 + _rediscoverDelayMs;
        _rediscoverDelayMs = Math.Min(_rediscoverDelayMs * 2, RediscoverMaxMs);
    }

    private void LoseStage()
    {
        Trace.WriteLine("[x11] shell stage gone, rediscovering");
        _stages = Array.Empty<IntPtr>();
        _stageLost = false;
        _regionDirty = false;
        _rediscoverDelayMs = RediscoverInitialMs;
        ScheduleRediscovery();
        // While the shell restarts it takes no clicks. Remembered dock zones
        // live in DockZoneTracker, not here.
        Publish(Array.Empty<PixelRect>());
    }

    private void HandleEvent(IntPtr ev)
    {
        var type = Marshal.ReadInt32(ev, Xlib.EvType);
        if (type == _shapeEventBase + XShape.ShapeNotify)
        {
            if (Marshal.ReadInt32(ev, Xlib.EvShapeKind) == XShape.ShapeInput && IsStage(Marshal.ReadIntPtr(ev, Xlib.EvWindow)))
                _regionDirty = true;
        }
        else if (type == Xlib.DestroyNotify)
        {
            if (IsStage(Marshal.ReadIntPtr(ev, Xlib.EvDestroyWindow))) _stageLost = true;
        }
        else if (type == Xlib.ConfigureNotify)
        {
            // The stage moves or resizes with monitor layout changes, and the
            // shape is relative to the stage origin.
            if (IsStage(Marshal.ReadIntPtr(ev, Xlib.EvConfigureWindow))) _regionDirty = true;
        }
        else if (type == Xlib.ClientMessage)
        {
            if (Marshal.ReadIntPtr(ev, Xlib.EvClientMessageType) == _managerAtom
                && Marshal.ReadIntPtr(ev, Xlib.EvClientData + IntPtr.Size) == _cmSelection
                && _stages.Length == 0)
            {
                _rediscoverDelayMs = RediscoverInitialMs;
                _nextDiscoveryAt = 0;
            }
        }
    }

    private bool IsStage(IntPtr window) => Array.IndexOf(_stages, window) >= 0;

    private void ReadRegion()
    {
        _errorSeen = false;
        var rects = new List<PixelRect>();
        foreach (var stage in _stages)
        {
            Xlib.XTranslateCoordinates(_display, stage, _root, 0, 0, out var originX, out var originY, out _);
            var list = XShape.XShapeGetRectangles(_display, stage, XShape.ShapeInput, out var count, out _);
            if (list == IntPtr.Zero) continue;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var r = Marshal.PtrToStructure<XRectangle>(list + i * Marshal.SizeOf<XRectangle>());
                    rects.Add(new PixelRect(originX + r.x, originY + r.y, r.width, r.height));
                }
            }
            finally
            {
                Xlib.XFree(list);
            }
        }

        // An error here almost always means the stage died between the event
        // and our query. A half-read region would wrongly report "no dock".
        if (_errorSeen)
        {
            _stageLost = true;
            return;
        }
        Publish(rects);
    }

    private void OnXError(X11Error error)
    {
        _errorSeen = true;
        Trace.WriteLine($"[x11] X error {error.ErrorCode} (request {error.RequestCode}) on 0x{error.ResourceId:x}");
        if (error.ErrorCode == Xlib.BadWindow && IsStage(error.ResourceId)) _stageLost = true;
    }

    private void Publish(IReadOnlyList<PixelRect> rects)
    {
        if (rects.SequenceEqual(_current)) return;
        _current = Array.AsReadOnly(rects.ToArray());
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            // A subscriber bug must not stop the watch.
            Trace.WriteLine($"[x11] shell region Changed handler threw: {e}");
        }
    }
}
