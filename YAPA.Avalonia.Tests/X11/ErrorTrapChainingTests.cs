using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Platform.Linux.X11;

namespace YAPA.Avalonia.Tests.X11;

[TestClass]
public class ErrorTrapChainingTests
{
    // Same signature as ours but a different delegate type, which is what
    // Avalonia's own X11 backend installs before we do.
    private delegate int ForeignHandler(IntPtr display, IntPtr errorEvent);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XSetErrorHandler(IntPtr handler);

    private static readonly ForeignHandler Foreign = (_, _) => 0;

    [TestMethod]
    public void Registering_on_top_of_another_managed_handler_does_not_throw()
    {
        IntPtr original;
        try
        {
            original = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(Foreign));
        }
        catch (DllNotFoundException)
        {
            Assert.Inconclusive("libX11 not installed");
            return;
        }

        try
        {
            // Regression: this threw InvalidCastException inside the real app
            // and took the shell-region monitor down with it.
            X11ErrorTrap.Register(new IntPtr(0x1234), _ => { });
            X11ErrorTrap.Unregister(new IntPtr(0x1234));
        }
        finally
        {
            XSetErrorHandler(original);
        }
    }
}
