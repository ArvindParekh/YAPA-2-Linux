using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YAPA.Avalonia.Platform;
using YAPA.Shared.Journal;

namespace YAPA.Avalonia.Journal;

/// <summary>
/// Claude Code conversations, read from its transcripts (<c>~/.claude/projects/*/*.jsonl</c>):
/// what each was called, the folder it ran in, what the user typed and when it was busy.
/// That's where the work of someone who builds with Claude is, and it reaches back before
/// YAPA recorded anything. YAPA's own assistant keeps no transcripts, so it never shows up.
///
/// Transcripts run to gigabytes and only ever grow, so each file is read once and indexed
/// (in the cache directory), and afterwards only what was appended is read.
/// </summary>
public sealed class ClaudeCodeHistory
{
    private const int IndexVersion = 1;

    private readonly string _root;
    private readonly string _indexDirectory;
    private readonly string _home;
    private readonly ConcurrentDictionary<string, FileIndex> _indexes = new();
    // One lock per transcript, not per index: an index is replaced when its file was rewritten.
    private readonly ConcurrentDictionary<string, object> _gates = new();

    public ClaudeCodeHistory(string? root = null, string? indexDirectory = null, string? home = null)
    {
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } c ? c : Path.Combine(_home, ".claude");
        _root = root ?? Path.Combine(config, "projects");
        _indexDirectory = indexDirectory ?? Path.Combine(AppPaths.CacheDirectory, "journal", "claude-code");
    }

    public bool IsAvailable => Directory.Exists(_root);

    /// <summary>Drops what's indexed in memory; the next read starts from the transcripts again.</summary>
    public void Reset() => _indexes.Clear();

    /// <summary>
    /// Deletes what the index copied from [fromUtc, toUtc] (what was typed, and when anything
    /// happened), in every index on disk and in memory. The transcripts are Claude Code's and
    /// stay as they are; read again from scratch, that time is still kept out by the journal.
    /// </summary>
    public void Scrub(DateTime fromUtc, DateTime toUtc)
    {
        var fromMinute = fromUtc.Ticks / TimeSpan.TicksPerMinute;
        var toMinute = toUtc.Ticks / TimeSpan.TicksPerMinute;
        bool Clean(FileIndex index) =>
            index.Prompts.RemoveAll(p => p.At >= fromUtc && p.At <= toUtc) > 0
            | index.Minutes.RemoveWhere(m => m >= fromMinute && m <= toMinute) > 0;

        var paths = new HashSet<string>(_indexes.Keys, StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(_indexDirectory))
                foreach (var file in Directory.EnumerateFiles(_indexDirectory, "*.json"))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<FileIndex>(File.ReadAllText(file), IndexJson) is { Path.Length: > 0 } stored) paths.Add(stored.Path);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        foreach (var path in paths)
        {
            lock (_gates.GetOrAdd(path, _ => new object()))
            {
                var index = _indexes.TryGetValue(path, out var loaded) ? loaded : LoadIndex(path);
                if (index != null && Clean(index)) SaveIndex(index);
            }
        }
    }

    /// <summary>Conversations with anything in [fromUtc, toUtc], each cut to that range.</summary>
    public IReadOnlyList<AgentSession> Read(DateTime fromUtc, DateTime toUtc)
    {
        if (!IsAvailable) return [];
        var result = new List<AgentSession>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }

        foreach (var path in files)
        {
            // Subagents' transcripts sit in folders of their own; their work is their parent's.
            if (Path.GetDirectoryName(Path.GetDirectoryName(path)) != _root) continue;
            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (info.LastWriteTimeUtc < fromUtc) continue; // nothing in it is that recent

            // Cut from the index under its file's lock: another day's read may be appending to it.
            lock (_gates.GetOrAdd(path, _ => new object()))
            {
                var index = Update(path, info);
                if (index == null) continue;
                var prompts = index.Prompts.Where(p => p.At >= fromUtc && p.At <= toUtc).Select(p => new AgentPrompt(p.At, p.Text)).ToList();
                var fromMinute = fromUtc.Ticks / TimeSpan.TicksPerMinute;
                var toMinute = toUtc.Ticks / TimeSpan.TicksPerMinute;
                var activity = index.Minutes.Where(m => m >= fromMinute && m <= toMinute).Select(m => new DateTime(m * TimeSpan.TicksPerMinute, DateTimeKind.Utc)).ToList();
                if (prompts.Count == 0 && activity.Count == 0) continue;
                // Engagement looks back at earlier prompts too: one typed before the range can explain activity in it.
                var context = index.Prompts.Where(p => p.At < fromUtc && p.At >= fromUtc - JournalBuilder.Engagement).Select(p => new AgentPrompt(p.At, p.Text));
                result.Add(new AgentSession(index.SessionId, ProjectName(index.Cwd), index.BestTitle, context.Concat(prompts).ToList(), activity, index.Cwd ?? ""));
            }
        }
        return result;
    }

    private string ProjectName(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        var full = cwd.TrimEnd('/');
        if (full == _home.TrimEnd('/')) return "";
        // The desktop app's throwaway folders ("scratch-2026-11-05-b6ddda") name nothing.
        var name = Path.GetFileName(full);
        return name.StartsWith("scratch-", StringComparison.Ordinal) ? "" : name;
    }

    // ── The index ────────────────────────────────────────────────────────────

    internal sealed class FileIndex
    {
        public int Version { get; set; } = IndexVersion;
        public string Path { get; set; } = "";
        public long Offset { get; set; }
        public string SessionId { get; set; } = "";
        public string? Cwd { get; set; }
        public string? CustomTitle { get; set; }
        public string? AiTitle { get; set; }
        public string? AgentName { get; set; }
        public List<IndexedPrompt> Prompts { get; set; } = [];
        /// <summary>Minutes (UTC ticks / minute) in which anything happened.</summary>
        public SortedSet<long> Minutes { get; set; } = [];

        [JsonIgnore]
        public string BestTitle =>
            Meaningful(CustomTitle) ?? Meaningful(AiTitle) ?? Meaningful(AgentName)
            ?? (Prompts.Count > 0 ? Clip(Prompts[0].Text, 60) : "Untitled conversation");

        // A placeholder someone (or something) typed in says less than Claude Code's own title.
        private static string? Meaningful(string? title)
            => title is { Length: > 0 } t && t.Trim().ToLowerInvariant() is not ("new title" or "new chat" or "new conversation" or "new session"
                or "untitled" or "untitled conversation" or "title") ? t : null;
    }

    internal sealed record IndexedPrompt(DateTime At, string Text);

    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented = false };

    private FileIndex? Update(string path, FileInfo info)
    {
        lock (_gates.GetOrAdd(path, _ => new object()))
        {
            var index = _indexes.GetOrAdd(path, p => LoadIndex(p) ?? new FileIndex { Path = p, SessionId = System.IO.Path.GetFileNameWithoutExtension(p) });
            if (info.Length < index.Offset) // rewritten, not appended to: start over
            {
                index = new FileIndex { Path = path, SessionId = System.IO.Path.GetFileNameWithoutExtension(path) };
                _indexes[path] = index;
            }
            if (info.Length == index.Offset) return index;
            try
            {
                ReadFrom(index, path);
                SaveIndex(index);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[journal] can't read {path}: {e.Message}");
            }
            return index;
        }
    }

    private void ReadFrom(FileIndex index, string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        stream.Seek(index.Offset, SeekOrigin.Begin);
        var buffer = new byte[1 << 20];
        var pending = new MemoryStream();
        long lineStart = index.Offset;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var span = buffer.AsSpan(0, read);
            while (true)
            {
                var newline = span.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    pending.Write(span);
                    break;
                }
                ReadOnlySpan<byte> line;
                if (pending.Length > 0)
                {
                    pending.Write(span[..newline]);
                    line = pending.GetBuffer().AsSpan(0, (int)pending.Length);
                }
                else
                {
                    line = span[..newline];
                }
                Index(index, line);
                lineStart += line.Length + 1;
                pending.SetLength(0);
                span = span[(newline + 1)..];
            }
        }
        // A line still being written stays for next time.
        index.Offset = lineStart;
    }

    /// <summary>
    /// One transcript line. Only the top-level fields are looked at (nested values are
    /// skipped without being built), and the message is parsed only when it is something
    /// the user typed.
    /// </summary>
    private static void Index(FileIndex index, ReadOnlySpan<byte> line)
    {
        if (line.Length < 2) return;
        string? type = null, timestamp = null, turnOrigin = null, promptSource = null;
        bool sidechain = false, meta = false;
        int messageStart = -1, messageEnd = -1;
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "type": type = String(ref reader); break;
                    case "timestamp": timestamp = String(ref reader); break;
                    case "turnOrigin": turnOrigin = String(ref reader); break;
                    case "promptSource": promptSource = String(ref reader); break;
                    case "isSidechain": sidechain = reader.TokenType == JsonTokenType.True; break;
                    case "isMeta": meta = reader.TokenType == JsonTokenType.True; break;
                    case "cwd": if (String(ref reader) is { Length: > 0 } cwd) index.Cwd = cwd; break;
                    case "customTitle": if (String(ref reader) is { Length: > 0 } ct) index.CustomTitle = ct; break;
                    case "aiTitle": if (String(ref reader) is { Length: > 0 } ai) index.AiTitle = ai; break;
                    case "agentName": if (String(ref reader) is { Length: > 0 } an) index.AgentName = an; break;
                    case "message":
                        messageStart = (int)reader.TokenStartIndex;
                        reader.Skip();
                        messageEnd = (int)reader.BytesConsumed;
                        break;
                    default: reader.Skip(); break;
                }
            }
        }
        catch (JsonException)
        {
            return;
        }

        if (sidechain || timestamp == null || !DateTime.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return;
        if (type is not ("user" or "assistant")) return;
        index.Minutes.Add(at.Ticks / TimeSpan.TicksPerMinute);

        // Typed by the user: not a tool's result, not a schedule, another agent or a hook.
        if (type != "user" || meta || promptSource == "system" || turnOrigin is not (null or "human") || messageStart < 0) return;
        if (PromptText(line[messageStart..messageEnd]) is { } text) index.Prompts.Add(new IndexedPrompt(at, text));
    }

    private static string? String(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        reader.Skip();
        return null;
    }

    private static readonly Regex Tags = new(@"<(system-reminder|command-[a-z-]+|local-command-[a-z-]+)>.*?</\1>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string? PromptText(ReadOnlySpan<byte> message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message.ToArray());
            if (!doc.RootElement.TryGetProperty("content", out var content)) return null;
            string text;
            if (content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString() ?? "";
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    var kind = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (kind == "tool_result") return null; // a tool talking, not the user
                    if (kind == "text" && part.TryGetProperty("text", out var x)) parts.Add(x.GetString() ?? "");
                }
                text = string.Join("\n", parts);
            }
            else
            {
                return null;
            }

            text = Tags.Replace(text, " ").Trim();
            if (text.Length == 0 || text.StartsWith("<", StringComparison.Ordinal) || text.StartsWith("Caveat:", StringComparison.Ordinal)
                || text.StartsWith("[Request interrupted", StringComparison.Ordinal)) return null;
            return Clip(text, 600);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string s, int max)
    {
        var t = Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    private string IndexPath(string transcript)
        => System.IO.Path.Combine(_indexDirectory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transcript)))[..20].ToLowerInvariant() + ".json");

    private FileIndex? LoadIndex(string transcript)
    {
        try
        {
            var path = IndexPath(transcript);
            if (!File.Exists(path)) return null;
            var index = JsonSerializer.Deserialize<FileIndex>(File.ReadAllText(path), IndexJson);
            return index is { Version: IndexVersion } && index.Path == transcript ? index : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>What the user typed is private: the index is readable by them only.</summary>
    private void SaveIndex(FileIndex index)
    {
        try
        {
            Directory.CreateDirectory(_indexDirectory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_indexDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = IndexPath(index.Path);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(index, IndexJson));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[journal] can't save the transcript index: {e.Message}");
        }
    }
}
