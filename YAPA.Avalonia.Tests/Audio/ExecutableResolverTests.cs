using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;

namespace YAPA.Avalonia.Tests.Audio;

[TestClass]
public class ExecutableResolverTests
{
    [TestMethod]
    public void ReturnsFirstMatchInDirectoryOrder()
    {
        var present = new HashSet<string> { "/opt/b/pw-play", "/opt/c/pw-play" };
        var resolver = new ExecutableResolver(new[] { "/opt/a", "/opt/b", "/opt/c" }, present.Contains);

        Assert.AreEqual("/opt/b/pw-play", resolver.Resolve("pw-play"));
        Assert.IsNull(resolver.Resolve("ffplay"));
    }

    [TestMethod]
    public void CachesHitsAndMisses()
    {
        var probes = new List<string>();
        var resolver = new ExecutableResolver(new[] { "/a", "/b" }, p => { probes.Add(p); return p == "/b/paplay"; });

        resolver.Resolve("paplay");
        resolver.Resolve("paplay");
        resolver.Resolve("aplay");
        resolver.Resolve("aplay");

        Assert.AreEqual(4, probes.Count, string.Join(", ", probes));
    }

    [TestMethod]
    public void IgnoresRelativeAndEmptyDirectories()
    {
        var resolver = new ExecutableResolver(new[] { "", ".", "bin", "/usr/bin", "/usr/bin" }, _ => false);
        CollectionAssert.AreEqual(new[] { "/usr/bin" }, resolver.SearchDirectories.ToArray());
    }

    [TestMethod]
    public void SearchPath_AppendsLocationsMissingFromDesktopSessions()
    {
        var dirs = ExecutableResolver.BuildSearchPath("/usr/bin:/snap/bin", "/home/me");

        Assert.AreEqual("/usr/bin", dirs[0], "PATH comes first");
        Assert.AreEqual("/snap/bin", dirs[1]);
        CollectionAssert.Contains(dirs.ToArray(), "/home/linuxbrew/.linuxbrew/bin");
        CollectionAssert.Contains(dirs.ToArray(), "/home/me/.local/bin");
        CollectionAssert.Contains(dirs.ToArray(), "/usr/local/bin");
        CollectionAssert.Contains(dirs.ToArray(), "/bin");
        Assert.AreEqual(dirs.Count, dirs.Distinct().Count(), "no duplicates");
    }

    [TestMethod]
    public void SearchPath_WorksWithoutPath()
    {
        var dirs = ExecutableResolver.BuildSearchPath(null, "/home/me");
        CollectionAssert.Contains(dirs.ToArray(), "/usr/bin");
    }

    [TestMethod]
    public void FindsBrewOnlyToolWhenPathIsMinimal()
    {
        // The real-world failure: GNOME's app-grid PATH has no linuxbrew, where paplay lives.
        var dirs = ExecutableResolver.BuildSearchPath("/usr/bin:/bin", "/home/me");
        var resolver = new ExecutableResolver(dirs, p => p == "/home/linuxbrew/.linuxbrew/bin/paplay");

        Assert.AreEqual("/home/linuxbrew/.linuxbrew/bin/paplay", resolver.Resolve("paplay"));
    }

    [TestMethod]
    public void RealFileSystemCheck_RequiresExecuteBit()
    {
        Assert.IsTrue(ExecutableResolver.IsExecutableFile("/bin/sh"));
        Assert.IsFalse(ExecutableResolver.IsExecutableFile("/etc/hostname"));
        Assert.IsFalse(ExecutableResolver.IsExecutableFile("/usr/bin"), "directories are not executables");
        Assert.IsFalse(ExecutableResolver.IsExecutableFile("/definitely/not/here"));
    }
}
