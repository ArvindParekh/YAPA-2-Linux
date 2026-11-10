using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Bootstrap;
using YAPA.Avalonia.Settings;
using YAPA.Avalonia.Windows;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Tests.Ui;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Builds every window against an isolated profile on Avalonia's headless
/// platform with real Skia rendering. Catches XAML/binding/DI breakage that
/// only shows up at runtime. Set YAPA_UI_RENDER_DIR to also get PNGs of each
/// screen for visual review.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UiSmokeTests
{
    private static HeadlessUnitTestSession? _session;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        // YAPA_HOME is already pointed at a sandbox by TestAssembly.
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        _session.Dispatch(() => { App.Bootstrapper = new AppBootstrapper(); }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _session?.Dispatch(() => App.Bootstrapper?.Dispose(), CancellationToken.None).GetAwaiter().GetResult();
        _session?.Dispose();
    }

    private static void OnUi(Action action) => _session!.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    private static void Render(TopLevel window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.IsNotNull(frame, $"{name} did not render");
        if (Environment.GetEnvironmentVariable("YAPA_UI_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, name + ".png"));
        }
    }

    [TestMethod]
    public void Classic_timer_renders_in_every_phase() => OnUi(() =>
    {
        var engine = App.Bootstrapper!.Resolve<IPomodoroEngine>();
        var window = new MainWindow();
        window.Show();
        Render(window, "timer-classic-ready");
        engine.Start();
        Render(window, "timer-classic-work");
        engine.Pause();
        Render(window, "timer-classic-paused");
        engine.Stop();
        window.Close();
    });

    [TestMethod]
    public void Motivational_timer_renders() => OnUi(() =>
    {
        var window = new MotivationalWindow();
        window.Show();
        Render(window, "timer-motivational");
        window.Close();
    });

    [TestMethod]
    public void Every_settings_page_renders() => OnUi(() =>
    {
        var window = new SettingsWindow();
        window.Show();
        foreach (var page in window.Pages)
        {
            window.SelectedPage = page;
            Render(window, "settings-" + page.Title.ToLowerInvariant());
        }
        window.Close();
    });

    [TestMethod]
    public void Sounds_page_with_ambience_renders() => OnUi(() =>
    {
        var ambience = App.Bootstrapper!.Resolve<YAPA.Avalonia.Audio.Ambience.AmbienceSettings>();
        ambience.Sound = YAPA.Avalonia.Audio.Ambience.AmbientSound.SteadyRain;
        ambience.Ticking = true;
        var window = new SettingsWindow { Height = 1500 };
        window.Show();
        window.Navigate(YAPA.Avalonia.Services.SettingsPage.Sounds);
        Render(window, "settings-sounds-ambience");
        window.Close();
        ambience.ResetToDefaults();
    });

    /// <summary>
    /// Drives every dropdown, switch, slider and number box on every settings page through
    /// the real controls and bindings, the way a user would. A binding feedback loop (like
    /// the one that crashed the app when choosing a background sound) recurses until the
    /// stack overflows; that can't be caught, so it takes the test run down. Loud and clear.
    /// </summary>
    [TestMethod]
    public void Every_settings_control_can_be_operated() => OnUi(() =>
    {
        var window = new SettingsWindow();
        window.Show();
        try
        {
            foreach (var page in window.Pages)
            {
                window.SelectedPage = page;
                Dispatcher.UIThread.RunJobs();

                var controls = window.GetVisualDescendants().ToList();
                foreach (var combo in controls.OfType<ComboBox>())
                {
                    var count = combo.ItemCount;
                    var original = combo.SelectedIndex;
                    for (var i = 0; i < count; i++)
                    {
                        combo.SelectedIndex = i;
                        Dispatcher.UIThread.RunJobs();
                    }
                    combo.SelectedIndex = original;
                    Dispatcher.UIThread.RunJobs();
                }

                foreach (var toggle in window.GetVisualDescendants().OfType<ToggleSwitch>().ToList())
                {
                    var original = toggle.IsChecked;
                    toggle.IsChecked = !original;
                    Dispatcher.UIThread.RunJobs();
                    toggle.IsChecked = original;
                    Dispatcher.UIThread.RunJobs();
                }

                foreach (var slider in window.GetVisualDescendants().OfType<Slider>().ToList())
                {
                    var original = slider.Value;
                    slider.Value = slider.Minimum;
                    slider.Value = slider.Maximum;
                    slider.Value = original;
                    Dispatcher.UIThread.RunJobs();
                }

                foreach (var number in window.GetVisualDescendants().OfType<NumericUpDown>().ToList())
                {
                    var original = number.Value;
                    number.Value = number.Maximum;
                    number.Value = number.Minimum;
                    number.Value = original;
                    Dispatcher.UIThread.RunJobs();
                }

                page.Reset();
                Dispatcher.UIThread.RunJobs();
            }

            // The specific crash: choosing a background sound.
            var ambience = App.Bootstrapper!.Resolve<YAPA.Avalonia.Audio.Ambience.AmbienceSettings>();
            window.Navigate(YAPA.Avalonia.Services.SettingsPage.Sounds);
            Dispatcher.UIThread.RunJobs();
            var picker = window.GetVisualDescendants().OfType<ComboBox>()
                .First(c => c.Items.Cast<object>().Any(o => o.ToString() == "Steady rain"));
            picker.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(YAPA.Avalonia.Audio.Ambience.AmbientSound.SteadyRain, ambience.Sound);
            Assert.AreEqual(2, picker.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    });

    [TestMethod]
    public void Statistics_renders() => OnUi(() =>
    {
        var window = new StatisticsWindow();
        window.Show();
        Thread.Sleep(300); // statistics load off the UI thread
        Dispatcher.UIThread.RunJobs();
        Render(window, "statistics");
        window.Close();
    });

    [TestMethod]
    public void Tray_toggle_restores_a_minimized_timer_in_one_click() => OnUi(() =>
    {
        // Regression: after Show Desktop / minimize the window still reported
        // IsVisible, so the first toggle hid it and only the second showed it.
        var navigator = App.Bootstrapper!.Resolve<YAPA.Avalonia.Services.AppNavigator>();
        navigator.ShowTimer();
        var window = navigator.TimerWindow!;
        window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(navigator.IsTimerShowing);

        navigator.ToggleTimerVisibility();
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(navigator.IsTimerShowing);
        Assert.AreEqual(WindowState.Normal, window.WindowState);

        navigator.ToggleTimerVisibility();
        Assert.IsFalse(window.IsVisible);
    });

    [TestMethod]
    public void Right_click_opens_settings() => OnUi(() =>
    {
        var bs = App.Bootstrapper!;
        var engine = bs.Resolve<IPomodoroEngine>();
        engine.Reset();
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Right-click on the digits area.
        window.MouseDown(new Point(60, 50), global::Avalonia.Input.MouseButton.Right);
        window.MouseUp(new Point(60, 50), global::Avalonia.Input.MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        var navigator = bs.Resolve<YAPA.Avalonia.Services.AppNavigator>();
        Assert.IsNotNull(navigator.SettingsWindowForTests, "right-click should open settings");
        navigator.SettingsWindowForTests!.Close();
        Dispatcher.UIThread.RunJobs();

        // The ⋯ actions menu is a native popup, which the headless platform can't
        // create; it is exercised on a real X server instead.
        Assert.IsNotNull(window.FindControl<Button>("MenuButton"));
        window.Close();
    });

    [TestMethod]
    public void Actions_menu_gets_the_context_menu_template() => OnUi(() =>
    {
        // Regression: the ContextMenu subclass matched no control theme, so it
        // opened as an empty popup that never appeared.
        var menu = new TimerContextMenu();
        var host = new Window { Content = menu };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.IsNotNull(menu.Template, "TimerContextMenu must pick up the Fluent ContextMenu template");
        host.Close();
    });

    [TestMethod]
    public void L_takes_the_long_break_now() => OnUi(() =>
    {
        var engine = App.Bootstrapper!.Resolve<IPomodoroEngine>();
        engine.Reset();
        var window = new MainWindow();
        window.Show();
        window.KeyPress(global::Avalonia.Input.Key.L, global::Avalonia.Input.RawInputModifiers.None,
            global::Avalonia.Input.PhysicalKey.L, "l");
        Assert.AreEqual(PomodoroPhase.Break, engine.Phase);
        Assert.IsTrue(engine.IsLongBreak);
        Render(window, "timer-classic-long-break");
        engine.Reset();
        window.Close();
    });

    [TestMethod]
    public void Keyboard_space_toggles_the_timer() => OnUi(() =>
    {
        var engine = App.Bootstrapper!.Resolve<IPomodoroEngine>();
        var window = new MainWindow();
        window.Show();
        window.KeyPress(global::Avalonia.Input.Key.Space, global::Avalonia.Input.RawInputModifiers.None,
            global::Avalonia.Input.PhysicalKey.Space, " ");
        Assert.AreEqual(PomodoroPhase.Work, engine.Phase);
        window.KeyPress(global::Avalonia.Input.Key.Space, global::Avalonia.Input.RawInputModifiers.None,
            global::Avalonia.Input.PhysicalKey.Space, " ");
        Assert.AreEqual(PomodoroPhase.Pause, engine.Phase);
        window.KeyPress(global::Avalonia.Input.Key.Escape, global::Avalonia.Input.RawInputModifiers.None,
            global::Avalonia.Input.PhysicalKey.Escape, null);
        Assert.AreEqual(PomodoroPhase.NotStarted, engine.Phase);
        window.Close();
    });
}
