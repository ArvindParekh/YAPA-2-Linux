using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using YAPA.Avalonia.Platform;

namespace YAPA.Avalonia.Assistant;

public sealed record MemoryNote(string Id, string Text, DateTime CreatedUtc);

/// <summary>One finished request, as the archive keeps it.</summary>
public sealed record ArchivedExchange(DateTime AtUtc, string Question, string Answer, IReadOnlyList<string> Actions);

public sealed record ArchivedConversation(DateTime StartedUtc, IReadOnlyList<ArchivedExchange> Exchanges);

/// <summary>
/// What the assistant keeps between conversations, on disk next to the history: notes the
/// user asked it to remember ("count a walk as one pomodoro an hour", "call me boss"),
/// sent with every request, and past conversations it can look back through when asked.
/// </summary>
public sealed class AssistantMemory
{
    public const int MaxNotes = 50;
    private const int MaxConversations = 200;

    private readonly string _notesPath;
    private readonly string _archivePath;
    private readonly List<MemoryNote> _notes;

    public AssistantMemory(string? directory = null)
    {
        var dir = directory ?? Path.Combine(AppPaths.DataDirectory, "assistant");
        _notesPath = Path.Combine(dir, "memory.json");
        _archivePath = Path.Combine(dir, "conversations.jsonl");
        _notes = Read<List<MemoryNote>>(_notesPath) ?? [];
    }

    public IReadOnlyList<MemoryNote> Notes => _notes;

    public MemoryNote Remember(string text)
    {
        if (_notes.Count >= MaxNotes) throw new ToolException($"Memory is full ({MaxNotes} notes); forget something first.");
        var note = new MemoryNote(Guid.NewGuid().ToString("N")[..6], text.Trim(), DateTime.UtcNow);
        _notes.Add(note);
        SaveNotes();
        return note;
    }

    public void Restore(MemoryNote note)
    {
        if (_notes.Any(n => n.Id == note.Id)) return;
        _notes.Add(note);
        SaveNotes();
    }

    public bool Forget(string id)
    {
        var removed = _notes.RemoveAll(n => n.Id == id) > 0;
        if (removed) SaveNotes();
        return removed;
    }

    /// <summary>Appends a finished conversation; the oldest go once there are too many.</summary>
    public void Archive(ArchivedConversation conversation)
    {
        if (conversation.Exchanges.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_archivePath)!);
            var lines = File.Exists(_archivePath) ? File.ReadAllLines(_archivePath).ToList() : [];
            lines.Add(JsonSerializer.Serialize(conversation));
            if (lines.Count > MaxConversations) lines.RemoveRange(0, lines.Count - MaxConversations);
            AtomicFile.WriteAllText(_archivePath, string.Join("\n", lines) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[assistant] can't archive the conversation: {e.Message}");
        }
    }

    /// <summary>Conversations started since <paramref name="sinceUtc"/>, newest first.</summary>
    public IReadOnlyList<ArchivedConversation> Conversations(DateTime sinceUtc)
    {
        try
        {
            if (!File.Exists(_archivePath)) return [];
            return File.ReadLines(_archivePath)
                .Select(line =>
                {
                    try { return JsonSerializer.Deserialize<ArchivedConversation>(line); }
                    catch (JsonException) { return null; }
                })
                .Where(c => c != null && c.StartedUtc >= sinceUtc)
                .Reverse()
                .ToList()!;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[assistant] can't read past conversations: {e.Message}");
            return [];
        }
    }

    public int ConversationCount
    {
        get
        {
            try { return File.Exists(_archivePath) ? File.ReadLines(_archivePath).Count(l => l.Length > 0) : 0; }
            catch (IOException) { return 0; }
        }
    }

    public void ClearConversations()
    {
        try { File.Delete(_archivePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[assistant] can't clear past conversations: {e.Message}");
        }
    }

    private void SaveNotes()
    {
        try
        {
            AtomicFile.WriteAllText(_notesPath, JsonSerializer.Serialize(_notes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[assistant] can't save memory: {e.Message}");
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[assistant] can't read {path}: {e.Message}");
            return null;
        }
    }
}
