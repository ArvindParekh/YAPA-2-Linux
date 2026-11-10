using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Ambience;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Settings;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Tests.AppLogic;

namespace YAPA.Avalonia.Tests.Audio;

internal sealed class FakeMusicPlayer : IBackgroundMusicPlayer
{
    public readonly List<string> Calls = new();
    public bool IsPlaying { get; private set; }

    public void Play(string path, bool loop, double volume)
    {
        Calls.Add($"play {path}{(loop ? " (loop)" : "")}");
        IsPlaying = true;
    }

    public void Stop()
    {
        Calls.Add("stop");
        IsPlaying = false;
    }

    public void Dispose() { }
}

/// <summary>
/// Settings apply immediately, so mid-pomodoro the Sounds page is changing what you hear,
/// and the audition buttons must say so instead of offering to play it again.
/// </summary>
[TestClass]
public class LiveSoundTests
{
    private static (Rig Rig, AvaloniaMusicPlayerSettings Music, FakeMusicPlayer Player, SoundEffects Effects) Music()
    {
        var rig = new Rig();
        var music = new AvaloniaMusicPlayerSettings(rig.Settings);
        var player = new FakeMusicPlayer();
        var effects = new SoundEffects(rig.Engine, rig.EngineSettings, new SoundSettings(rig.Settings), music, rig.Settings,
            AudioFactory.CreateCuePlayer(), player);
        return (rig, music, player, effects);
    }

    [TestMethod]
    public void Music_chosen_mid_pomodoro_starts_playing_now()
    {
        var (rig, music, player, effects) = Music();
        rig.Engine.Start();
        Assert.IsFalse(player.IsPlaying);

        music.WorkSong = "radio:fluid";
        Assert.AreEqual("play https://ice1.somafm.com/fluid-128-mp3 (loop)", player.Calls.Last());
        Assert.AreEqual(MusicSlot.Work, effects.LiveSlot);

        music.WorkSong = null;
        Assert.IsFalse(player.IsPlaying);
        Assert.IsNull(effects.LiveSlot);
    }

    [TestMethod]
    public void Changing_another_phases_music_leaves_the_current_one_alone()
    {
        var (rig, music, player, _) = Music();
        music.WorkSong = "radio:fluid";
        rig.Engine.Start();
        var calls = player.Calls.Count;

        music.BreakSong = "radio:drone-zone";
        music.RepeatBreakSong = true;
        Assert.AreEqual(calls, player.Calls.Count, "the station must not reconnect over an unrelated change");
    }

    [TestMethod]
    public void Turning_sounds_off_and_on_stops_and_restarts_the_music()
    {
        var (rig, music, player, _) = Music();
        music.WorkSong = "radio:fluid";
        rig.Engine.Start();

        rig.EngineSettings.DisableSoundNotifications = true;
        Assert.IsFalse(player.IsPlaying);
        rig.EngineSettings.DisableSoundNotifications = false;
        Assert.IsTrue(player.IsPlaying);
    }

    [TestMethod]
    public void The_music_row_for_the_live_phase_says_it_is_playing()
    {
        var (rig, music, _, effects) = Music();
        music.WorkSong = "radio:fluid";
        music.BreakSong = "radio:drone-zone";
        MusicRowViewModel Row(MusicSlot slot, System.Func<string?> get) => new(slot, slot.ToString(), get, _ => { }, () => true, _ => { }, () => null, effects);
        var work = Row(MusicSlot.Work, () => music.WorkSong);
        var breaks = Row(MusicSlot.Break, () => music.BreakSong);
        Assert.AreEqual("▶ Listen", work.PreviewLabel, "nothing is playing before the timer starts");

        var changes = new List<string?>();
        work.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        rig.Engine.Start();

        Assert.AreEqual("Playing now", work.PreviewLabel);
        Assert.IsFalse(work.CanPreview);
        CollectionAssert.Contains(changes, nameof(MusicRowViewModel.PreviewLabel), "the button updates while the page is open");
        Assert.AreEqual("▶ Listen", breaks.PreviewLabel, "the break's music can still be auditioned");
        Assert.IsTrue(breaks.CanPreview);

        work.Detach();
        breaks.Detach();
    }

    [TestMethod]
    public void The_ambience_button_says_playing_during_a_pomodoro_and_try_it_otherwise()
    {
        var rig = new Rig();
        var ambience = new AmbienceSettings(rig.Settings) { Sound = AmbientSound.SteadyRain };
        var effects = new AmbienceEffects(rig.Engine, ambience, rig.EngineSettings, new FakeAmbiencePlayer(), rig.Settings, rig.Clock);
        var section = new AmbienceSectionViewModel(ambience, effects, () => null);

        Assert.AreEqual("▶ Try it", section.PreviewLabel);
        Assert.IsTrue(section.CanPreview);

        var changes = new List<string?>();
        section.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        rig.Engine.Start();
        Assert.AreEqual("Playing now", section.PreviewLabel);
        Assert.IsFalse(section.CanPreview);
        CollectionAssert.Contains(changes, nameof(AmbienceSectionViewModel.PreviewLabel));

        rig.Engine.Pause();
        Assert.AreEqual("▶ Try it", section.PreviewLabel);
        section.Detach();
    }
}
