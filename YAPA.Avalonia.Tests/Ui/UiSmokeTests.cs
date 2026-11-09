using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
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
