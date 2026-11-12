using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace YAPA.Avalonia.Assistant;

/// <summary>Something the model can't do as asked, in words it can relay or act on.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <param name="Data">What the model reads back.</param>
/// <param name="Done">For the user, in the past tense ("Logged 3 pomodoros"), when something changed.</param>
public sealed record ToolResult(JsonNode Data, string? Done = null);

public sealed record ToolOutcome(string Text, bool IsError);

public sealed class AssistantTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public JsonObject Schema { get; init; } = JsonSchema.Object();

    /// <summary>Changes something; the change is journaled so it can be undone.</summary>
    public bool Mutates { get; init; }

    /// <summary>Changes the timer's state; the journal snapshots the engine before the first such change.</summary>
    public bool TouchesEngine { get; init; }

    /// <summary>What the orb's caption says while it runs: "Logging 3 pomodoros".</summary>
    public Func<ToolArgs, string>? Activity { get; init; }

    /// <summary>Runs on the UI thread, which owns the engine and the settings, unless <see cref="OffUiThread"/>.</summary>
    public required Func<ToolArgs, ToolResult> Run { get; init; }

    /// <summary>Reads only thread-safe things and may take a while (SQL): run it on a worker thread.</summary>
    public bool OffUiThread { get; init; }
}

public enum ToolActivityKind
{
    Started,
    Finished,
    Failed,
}

/// <param name="Text">The activity while it runs, what was done once finished, or the problem.</param>
public sealed record ToolActivity(ToolActivityKind Kind, string Tool, string Text, bool Mutated);

/// <summary>The tools, and running them on the UI thread with a journal for undo.</summary>
public sealed class AssistantToolbox
{
    private readonly IReadOnlyDictionary<string, AssistantTool> _tools;
    private readonly Func<Func<ToolOutcome>, Task<ToolOutcome>> _onUiThread;

    public AssistantToolbox(IEnumerable<AssistantTool> tools, ChangeJournal journal, Func<Func<ToolOutcome>, Task<ToolOutcome>> onUiThread)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Journal = journal;
        _onUiThread = onUiThread;
    }

    public ChangeJournal Journal { get; }

    /// <summary>Raised around every call, on the thread the tool runs on.</summary>
    public event Action<ToolActivity>? Activity;

    public IReadOnlyCollection<AssistantTool> Tools => _tools.Values.ToList();

    public JsonArray Describe() => new(_tools.Values.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["inputSchema"] = t.Schema.DeepClone(),
        ["annotations"] = new JsonObject { ["readOnlyHint"] = !t.Mutates },
    }).ToArray());

    public Task<ToolOutcome> CallAsync(string name, JsonObject arguments)
        => _tools.TryGetValue(name, out var tool) && tool.OffUiThread
            ? Task.Run(() => Run(name, arguments))
            : _onUiThread(() => Run(name, arguments));

    /// <summary>Must be called on the UI thread, unless the tool is <see cref="AssistantTool.OffUiThread"/>.</summary>
    public ToolOutcome Run(string name, JsonObject arguments)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return new ToolOutcome($"There is no tool named {name}.", true);

        var args = new ToolArgs(arguments);
        string label;
        try { label = tool.Activity?.Invoke(args) ?? ""; }
        catch (Exception) { label = ""; }
        Activity?.Invoke(new ToolActivity(ToolActivityKind.Started, name, label, tool.Mutates));

        try
        {
            ToolResult result;
            if (tool.TouchesEngine) Journal.BeforeEngineChange();
            using (tool.Mutates ? Journal.Track(name) : null)
            {
                result = tool.Run(args);
            }
            // Undo itself reports what it did but is not something to undo.
            if (tool.Mutates && result.Done is { } done) Journal.Describe(done);
            Activity?.Invoke(new ToolActivity(ToolActivityKind.Finished, name, result.Done ?? "", result.Done != null));
            return new ToolOutcome(result.Data.ToJsonString(), false);
        }
        catch (ToolException e)
        {
            Activity?.Invoke(new ToolActivity(ToolActivityKind.Failed, name, e.Message, false));
            return new ToolOutcome(e.Message, true);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[assistant] tool {name} failed: {e}");
            Activity?.Invoke(new ToolActivity(ToolActivityKind.Failed, name, e.Message, false));
            return new ToolOutcome($"The tool failed inside YAPA: {e.Message}", true);
        }
    }
}

/// <summary>Typed access to a tool call's arguments, with errors the model can act on.</summary>
public sealed class ToolArgs(JsonObject values)
{
    public JsonObject Values { get; } = values;

    public bool Has(string name) => Values[name] is not null;

    public string? String(string name) => Values[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var other => other.ToString(),
    };

    public string RequiredString(string name)
        => String(name) is { Length: > 0 } s ? s : throw new ToolException($"\"{name}\" is required.");

    public double? Number(string name)
        => Values[name] is null ? null : AsNumber(Values[name]) ?? throw new ToolException($"\"{name}\" must be a number.");

    /// <summary>
    /// A number from parsed JSON, a value built in code (TryGetValue&lt;double&gt; refuses an int
    /// one), or a numeric string; null otherwise.
    /// </summary>
    public static double? AsNumber(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
            return parsed;
        return null;
    }

    public int? Int(string name) => Number(name) is { } d ? (int)Math.Round(d) : null;

    public bool? Bool(string name) => Values[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) => parsed,
        _ => throw new ToolException($"\"{name}\" must be true or false."),
    };

    public JsonArray? Array(string name) => Values[name] switch
    {
        null => null,
        JsonArray a => a,
        // Models occasionally send an array as its JSON text.
        JsonValue v when v.TryGetValue<string>(out var s) && TryParseArray(s) is { } parsed => parsed,
        _ => throw new ToolException($"\"{name}\" must be a list."),
    };

    public JsonObject? Object(string name) => Values[name] switch
    {
        null => null,
        JsonObject o => o,
        JsonValue v when v.TryGetValue<string>(out var s) && TryParseObject(s) is { } parsed => parsed,
        _ => throw new ToolException($"\"{name}\" must be an object."),
    };

    private static JsonArray? TryParseArray(string s)
    {
        try { return JsonNode.Parse(s) as JsonArray; } catch (System.Text.Json.JsonException) { return null; }
    }

    private static JsonObject? TryParseObject(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>The few JSON Schema shapes the tools need.</summary>
public static class JsonSchema
{
    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema, _) in properties) props[name] = schema;
        var result = new JsonObject { ["type"] = "object", ["properties"] = props };
        var required = properties.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray();
        if (required.Length > 0) result["required"] = new JsonArray(required);
        result["additionalProperties"] = false;
        return result;
    }

    public static JsonObject Str(string description, params string[] choices)
    {
        var s = new JsonObject { ["type"] = "string", ["description"] = description };
        if (choices.Length > 0) s["enum"] = new JsonArray(choices.Select(c => (JsonNode)c).ToArray());
        return s;
    }

    public static JsonObject Int(string description, int? minimum = null, int? maximum = null)
    {
        var s = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (minimum is { } min) s["minimum"] = min;
        if (maximum is { } max) s["maximum"] = max;
        return s;
    }

    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Array(string description, JsonObject items)
        => new() { ["type"] = "array", ["description"] = description, ["items"] = items };

    /// <summary>A free-form object (settings changes, keyed by setting name).</summary>
    public static JsonObject Map(string description)
        => new() { ["type"] = "object", ["description"] = description, ["additionalProperties"] = true };
}
