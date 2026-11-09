using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Platform.Linux.X11;

namespace YAPA.Avalonia.Tests.X11;

/// <summary>
/// Live against the current X server; opt in with YAPA_X11_TESTS=1. The
/// show-desktop behaviour itself was verified on a private nested GNOME Shell
/// (Xvfb + gnome-shell --x11): see X11DesktopAnchor for the Mutter rules.
/// </summary>
[TestClass]
public class DesktopAnchorLiveTests
{
    [TestMethod]
    [TestCategory("X11Live")]
    public void Anchor_is_an_offscreen_dock_and_attach_sets_transient_for()
    {
        if (Environment.GetEnvironmentVariable("YAPA_X11_TESTS") != "1") Assert.Inconclusive("set YAPA_X11_TESTS=1");

        using var anchor = X11DesktopAnchor.TryCreate();
        Assert.IsNotNull(anchor, "no X server");

        var tree = Run("xwininfo", "-root -tree");
        var line = Array.Find(tree.Split('\n'), l => l.Contains("YAPA 2 desktop anchor"));
        Assert.IsNotNull(line, "anchor window not found");
        StringAssert.Contains(line, "1x1+-64+-64");

        var anchorId = line!.Trim().Split(' ')[0];
        StringAssert.Contains(Run("xprop", $"-id {anchorId} _NET_WM_WINDOW_TYPE"), "_NET_WM_WINDOW_TYPE_DOCK");
    }

    private static string Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
