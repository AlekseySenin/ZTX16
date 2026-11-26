namespace ZTX16.Platform.Authoring;

/// <summary>
/// The user's authoring storage: one host directory that authoring tools (the Console and the editors) see as a
/// virtual file tree. Paths are absolute virtual paths such as <c>/MyGame/code/Game.cs</c>: <c>/</c>-separated,
/// no <c>.</c> or <c>..</c> segments, no drive letters, so nothing outside the root can be named.
/// </summary>
/// <remarks>
/// Operations report problems the user can cause (a missing file, a name in use) as results — an error message
/// or null — rather than exceptions, because a shell must survive a typo.
/// </remarks>
public sealed class Workspace
{
    /// <summary>Longest virtual path.</summary>
    public const int MaxPathLength = 240;

    /// <summary>Largest file read or written through the workspace (16 MiB).</summary>
    public const int MaxFileSize = 16 << 20;

    /// <summary>Uses <paramref name="root"/> (created if missing).</summary>
    public Workspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    /// <summary>The host directory behind <c>/</c>.</summary>
    public string Root { get; }

    /// <summary>
    /// The host path of a virtual path, or null with <paramref name="error"/> set when the path is not valid.
    /// </summary>
    public string? Resolve(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Length > MaxPathLength)
        {
            error = $"bad path '{path}': paths start with '/' and are at most {MaxPathLength} characters";
            return null;
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains(':')
                || segment.EndsWith('.') || segment.EndsWith(' '))
            {
                error = $"bad path '{path}': '{segment}' is not a valid name";
                return null;
            }
        }

        string full = Path.GetFullPath(Path.Combine([Root, .. segments]));
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = $"bad path '{path}'";
            return null;
        }

        return full;
    }

    /// <summary>The virtual path of a host path under the root, or null.</summary>
    public string? ToVirtual(string hostPath)
    {
        string full = Path.GetFullPath(hostPath);
        if (full.Equals(Root, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "/" + full[(Root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/')
            : null;
    }

    /// <summary>
    /// Entries of a directory, one per line: directories first (with a trailing <c>/</c>), then files, each
    /// group sorted case-insensitively. Null if <paramref name="path"/> is not a directory.
    /// </summary>
    public string? List(string path)
    {
        string? full = Resolve(path, out _);
        if (full is null || !Directory.Exists(full))
        {
            return null;
        }

        IEnumerable<string> directories = Directory.EnumerateDirectories(full).Select(d => Path.GetFileName(d) + "/").Order(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files = Directory.EnumerateFiles(full).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)!;
        return string.Join("\n", directories.Concat(files));
    }

    /// <summary>True if a file or directory exists.</summary>
    public bool Exists(string path) => Resolve(path, out _) is { } full && (File.Exists(full) || Directory.Exists(full));

    /// <summary>True if <paramref name="path"/> is a directory.</summary>
    public bool IsDirectory(string path) => Resolve(path, out _) is { } full && Directory.Exists(full);

    /// <summary>Size of a file in bytes, or −1.</summary>
    public long Size(string path) => Resolve(path, out _) is { } full && File.Exists(full) ? new FileInfo(full).Length : -1;

    /// <summary>Creates a directory (and missing parents). Returns an error message or "".</summary>
    public string CreateDirectory(string path) => Run(path, full =>
    {
        if (File.Exists(full))
        {
            return "a file with that name exists";
        }

        Directory.CreateDirectory(full);
        return string.Empty;
    });

    /// <summary>Deletes a file, or a directory (only if empty unless <paramref name="recursive"/>). Returns an error or "".</summary>
    public string Delete(string path, bool recursive) => Run(path, full =>
    {
        if (full.Equals(Root, StringComparison.OrdinalIgnoreCase))
        {
            return "the workspace root cannot be deleted";
        }

        if (File.Exists(full))
        {
            File.Delete(full);
            return string.Empty;
        }

        if (!Directory.Exists(full))
        {
            return "not found";
        }

        if (!recursive && Directory.EnumerateFileSystemEntries(full).Any())
        {
            return "the directory is not empty";
        }

        Directory.Delete(full, recursive);
        return string.Empty;
    });

    /// <summary>Moves or renames a file or directory. Returns an error or "".</summary>
    public string Move(string from, string to) => Run(from, source => Run(to, target =>
    {
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            return "not found";
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            return "the target already exists";
        }

        if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "a directory cannot move into itself";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(source))
        {
            File.Move(source, target);
        }
        else
        {
            Directory.Move(source, target);
        }

        return string.Empty;
    }));

    /// <summary>Copies a file or a directory tree. Returns an error or "".</summary>
    public string Copy(string from, string to) => Run(from, source => Run(to, target =>
    {
        if (File.Exists(target) || Directory.Exists(target))
        {
            return "the target already exists";
        }

        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            return string.Empty;
        }

        if (!Directory.Exists(source))
        {
            return "not found";
        }

        if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "a directory cannot be copied into itself";
        }

        CopyTree(source, target);
        return string.Empty;
    }));

    /// <summary>A text file's contents (UTF-8), or null.</summary>
    public string? ReadText(string path) => ReadBytes(path) is { } bytes ? new System.Text.UTF8Encoding(false).GetString(bytes) : null;

    /// <summary>Writes a text file (UTF-8, created with missing parents). Returns an error or "".</summary>
    public string WriteText(string path, string text) => WriteBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text));

    /// <summary>A file's contents, or null.</summary>
    public byte[]? ReadBytes(string path)
    {
        string? full = Resolve(path, out _);
        if (full is null || !File.Exists(full) || new FileInfo(full).Length > MaxFileSize)
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a file (created with missing parents; replaced safely). Returns an error or "".</summary>
    public string WriteBytes(string path, byte[] data) => Run(path, full =>
    {
        if (data.Length > MaxFileSize)
        {
            return $"files are at most {MaxFileSize >> 20} MiB";
        }

        if (Directory.Exists(full))
        {
            return "a directory with that name exists";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, full, overwrite: true);
        return string.Empty;
    });

    private string Run(string path, Func<string, string> action)
    {
        string? full = Resolve(path, out string error);
        if (full is null)
        {
            return error;
        }

        try
        {
            return action(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
