using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;

namespace YAPA.Avalonia.Tests.Audio;

[TestClass]
public class SoundRefTests
{
    [TestMethod]
    public void EveryBuiltIn_RoundTrips()
    {
        foreach (var sound in Enum.GetValues<BuiltInSound>())
        {
            var reference = SoundRef.BuiltIn(sound);
            var text = reference.Serialize();
            StringAssert.StartsWith(text, "builtin:");
            Assert.AreEqual(reference, SoundRef.Parse(text), text);
            Assert.AreEqual(sound, SoundRef.Parse(text).AsBuiltIn);
        }
    }

    [TestMethod]
    public void SerializedNames_AreStableKebabCase()
    {
        // These strings live in users' settings files; changing one is a breaking change.
        Assert.AreEqual("builtin:classic-tick", SoundRef.BuiltIn(BuiltInSound.ClassicTick).Serialize());
        Assert.AreEqual("builtin:classic-ding", SoundRef.BuiltIn(BuiltInSound.ClassicDing).Serialize());
        Assert.AreEqual("builtin:chime-up", SoundRef.BuiltIn(BuiltInSound.ChimeUp).Serialize());
        Assert.AreEqual("builtin:chime-down", SoundRef.BuiltIn(BuiltInSound.ChimeDown).Serialize());
        Assert.AreEqual("builtin:soft-tap", SoundRef.BuiltIn(BuiltInSound.SoftTap).Serialize());
        Assert.AreEqual("builtin:bell", SoundRef.BuiltIn(BuiltInSound.Bell).Serialize());
        Assert.AreEqual("builtin:bright-bell", SoundRef.BuiltIn(BuiltInSound.BrightBell).Serialize());
    }

    [TestMethod]
    public void FileRef_RoundTripsVerbatim()
    {
        foreach (var path in new[] { "/home/me/Music/gong.ogg", "/tmp/with space/ünïcödé:colon.wav", "relative/thing.mp3" })
        {
            var reference = SoundRef.File(path);
            Assert.AreEqual(SoundRefKind.File, reference.Kind);
            Assert.AreEqual("file:" + path, reference.Serialize());
            Assert.AreEqual(reference, SoundRef.Parse(reference.Serialize()));
            Assert.AreEqual(path, SoundRef.Parse(reference.Serialize()).FilePath);
        }
    }

    [TestMethod]
    public void None_RoundTripsAsEmpty()
    {
        Assert.IsTrue(SoundRef.None.IsNone);
        Assert.IsTrue(default(SoundRef).IsNone);
        Assert.AreEqual("", SoundRef.None.Serialize());
        Assert.AreEqual(SoundRef.None, SoundRef.Parse(""));
        Assert.AreEqual("None", SoundRef.None.DisplayName);
        Assert.IsTrue(SoundRef.File("  ").IsNone);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("   ")]
    [DataRow("builtin:")]
    [DataRow("builtin:air-horn")]
    [DataRow("file:")]
    [DataRow("tick.wav")]
    [DataRow("Assets/tick.wav")]
    [DataRow("garbage")]
    [DataRow("{\"json\":true}")]
    [DataRow("http://example.com/a.wav")]
    public void Garbage_ParsesToNone(string? text)
        => Assert.IsTrue(SoundRef.Parse(text).IsNone, text);

    [TestMethod]
    public void Parse_IsCaseAndWhitespaceTolerant()
    {
        Assert.AreEqual(SoundRef.BuiltIn(BuiltInSound.BrightBell), SoundRef.Parse("  BuiltIn:Bright-Bell "));
    }

    [DataTestMethod]
    [DataRow("/usr/local/lib/yapa2/Assets/tick.wav", BuiltInSound.ClassicTick)]
    [DataRow("/home/me/.local/lib/yapa2/Assets/ding.wav", BuiltInSound.ClassicDing)]
    [DataRow("/home/me/src/YAPA.Avalonia/bin/Debug/net8.0/assets/TICK.WAV", BuiltInSound.ClassicTick)]
    [DataRow(@"C:\Program Files\YAPA 2\Assets\ding.wav", BuiltInSound.ClassicDing)]
    [DataRow("file:/opt/yapa/Assets/tick.wav", BuiltInSound.ClassicTick)]
    public void LegacyShippedAssetPaths_MapToClassicBuiltIns(string legacy, BuiltInSound expected)
        => Assert.AreEqual(SoundRef.BuiltIn(expected), SoundRef.Parse(legacy));

    [TestMethod]
    public void LegacyBareAbsolutePath_IsFileRef()
    {
        var parsed = SoundRef.Parse("/home/me/Music/my bell.wav");
        Assert.AreEqual(SoundRef.File("/home/me/Music/my bell.wav"), parsed);
        Assert.AreEqual("file:/home/me/Music/my bell.wav", parsed.Serialize());
        Assert.AreEqual("my bell.wav", parsed.DisplayName);
    }

    [TestMethod]
    public void FileUri_IsAccepted()
        => Assert.AreEqual("/home/me/a b.wav", SoundRef.Parse("file:///home/me/a%20b.wav").FilePath);

    [TestMethod]
    public void DisplayNames()
    {
        Assert.AreEqual("Chime up", SoundRef.BuiltIn(BuiltInSound.ChimeUp).DisplayName);
        Assert.AreEqual("ding.wav", SoundRef.File(@"C:\sounds\ding.wav").DisplayName);
    }

    [TestMethod]
    public void UndefinedEnumValue_IsNone()
        => Assert.IsTrue(SoundRef.BuiltIn((BuiltInSound)999).IsNone);

    [TestMethod]
    public void BuiltIns_ListsEverySound()
        => Assert.AreEqual(Enum.GetValues<BuiltInSound>().Length, SoundRef.BuiltIns.Count);
}
