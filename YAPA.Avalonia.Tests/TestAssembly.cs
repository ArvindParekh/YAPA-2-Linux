using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The tests read /proc, talk to X11 and set Unix file modes: they run on Linux, like the app's reference desktop.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace YAPA.Avalonia.Tests;

[TestClass]
public static class TestAssembly
{
    public static string Home { get; private set; } = "";

    /// <summary>
    /// AppPaths reads YAPA_HOME once, on first use. Setting it here, before any
    /// test runs, guarantees nothing in this assembly can ever touch the real
    /// ~/.config/YAPA2 or ~/.local/share/YAPA2.
    /// </summary>
    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Home = Path.Combine(Path.GetTempPath(), "yapa-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("YAPA_HOME", Home);
        // The UI tests click through real settings, which preview sounds: keep them silent.
        Environment.SetEnvironmentVariable("YAPA_SILENT_AUDIO", "1");
        // No test reaches the real claude (the user's plan, their transcripts): tests that need one bring a fake.
        if (Environment.GetEnvironmentVariable(YAPA.Avalonia.Assistant.ClaudeCli.OverrideVariable) is not { Length: > 0 })
            Environment.SetEnvironmentVariable(YAPA.Avalonia.Assistant.ClaudeCli.OverrideVariable, "/nonexistent/claude");
        if (!YAPA.Avalonia.Platform.AppPaths.IsIsolated || !YAPA.Avalonia.Platform.AppPaths.DataDirectory.StartsWith(Home))
            throw new InvalidOperationException("AppPaths was initialised before the test sandbox; refusing to run against real user data.");
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(Home, recursive: true); } catch { /* best effort */ }
    }
}
