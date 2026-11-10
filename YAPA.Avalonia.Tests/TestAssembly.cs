using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        if (!YAPA.Avalonia.Platform.AppPaths.IsIsolated || !YAPA.Avalonia.Platform.AppPaths.DataDirectory.StartsWith(Home))
            throw new InvalidOperationException("AppPaths was initialised before the test sandbox; refusing to run against real user data.");
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(Home, recursive: true); } catch { /* best effort */ }
    }
}
