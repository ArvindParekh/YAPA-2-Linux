using System;
using System.IO;

namespace YAPA.Avalonia.Platform;

/// <summary>Creating a file only its owner can read: for secrets, such as a sign-in token or the assistant's tool server key.</summary>
public static class OwnerOnlyFile
{
    /// <summary>Create (or replace) for writing, readable and writable by the user alone where the system has such modes.</summary>
    public static FileStreamOptions Create()
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        // Set at creation, not after: there's no moment when the file exists with wider permissions.
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}
