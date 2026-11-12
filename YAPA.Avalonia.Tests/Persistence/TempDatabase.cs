using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace YAPA.Avalonia.Tests.Persistence;

/// <summary>A throwaway database file in its own temp directory, plus raw-SQL helpers.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "yapa-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, "Yapa.db");
    }

    public string FilePath { get; }

    /// <summary>The temp directory, for anything else a test writes beside the database.</summary>
    public string Folder => _directory;

    private SqliteConnection Open()
    {
        // No pooling: a pooled handle would keep the file open behind the repository's back.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public List<string> Strings(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public void InsertRaw(string dateTimeText, int count = 1, int duration = 25, string? profile = null)
        => Execute(
            """INSERT INTO "Pomodoros" ("ProfileName", "DateTime", "Count", "DurationMin") VALUES ($p, $d, $c, $m);""",
            ("$p", profile), ("$d", dateTimeText), ("$c", count), ("$m", duration));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
