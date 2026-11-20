using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Shared.Common;

namespace YAPA.Avalonia.Tests.AppLogic;

/// <summary>Profiles from before the redesign hold the classic timer as their theme: they move to Studio, once.</summary>
[TestClass]
public class ThemeMigrationTests
{
    private static AvaloniaThemeSettings Open(MemoryEnvironment env)
        => new(new JsonYapaSettings(env, new NewtonsoftJson()));

    [TestMethod]
    public void A_classic_profile_moves_to_studio()
    {
        var env = new MemoryEnvironment { Settings = """{"ThemeManager":{"ActiveTheme":"YAPA 1.0"}}""" };
        Assert.AreEqual(AppNavigator.StudioTheme, Open(env).ActiveTheme);
    }

    [TestMethod]
    public void Choosing_classic_afterwards_sticks()
    {
        var env = new MemoryEnvironment { Settings = """{"ThemeManager":{"ActiveTheme":"YAPA 1.0"}}""" };
        Open(env).ActiveTheme = AppNavigator.ClassicTheme;
        Assert.AreEqual(AppNavigator.ClassicTheme, Open(env).ActiveTheme);
    }

    [TestMethod]
    public void A_fresh_profile_chooses_classic_and_keeps_it()
    {
        var env = new MemoryEnvironment();
        Open(env).ActiveTheme = AppNavigator.ClassicTheme;
        Assert.AreEqual(AppNavigator.ClassicTheme, Open(env).ActiveTheme);
    }

    [TestMethod]
    public void Motivational_is_left_alone()
    {
        var env = new MemoryEnvironment { Settings = """{"ThemeManager":{"ActiveTheme":"Motivational"}}""" };
        Assert.AreEqual(AppNavigator.MotivationalTheme, Open(env).ActiveTheme);
    }

    [TestMethod]
    public void Nothing_is_written_for_a_profile_without_a_stored_theme()
    {
        var env = new MemoryEnvironment();
        _ = Open(env).ActiveTheme;
        Assert.AreEqual("{}", env.Settings);
    }
}
