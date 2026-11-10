using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Library;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Settings;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Tests.AppLogic;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Tests.Audio;

[TestClass]
[DoNotParallelize] // shares the (isolated) Sounds folder
public class MediaLibraryTests
{
    private string _dropped = "";

    [TestInitialize]
    public void Setup()
    {
        Directory.CreateDirectory(MediaLibrary.Folder);
        _dropped = Path.Combine(MediaLibrary.Folder, $"enrico-rain_{Guid.NewGuid():N}.opus");
        File.WriteAllBytes(_dropped, new byte[16]);
    }

    [TestCleanup]
    public void Cleanup() => File.Delete(_dropped);

    [TestMethod]
    public void The_sounds_folder_lives_under_the_isolated_home()
        => StringAssert.StartsWith(MediaLibrary.Folder, TestAssembly.Home);

    [TestMethod]
    public void Files_dropped_in_the_sounds_folder_are_listed_by_name()
    {
        File.WriteAllText(Path.Combine(MediaLibrary.Folder, "notes.txt"), "not audio");
        try
        {
            var items = MediaLibrary.LibraryFiles();
            var rain = items.Single(i => i.Location == _dropped);
            Assert.AreEqual(MediaKind.Library, rain.Kind);
            StringAssert.StartsWith(rain.Name, "Enrico rain ");
            Assert.IsFalse(items.Any(i => i.Location.EndsWith(".txt")));
        }
        finally
        {
            File.Delete(Path.Combine(MediaLibrary.Folder, "notes.txt"));
        }
    }

    [TestMethod]
    public void Names_read_like_titles()
    {
        Assert.AreEqual("Enrico rain", MediaLibrary.NameFor("/x/enrico-rain.opus"));
        Assert.AreEqual("Coffee shop morning", MediaLibrary.NameFor("/x/coffee_shop -- morning.mp3"));
        Assert.AreEqual("---.ogg", MediaLibrary.NameFor("/x/---.ogg"));
    }

    [TestMethod]
    public void References_resolve_to_something_playable_or_to_nothing()
    {
        var radio = MediaLibrary.Resolve("radio:fluid");
        Assert.AreEqual(MediaKind.Radio, radio!.Kind);
        StringAssert.StartsWith(radio.Location, "https://");

        Assert.IsNull(MediaLibrary.Resolve("radio:gone-off-air"));
        Assert.IsNull(MediaLibrary.Resolve(null));
        Assert.IsNull(MediaLibrary.Resolve("/nowhere/missing.mp3"));

        Assert.AreEqual(MediaKind.Library, MediaLibrary.Resolve(_dropped)!.Kind);
        Assert.AreEqual(MediaKind.Library, MediaLibrary.Resolve("file:" + _dropped)!.Kind);

        // A plain path is what older versions stored; it must keep working.
        var elsewhere = Path.Combine(Path.GetTempPath(), $"yapa-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(elsewhere, new byte[4]);
        try
        {
            Assert.AreEqual(MediaKind.File, MediaLibrary.Resolve(elsewhere)!.Kind);
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }

    [TestMethod]
    public void Every_station_is_https_and_has_a_unique_reference()
    {
        Assert.IsTrue(MediaLibrary.Radio.All(r => r.Location.StartsWith("https://")));
        Assert.AreEqual(MediaLibrary.Radio.Count, MediaLibrary.Radio.Select(r => r.Reference).Distinct().Count());
    }

    private static (MusicRowViewModel Row, AvaloniaMusicPlayerSettings Music) Row()
    {
        var settings = new JsonYapaSettings(new MemoryEnvironment(), new NewtonsoftJson());
        var music = new AvaloniaMusicPlayerSettings(settings);
        var effects = new SoundEffects(Substitute.For<IPomodoroEngine>(), new PomodoroEngineSettings(settings), new SoundSettings(settings), music, settings);
        var row = new MusicRowViewModel(MusicSlot.Work, "During pomodoros", () => music.WorkSong, v => music.WorkSong = v,
            () => music.RepeatWorkSong, v => music.RepeatWorkSong = v, () => null, effects);
        return (row, music);
    }

    [TestMethod]
    public void Music_can_be_chosen_without_a_file_dialog()
    {
        var (row, music) = Row();
        var labels = row.Options.Select(o => o.Label).ToList();

        Assert.AreEqual("None", labels[0]);
        Assert.AreEqual("Choose a file…", labels[^1], "the file dialog is the fallback, last");
        Assert.IsTrue(labels.Contains("Radio · Fluid"));
        Assert.IsTrue(labels.Any(l => l.StartsWith("Sounds · Enrico rain")));

        row.SelectedIndex = labels.IndexOf("Radio · Fluid");
        Assert.AreEqual("radio:fluid", music.WorkSong);
        Assert.IsFalse(row.IsFile, "no Loop box for a station");

        row.SelectedIndex = labels.FindIndex(l => l.StartsWith("Sounds · Enrico rain"));
        Assert.AreEqual(_dropped, music.WorkSong);
        Assert.IsTrue(row.IsFile);
        Assert.IsTrue(row.Repeat, "a recording keeps playing for the whole phase");
        Assert.AreEqual("From your Sounds folder", row.Description);

        row.SelectedIndex = 0;
        Assert.IsNull(music.WorkSong);
    }

    [TestMethod]
    public void The_options_list_is_stable_until_it_really_changes()
    {
        var (row, music) = Row();
        var before = row.Options;
        row.SelectedIndex = 1;
        row.Rebuild();
        Assert.AreSame(before, row.Options, "an unchanged rescan must not hand the ComboBox a new list");

        var another = Path.Combine(MediaLibrary.Folder, $"brook_{Guid.NewGuid():N}.ogg");
        File.WriteAllBytes(another, new byte[4]);
        try
        {
            row.Rebuild();
            Assert.AreNotSame(before, row.Options);
            Assert.AreEqual("radio:fluid", music.WorkSong, "a rescan never changes the choice");
            Assert.AreEqual(1, row.SelectedIndex);
        }
        finally
        {
            File.Delete(another);
        }
    }

    [TestMethod]
    public void A_file_chosen_from_elsewhere_keeps_its_place_in_the_list()
    {
        var (row, music) = Row();
        var elsewhere = Path.Combine(Path.GetTempPath(), $"yapa-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(elsewhere, new byte[4]);
        try
        {
            music.WorkSong = elsewhere; // as an older version, or the file dialog, would store it
            row.Rebuild();
            var selected = row.Options[row.SelectedIndex];
            Assert.AreEqual(elsewhere, selected.Reference);
            StringAssert.StartsWith(selected.Label, "File · ");
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }
}
