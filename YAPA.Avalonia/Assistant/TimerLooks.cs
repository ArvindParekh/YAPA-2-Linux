using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Assistant;

/// <param name="BuiltIn">Ships with YAPA; can't be deleted or overwritten.</param>
public sealed record TimerLook(string Name, string Description, IReadOnlyDictionary<string, string> Values, bool BuiltIn);

/// <summary>
/// Named looks for the timer: a set of appearance values applied at once ("Glass", "Neon", or
/// one the user or the assistant made). A look is complete: applying it first puts every look
/// setting back to its default, then sets its own, so looks never leave traces of each other.
/// Size and position are not part of a look.
/// </summary>
public sealed class TimerLooks
{
    /// <summary>The settings a look covers, with their defaults (as the catalog writes them).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["appearance.theme"] = "classic",
        ["appearance.text_color"] = "#ffffffff",
        ["appearance.shadow_color"] = "#ff000000",
        ["appearance.shadow_opacity"] = "60",
        ["appearance.opacity"] = "100",
        ["appearance.show_seconds"] = "true",
        ["appearance.show_status"] = "true",
        ["appearance.show_progress"] = "true",
        ["appearance.controls_on_hover"] = "false",
        ["appearance.digit_weight"] = "light",
        ["appearance.digit_font"] = "inter",
        ["appearance.progress_color"] = "auto",
        ["appearance.break_color"] = "#ff3cb371",
        ["appearance.backdrop"] = "false",
        ["appearance.backdrop_color"] = "#ff15151a",
        ["appearance.backdrop_opacity"] = "55",
    };

    public static readonly IReadOnlyList<TimerLook> Presets =
    [
        new("Classic", "The original: white digits floating on the desktop.", new Dictionary<string, string>(), true),
        new("Glass", "Digits on a smoky plate, with a tomato progress line.", new Dictionary<string, string>
        {
            ["appearance.backdrop"] = "true", ["appearance.backdrop_color"] = "#ff101014", ["appearance.backdrop_opacity"] = "62",
            ["appearance.shadow_opacity"] = "0", ["appearance.progress_color"] = "#ffff6b57", ["appearance.break_color"] = "#ff3ccb9b",
        }, true),
        new("Minimal", "Thin Geist digits and nothing else until you point at it.", new Dictionary<string, string>
        {
            ["appearance.digit_font"] = "geist", ["appearance.digit_weight"] = "light", ["appearance.show_status"] = "false",
            ["appearance.show_progress"] = "false", ["appearance.controls_on_hover"] = "true", ["appearance.shadow_opacity"] = "35",
        }, true),
        new("Bold", "Heavy gold digits that read from across the room.", new Dictionary<string, string>
        {
            ["appearance.digit_font"] = "geist", ["appearance.digit_weight"] = "bold", ["appearance.text_color"] = "#ffffd166",
            ["appearance.shadow_opacity"] = "70", ["appearance.progress_color"] = "#ffffd166",
        }, true),
        new("Neon", "Cyan digits with a magenta glow.", new Dictionary<string, string>
        {
            ["appearance.text_color"] = "#ff5ef2ff", ["appearance.shadow_color"] = "#ffff2bd6", ["appearance.shadow_opacity"] = "90",
            ["appearance.progress_color"] = "#ffff2bd6", ["appearance.break_color"] = "#ff5ef2ff",
        }, true),
        new("Paper", "Ink on warm paper, calm on light wallpapers.", new Dictionary<string, string>
        {
            ["appearance.backdrop"] = "true", ["appearance.backdrop_color"] = "#fff4f0e8", ["appearance.backdrop_opacity"] = "94",
            ["appearance.text_color"] = "#ff26231f", ["appearance.shadow_opacity"] = "0", ["appearance.progress_color"] = "#ffd9573f",
            ["appearance.break_color"] = "#ff3d9970", ["appearance.digit_font"] = "geist", ["appearance.digit_weight"] = "regular",
        }, true),
    ];

    private readonly SettingsCatalog _catalog;
    private readonly ISettingsForComponent _store;

    public TimerLooks(SettingsCatalog catalog, ISettings settings)
    {
        _catalog = catalog;
        _store = settings.GetSettingsForComponent("Looks");
    }

    public event Action? Changed;

    public IReadOnlyList<TimerLook> All => Presets.Concat(Saved().Select(kv =>
        new TimerLook(kv.Key, kv.Value.GetValueOrDefault("description", "Your own look."), kv.Value.Where(v => v.Key != "description")
            .ToDictionary(v => v.Key, v => v.Value), false))).ToList();

    public TimerLook? Find(string name)
        => All.FirstOrDefault(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The current values of every look setting, to put back on undo.</summary>
    public JsonObject Current()
    {
        var current = new JsonObject();
        foreach (var key in Defaults.Keys) current[key] = _catalog.Get(key)?.DeepClone();
        return current;
    }

    /// <summary>Which look the timer is wearing right now, if it is exactly one of them.</summary>
    public string? Matching()
    {
        var now = Current();
        return All.FirstOrDefault(l => Defaults.All(d =>
            string.Equals(Plain(now[d.Key]), l.Values.GetValueOrDefault(d.Key, d.Value), StringComparison.OrdinalIgnoreCase)))?.Name;
    }

    public void Apply(TimerLook look)
    {
        var values = new JsonObject();
        foreach (var (key, fallback) in Defaults) values[key] = JsonValue.Create(look.Values.GetValueOrDefault(key, fallback));
        _catalog.Restore(values);
        Changed?.Invoke();
    }

    /// <summary>Saves <paramref name="values"/> (look keys only), or what the timer looks like now, under a name.</summary>
    public TimerLook Save(string name, string? description, JsonObject? values)
    {
        name = name.Trim();
        if (name.Length is 0 or > 32) throw new ToolException("A look needs a name of up to 32 characters.");
        if (Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ToolException($"{name} is a built-in look; pick another name.");

        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var source = values ?? Current();
        foreach (var (key, value) in source)
        {
            if (!Defaults.ContainsKey(key)) throw new ToolException($"{key} isn't part of a look; looks cover {string.Join(", ", Defaults.Keys)}.");
            // In the catalog's own form: checked, and comparable with what the timer reports.
            stored[key] = Plain(_catalog.Normalized(key, value));
        }
        if (!string.IsNullOrWhiteSpace(description)) stored["description"] = description.Trim();

        var all = Saved();
        foreach (var existing in all.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList()) all.Remove(existing);
        all[name] = stored;
        _store.Update("Saved", all);
        Changed?.Invoke();
        return Find(name)!;
    }

    public bool Delete(string name)
    {
        var all = Saved();
        var key = all.Keys.FirstOrDefault(k => string.Equals(k, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (key == null) return false;
        all.Remove(key);
        _store.Update("Saved", all);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Puts a deleted look back (undo).</summary>
    public void Restore(TimerLook look)
    {
        var all = Saved();
        all[look.Name] = look.Values.Concat([new KeyValuePair<string, string>("description", look.Description)])
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _store.Update("Saved", all);
        Changed?.Invoke();
    }

    private Dictionary<string, Dictionary<string, string>> Saved()
        => _store.Get<Dictionary<string, Dictionary<string, string>>?>("Saved", null) is { } saved
            ? new Dictionary<string, Dictionary<string, string>>(saved, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    private static string Plain(JsonNode? value)
        => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
}
