using System.IO;

namespace YAPA.Avalonia.Platform;

public static class AtomicFile
{
    /// <summary>
    /// Writes via a temp file and rename so a crash or power loss mid-write
    /// can never leave a truncated settings file behind (the loader treats
    /// unparsable JSON as "no settings", which would silently reset everything).
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(contents);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
