using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Assistant;
using YAPA.Avalonia.Audio;
using YAPA.Avalonia.Audio.Ambience;
using YAPA.Avalonia.Persistence;
using YAPA.Avalonia.Services;
using YAPA.Avalonia.Specifics;
using YAPA.Avalonia.Tests.AppLogic;
using YAPA.Avalonia.Tests.Audio;
using YAPA.Avalonia.Tests.Persistence;
using YAPA.Shared.Common;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Tests.Assistant;

/// <summary>The app as the assistant sees it: real engine, real SQLite history, real settings.</summary>
internal sealed class AssistantRig : IDisposable
{
    public readonly TempDatabase Db = new();
    public readonly ISettings Settings;
    public readonly PomodoroEngineSettings EngineSettings;
    public readonly GeneralSettings General;
    public readonly AmbienceSettings Ambience;
    public readonly AvaloniaYapaThemeSettings Look;
    public readonly ManualTimer Timer = new();
    public readonly ManualClock Clock = new();
    public readonly PomodoroEngine Engine;
    public readonly SqlitePomodoroRepository History;
    public readonly ChangeJournal Journal;
    public readonly SettingsCatalog Catalog;
    public readonly AppTools Tools;
    public readonly AssistantToolbox Toolbox;

    public AssistantRig()
    {
        // 14:00 local today, whatever zone the tests run in.
        Clock.UtcNow = TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddHours(14), TimeZoneInfo.Local);
        Settings = new JsonYapaSettings(new MemoryEnvironment(), new NewtonsoftJson());
        EngineSettings = new PomodoroEngineSettings(Settings)
        {
            Profiles = new() { ["Pomodoro"] = new PomodoroProfile { WorkTime = 50 * 60, BreakTime = 10 * 60, LongBreakTime = 30 * 60, PomodorosBeforeLongBreak = 4 } },
            ActiveProfile = "Pomodoro",
        };
        General = new GeneralSettings(Settings) { DailyGoal = 12 };
        Ambience = new AmbienceSettings(Settings);
        Look = new AvaloniaYapaThemeSettings(Settings);
        History = new SqlitePomodoroRepository(Db.FilePath, TimeZoneInfo.Local, () => Clock.UtcNow);
        Engine = new PomodoroEngine(EngineSettings, Timer, Clock, new Inline(), Settings, History);
        _ = new Dashboard(Engine, History, EngineSettings);
        Journal = new ChangeJournal(Engine, History, Clock);
        Catalog = new SettingsCatalog(EngineSettings, General, Look, new AvaloniaThemeSettings(Settings), Ambience,
            new SoundSettings(Settings), new AssistantSettings(Settings));
        var effects = new AmbienceEffects(Engine, Ambience, EngineSettings, new FakeAmbiencePlayer(), Settings, Clock);
        Tools = new AppTools(Engine, EngineSettings, General, History, new AppNavigator(new AvaloniaThemeSettings(Settings)),
            Catalog, Journal, Ambience, effects, Clock);
        Toolbox = new AssistantToolbox(Tools.All(), Journal, f => Task.FromResult(f()));
    }

    public DateTime LocalNow => Clock.UtcNow.ToLocalTime();

    public ToolOutcome Call(string tool, string json = "{}") => Toolbox.Run(tool, JsonNode.Parse(json)!.AsObject());

    public JsonNode Ok(string tool, string json = "{}")
    {
        var outcome = Call(tool, json);
        Assert.IsFalse(outcome.IsError, outcome.Text);
        return JsonNode.Parse(outcome.Text)!;
    }

    public void Elapse(int seconds)
    {
        Clock.Advance(seconds);
        Timer.Fire();
    }

    public void Dispose()
    {
        History.Dispose();
        Db.Dispose();
    }
}

[TestClass]
public class AssistantToolTests
{
    [TestMethod]
    public void Logging_off_the_timer_records_each_one_back_to_back_and_moves_the_cycle_on()
    {
        using var rig = new AssistantRig();
        rig.Journal.BeginTurn();
        rig.Ok("log_pomodoros", """{"count": 2}""");

        var rows = rig.History.All();
        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows.All(r => r.DurationMin == 50 && r.ProfileName == "Pomodoro"));
        // The last ends now; the one before it a pomodoro and a short break earlier.
        Assert.AreEqual(rig.Clock.UtcNow, rows[1].DateTime);
        Assert.AreEqual(rig.Clock.UtcNow.AddMinutes(-60), rows[0].DateTime);
        Assert.AreEqual(3, rig.Engine.Index);
        Assert.AreEqual(PomodoroPhase.NotStarted, rig.Engine.Phase);
    }

    [TestMethod]
    public void Logged_times_are_the_local_times_given()
    {
        using var rig = new AssistantRig();
        rig.Ok("log_pomodoros", """{"count": 2, "ended_at": ["07:50", "08:50"], "minutes": 45}""");

        var ends = rig.History.All().Select(r => r.DateTime.ToLocalTime().TimeOfDay).ToArray();
        CollectionAssert.AreEqual(new[] { TimeSpan.FromHours(7) + TimeSpan.FromMinutes(50), TimeSpan.FromHours(8) + TimeSpan.FromMinutes(50) }, ends);
        Assert.IsTrue(rig.History.All().All(r => r.DurationMin == 45));
    }

    [TestMethod]
    public void Logging_while_a_pomodoro_runs_asks_first_and_can_add_to_history_only()
    {
        using var rig = new AssistantRig();
        rig.Engine.Start();

        var refused = rig.Call("log_pomodoros", """{"count": 1}""");
        Assert.IsTrue(refused.IsError);
        StringAssert.Contains(refused.Text, "advance_cycle false");
        Assert.AreEqual(0, rig.History.All().Count);

        rig.Ok("log_pomodoros", """{"count": 1, "advance_cycle": false}""");
        Assert.AreEqual(1, rig.History.All().Count);
        Assert.AreEqual(PomodoroPhase.Work, rig.Engine.Phase, "the running pomodoro is untouched");
        Assert.AreEqual(1, rig.Engine.Index);
    }

    [TestMethod]
    public void Future_times_and_mismatched_counts_are_refused()
    {
        using var rig = new AssistantRig();
        Assert.IsTrue(rig.Call("log_pomodoros", """{"count": 1, "ended_at": ["23:59"]}""").IsError);
        Assert.IsTrue(rig.Call("log_pomodoros", """{"count": 2, "ended_at": ["09:00"]}""").IsError);
        Assert.AreEqual(0, rig.History.All().Count);
    }

    [TestMethod]
    public void Undo_takes_back_the_rows_and_the_cycle_together()
    {
        using var rig = new AssistantRig();
        rig.Journal.BeginTurn();
        rig.Ok("log_pomodoros", """{"count": 3}""");
        Assert.IsTrue(rig.Journal.EndTurn());

        var undone = rig.Journal.Undo();
        StringAssert.Contains(undone, "Logged 3 pomodoros");
        Assert.AreEqual(0, rig.History.All().Count);
        Assert.AreEqual(1, rig.Engine.Index);
        Assert.IsFalse(rig.Journal.CanUndo);
    }

    [TestMethod]
    public void Undoing_a_change_to_a_running_timer_keeps_the_time_that_has_passed()
    {
        using var rig = new AssistantRig();
        rig.Engine.Start();
        rig.Elapse(60);

        rig.Journal.BeginTurn();
        rig.Ok("adjust_interval", """{"add_minutes": 5}""");
        rig.Journal.EndTurn();
        Assert.AreEqual(55 * 60, rig.Engine.CurrentIntervalLength);

        rig.Elapse(30);
        rig.Journal.Undo();
        Assert.AreEqual(50 * 60, rig.Engine.CurrentIntervalLength);
        Assert.AreEqual(PomodoroPhase.Work, rig.Engine.Phase);
        Assert.AreEqual(90, rig.Engine.Elapsed, "the 30 s since the change still count");
    }

    [TestMethod]
    public void A_pomodoro_started_late_is_backdated()
    {
        using var rig = new AssistantRig();
        var result = rig.Ok("control_timer", """{"action": "start", "already_elapsed_minutes": 12}""");
        Assert.AreEqual(12 * 60, rig.Engine.Elapsed);
        StringAssert.Contains(result["done"]!.GetValue<string>(), "12:00 in");
    }

    [TestMethod]
    public void Timer_actions_that_make_no_sense_now_explain_themselves()
    {
        using var rig = new AssistantRig();
        Assert.IsTrue(rig.Call("control_timer", """{"action": "pause"}""").IsError);
        Assert.IsTrue(rig.Call("control_timer", """{"action": "skip_break"}""").IsError);
        Assert.IsTrue(rig.Call("adjust_interval", """{"add_minutes": 5}""").IsError);
        rig.Engine.Start();
        Assert.IsTrue(rig.Call("adjust_interval", """{"add_minutes": 5, "elapsed_minutes": 3}""").IsError, "exactly one");
        Assert.IsTrue(rig.Call("set_cycle_position", """{"pomodoro": 2}""").IsError, "not while running");
    }

    [TestMethod]
    public void Work_in_progress_is_only_dropped_when_the_user_said_so()
    {
        using var rig = new AssistantRig();
        rig.Engine.Start();
        rig.Elapse(27 * 60);
        rig.Engine.Pause();

        var refused = rig.Call("control_timer", """{"action": "stop"}""");
        Assert.IsTrue(refused.IsError);
        StringAssert.Contains(refused.Text, "27 minutes in");
        Assert.IsTrue(rig.Call("control_timer", """{"action": "reset_cycle"}""").IsError);
        Assert.AreEqual(PomodoroPhase.Pause, rig.Engine.Phase);

        rig.Ok("control_timer", """{"action": "stop", "confirmed": true}""");
        Assert.AreEqual(PomodoroPhase.NotStarted, rig.Engine.Phase);
    }

    [TestMethod]
    public void A_pomodoro_just_begun_or_a_break_stops_without_asking()
    {
        using var rig = new AssistantRig();
        rig.Engine.Start();
        rig.Elapse(20);
        rig.Ok("control_timer", """{"action": "stop"}""");

        rig.Ok("set_cycle_position", """{"pomodoro": 1}""");
        rig.Engine.Start();
        rig.Elapse(50 * 60);
        rig.Engine.Start(); // the break
        rig.Elapse(5 * 60);
        rig.Ok("control_timer", """{"action": "stop"}""");
    }

    [TestMethod]
    public void Remaining_minutes_set_the_end_from_now()
    {
        using var rig = new AssistantRig();
        rig.Engine.Start();
        rig.Elapse(20 * 60);
        rig.Ok("adjust_interval", """{"remaining_minutes": 3}""");
        Assert.AreEqual(180, rig.Engine.Remaining);
    }

    [TestMethod]
    public void Skipping_ahead_records_nothing()
    {
        using var rig = new AssistantRig();
        rig.Ok("set_cycle_position", """{"pomodoro": 4, "cycle": 2}""");
        Assert.AreEqual(4, rig.Engine.Index);
        Assert.AreEqual(2, rig.Engine.Cycle);
        Assert.AreEqual(0, rig.History.All().Count);
        Assert.IsTrue(rig.Call("set_cycle_position", """{"pomodoro": 5}""").IsError, "only 4 in the cycle");
    }

    [TestMethod]
    public void The_plan_answers_stop_at_six_questions()
    {
        using var rig = new AssistantRig();
        // 14:00, idle, 50 + 10 with a 30 min long break after 4: ends 14:50, 15:50, 16:50, 17:50; then 18:20–19:10.
        var plan = rig.Ok("plan_schedule", """{"until": "18:00"}""");
        Assert.AreEqual(4, plan["pomodoros_completed_in_plan"]!.GetValue<int>());
        Assert.AreEqual("17:50", plan["last_pomodoro_ends_at"]!.GetValue<string>());

        Assert.IsTrue(rig.Call("plan_schedule", """{"until": "09:00"}""").IsError, "already passed");
        Assert.IsTrue(rig.Call("plan_schedule", "{}").IsError);
    }

    [TestMethod]
    public void State_describes_the_timer_today_and_the_profile()
    {
        using var rig = new AssistantRig();
        rig.Engine.Start();
        rig.Elapse(600);
        var state = rig.Ok("get_state");

        Assert.AreEqual("pomodoro_running", state["timer"]!["state"]!.GetValue<string>());
        Assert.AreEqual("40:00", state["timer"]!["remaining"]!.GetValue<string>());
        Assert.AreEqual("14:50", state["timer"]!["ends_at"]!.GetValue<string>());
        Assert.AreEqual(12, state["today"]!["daily_goal"]!.GetValue<int>());
        Assert.AreEqual(50, state["profile"]!["work_minutes"]!.GetValue<int>());
    }

    [TestMethod]
    public void Settings_change_all_together_or_not_at_all_and_undo_restores_them()
    {
        using var rig = new AssistantRig();
        var refused = rig.Call("update_settings", """{"changes": {"timer.work_minutes": 45, "ambience.sound": "jazz"}}""");
        Assert.IsTrue(refused.IsError);
        Assert.AreEqual(50 * 60, rig.EngineSettings.WorkTime, "nothing applied");

        rig.Journal.BeginTurn();
        rig.Ok("update_settings", """{"changes": {"timer.work_minutes": 45, "ambience.sound": "soft rain", "appearance.text_color": "gold", "goal.daily_goal": "10"}}""");
        rig.Journal.EndTurn();
        Assert.AreEqual(45 * 60, rig.EngineSettings.WorkTime);
        Assert.AreEqual(AmbientSound.SoftRain, rig.Ambience.Sound);
        Assert.AreEqual(global::Avalonia.Media.Colors.Gold, rig.Look.TextColor);
        Assert.AreEqual(10, rig.General.DailyGoal);

        rig.Journal.Undo();
        Assert.AreEqual(50 * 60, rig.EngineSettings.WorkTime);
        Assert.AreEqual(AmbientSound.None, rig.Ambience.Sound);
        Assert.AreEqual(global::Avalonia.Media.Colors.White, rig.Look.TextColor);
        Assert.AreEqual(12, rig.General.DailyGoal);
    }

    [TestMethod]
    public void Settings_reject_values_out_of_range_and_unknown_keys()
    {
        using var rig = new AssistantRig();
        Assert.IsTrue(rig.Call("update_settings", """{"changes": {"timer.work_minutes": 0}}""").IsError);
        Assert.IsTrue(rig.Call("update_settings", """{"changes": {"sound.volume": 140}}""").IsError);
        Assert.IsTrue(rig.Call("update_settings", """{"changes": {"timer.colour": "red"}}""").IsError);
        rig.Ok("update_settings", """{"changes": {"sound.volume": "35%", "timer.auto_start_breaks": "on"}}""");
        Assert.AreEqual(0.35, rig.EngineSettings.Volume, 1e-9);
        Assert.IsTrue(rig.EngineSettings.AutoStartBreak);
    }

    [TestMethod]
    public void A_settings_change_reads_in_plain_words()
    {
        using var rig = new AssistantRig();
        rig.Journal.BeginTurn();
        rig.Ok("update_settings", """{"changes": {"ambience.sound": "soft_rain", "ambience.volume": 50, "appearance.text_color": "#FFD700"}}""");
        rig.Journal.EndTurn();
        Assert.AreEqual("Set background sound to soft rain, background volume to 50% and text colour to gold", rig.Journal.LastDescription);
    }

    [TestMethod]
    public void Every_setting_reads_back_in_a_form_it_accepts()
    {
        using var rig = new AssistantRig();
        foreach (var entry in rig.Catalog.Entries)
        {
            if (entry.Key is "general.launch_at_login" or "general.start_hidden") continue; // writes the real autostart file
            var current = entry.Get();
            var applied = rig.Catalog.Apply(new JsonObject { [entry.Key] = current?.DeepClone() }, null);
            Assert.AreEqual(0, applied.Count, $"{entry.Key}: {current?.ToJsonString()} should be a no-op");
        }
    }

    [TestMethod]
    public void Profiles_are_created_switched_and_restored()
    {
        using var rig = new AssistantRig();
        rig.Journal.BeginTurn();
        rig.Ok("manage_profiles", """{"action": "create", "name": "Deep", "work_minutes": 90, "short_break_minutes": 20, "switch_to": true}""");
        rig.Journal.EndTurn();
        Assert.AreEqual("Deep", rig.EngineSettings.ActiveProfile);
        Assert.AreEqual(90 * 60, rig.Engine.WorkTime);
        Assert.AreEqual(4, rig.EngineSettings.PomodorosBeforeLongBreak, "copied from the active profile");

        Assert.IsTrue(rig.Call("manage_profiles", """{"action": "create", "name": "deep"}""").IsError, "names are case-insensitive");

        rig.Journal.Undo();
        Assert.AreEqual("Pomodoro", rig.EngineSettings.ActiveProfile);
        Assert.IsFalse(rig.EngineSettings.Profiles.ContainsKey("Deep"));
    }

    [TestMethod]
    public void History_edits_are_checked_first_and_undo_follows_restored_rows()
    {
        using var rig = new AssistantRig();
        rig.Ok("edit_history", """{"add": [{"ended_at": "09:50"}, {"ended_at": "10:50"}]}""");
        var ids = rig.History.All().Select(r => r.Id).ToArray();

        Assert.IsTrue(rig.Call("edit_history", $$"""{"delete": [{{ids[0]}}, 999]}""").IsError);
        Assert.AreEqual(2, rig.History.All().Count, "nothing deleted when one id is wrong");

        rig.Journal.BeginTurn();
        rig.Ok("edit_history", $$"""{"update": [{"id": {{ids[0]}}, "minutes": 25}]}""");
        rig.Ok("edit_history", $$"""{"delete": [{{ids[0]}}]}""");
        rig.Journal.EndTurn();
        Assert.AreEqual(1, rig.History.All().Count);

        rig.Journal.Undo();
        var rows = rig.History.All();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(50, rows.Single(r => r.DateTime.ToLocalTime().Hour == 9).DurationMin, "back to before the update, under its new id");
    }

    [TestMethod]
    public void History_queries_see_local_times_and_cannot_write()
    {
        using var rig = new AssistantRig();
        rig.Ok("log_pomodoros", """{"count": 1, "ended_at": ["09:50"], "advance_cycle": false}""");

        var result = rig.Ok("query_history", """{"sql": "SELECT started_at, hour, weekday, minutes FROM pomodoros"}""");
        var row = result["rows"]![0]!.AsArray();
        StringAssert.EndsWith(row[0]!.GetValue<string>(), "09:00:00");
        Assert.AreEqual(9, row[1]!.GetValue<long>());
        Assert.AreEqual(DateTime.Today.DayOfWeek.ToString(), row[2]!.GetValue<string>());

        var write = rig.Call("query_history", """{"sql": "DELETE FROM pomodoros"}""");
        Assert.IsTrue(write.IsError);
        Assert.AreEqual(1, rig.History.All().Count);
    }

    [TestMethod]
    public void A_runaway_query_is_stopped()
    {
        using var rig = new AssistantRig();
        var outcome = rig.Call("query_history", """{"sql": "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT count(*) FROM n"}""");
        Assert.IsTrue(outcome.IsError);
        StringAssert.Contains(outcome.Text, "stopped");
    }

    [TestMethod]
    public void Statistics_cover_today_and_the_week()
    {
        using var rig = new AssistantRig();
        rig.Ok("log_pomodoros", """{"count": 2, "ended_at": ["09:50", "10:50"], "advance_cycle": false}""");
        var stats = rig.Ok("get_statistics");
        Assert.AreEqual(2, stats["today"]!["pomodoros"]!.GetValue<int>());
        Assert.AreEqual(100, stats["today"]!["focus_minutes"]!.GetValue<int>());
        Assert.IsNotNull(stats["focus_by_hour"]!["peak_hour"]);
    }

    [TestMethod]
    public void Unknown_tools_and_bad_arguments_come_back_as_errors_not_exceptions()
    {
        using var rig = new AssistantRig();
        Assert.IsTrue(rig.Call("format_disk").IsError);
        Assert.IsTrue(rig.Call("log_pomodoros", """{"count": "lots"}""").IsError);
        Assert.IsTrue(rig.Call("control_timer", "{}").IsError);
    }

    [TestMethod]
    public void Reading_tools_never_open_an_undo_checkpoint()
    {
        using var rig = new AssistantRig();
        rig.Journal.BeginTurn();
        rig.Ok("get_state");
        rig.Ok("get_settings");
        rig.Ok("plan_schedule", """{"pomodoros": 2}""");
        Assert.IsFalse(rig.Journal.EndTurn());
    }
}

[TestClass]
public class McpServerTests
{
    private static AssistantToolbox Toolbox() => new(
        [new AssistantTool { Name = "echo", Description = "Echoes.", Run = a => new ToolResult(new JsonObject { ["said"] = a.String("text") }) }],
        new ChangeJournal(NSubstitute.Substitute.For<IPomodoroEngine>(), NSubstitute.Substitute.For<IPomodoroRepository>(), new ManualClock()),
        f => Task.FromResult(f()));

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Post(McpServer server, string json, string? token = null, string? origin = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (token ?? server.Token));
        if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    [TestMethod]
    public async Task Speaks_enough_MCP_for_claude_code()
    {
        using var server = McpServer.Start(Toolbox());

        var init = await Post(server, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{}}}""");
        Assert.AreEqual(HttpStatusCode.OK, init.Status);
        Assert.AreEqual("2025-11-25", init.Body!["result"]!["protocolVersion"]!.GetValue<string>());

        var note = await Post(server, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.AreEqual(HttpStatusCode.Accepted, note.Status);

        var list = await Post(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.AreEqual("echo", list.Body!["result"]!["tools"]![0]!["name"]!.GetValue<string>());

        var call = await Post(server, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""");
        var content = call.Body!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.AreEqual("hi", JsonNode.Parse(content)!["said"]!.GetValue<string>());
        Assert.IsFalse(call.Body!["result"]!["isError"]!.GetValue<bool>());

        var unknown = await Post(server, """{"jsonrpc":"2.0","id":3,"method":"server/discover"}""");
        Assert.AreEqual(-32601, unknown.Body!["error"]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Turns_away_anyone_without_the_token_and_any_web_page()
    {
        using var server = McpServer.Start(Toolbox());
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Post(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", token: "guess")).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Post(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", origin: "https://evil.example")).Status);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + server.Token);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, (await client.GetAsync(server.Url)).StatusCode);
    }

    [TestMethod]
    public void The_client_config_points_at_the_server_with_its_token()
    {
        using var server = McpServer.Start(Toolbox());
        var config = JsonNode.Parse(server.ClientConfig())!["mcpServers"]!["yapa"]!;
        Assert.AreEqual(server.Url, config["url"]!.GetValue<string>());
        Assert.AreEqual("Bearer " + server.Token, config["headers"]!["Authorization"]!.GetValue<string>());
        StringAssert.StartsWith(server.Url, "http://127.0.0.1:");
    }
}

[TestClass]
public class ClaudeStreamTests
{
    [TestMethod]
    public void Reads_the_events_the_panel_needs()
    {
        var init = ClaudeStream.Parse("""{"type":"system","subtype":"init","model":"claude-sonnet-5-5","mcp_servers":[{"name":"yapa","status":"connected"}]}""").Single();
        Assert.AreEqual(new ClaudeReady("claude-sonnet-5-5", true), init);

        var failed = ClaudeStream.Parse("""{"type":"system","subtype":"init","model":"m","mcp_servers":[{"name":"yapa","status":"failed"}]}""").Single();
        Assert.IsFalse(((ClaudeReady)failed).ToolsConnected);

        Assert.IsInstanceOfType<ClaudeMessageStarted>(ClaudeStream.Parse("""{"type":"stream_event","event":{"type":"message_start"}}""").Single());
        Assert.AreEqual(new ClaudeTextDelta("Hi"), ClaudeStream.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hi"}}}""").Single());
        Assert.AreEqual(new ClaudeToolCall("mcp__yapa__get_state"), ClaudeStream.Parse("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"mcp__yapa__get_state"}}}""").Single());
        Assert.AreEqual(new ClaudeMessageEnded("end_turn"), ClaudeStream.Parse("""{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn"}}}""").Single());
        Assert.AreEqual(new ClaudeTurnResult(false, "success", "Done."), ClaudeStream.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"Done."}""").Single());
        Assert.IsTrue(((ClaudeTurnResult)ClaudeStream.Parse("""{"type":"result","subtype":"error_during_execution","is_error":false}""").Single()).IsError);

        var limited = (ClaudeRateLimited)ClaudeStream.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1794526200}}""").Single();
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1794526200), limited.ResetsAt);
        Assert.AreEqual(0, ClaudeStream.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}""").Count());
    }

    [TestMethod]
    public void Ignores_noise()
    {
        Assert.AreEqual(0, ClaudeStream.Parse("").Count());
        Assert.AreEqual(0, ClaudeStream.Parse("not json").Count());
        Assert.AreEqual(0, ClaudeStream.Parse("""{"type":"system","subtype":"post_turn_summary"}""").Count());
        Assert.AreEqual(0, ClaudeStream.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{"}}}""").Count());
    }

    [TestMethod]
    public void Messages_in_are_stream_json_user_turns()
    {
        var message = JsonNode.Parse(ClaudeStream.UserMessage("hello"))!;
        Assert.AreEqual("user", message["type"]!.GetValue<string>());
        Assert.AreEqual("hello", message["message"]!["content"]!.GetValue<string>());
        Assert.AreEqual("interrupt", JsonNode.Parse(ClaudeStream.Interrupt("x"))!["request"]!["subtype"]!.GetValue<string>());
    }

    [TestMethod]
    public void Answers_are_kept_plain_text()
    {
        Assert.AreEqual("You did 5 today.\n• Monday: 3", AssistantViewModel.Tidy("## Today\nYou did **5** today.\n- Monday: `3`", final: true)
            .Replace("Today\n", ""));
    }
}

[TestClass]
public class ClaudeCliTests
{
    [TestMethod]
    public void A_conversation_reaches_nothing_but_yapa()
    {
        var args = ClaudeCli.Arguments("sonnet", "prompt", "/tmp/mcp.json").ToList();
        string After(string flag) => args[args.IndexOf(flag) + 1];

        Assert.AreEqual("", After("--tools"), "no built-in tools: no shell, no files");
        CollectionAssert.Contains(args, "--strict-mcp-config");
        Assert.AreEqual("mcp__yapa", After("--allowedTools"));
        Assert.AreEqual("", After("--setting-sources"), "none of the user's hooks, plugins or CLAUDE.md");
        Assert.AreEqual("none", After("--permission-prompts"));
        CollectionAssert.Contains(args, "--no-session-persistence");
        Assert.AreEqual("sonnet", After("--model"));
    }

    [TestMethod]
    public void The_override_finds_a_given_executable_or_nothing()
    {
        var saved = Environment.GetEnvironmentVariable(ClaudeCli.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(ClaudeCli.OverrideVariable, "/bin/true");
            Assert.AreEqual("/bin/true", ClaudeCli.Locate());
            Environment.SetEnvironmentVariable(ClaudeCli.OverrideVariable, "/nonexistent/claude");
            Assert.IsNull(ClaudeCli.Locate());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeCli.OverrideVariable, saved);
        }
    }
}
