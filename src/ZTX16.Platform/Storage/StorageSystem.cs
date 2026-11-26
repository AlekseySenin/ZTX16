using System.Globalization;
using System.Text;

namespace ZTX16.Platform.Storage;

/// <summary>
/// Where Storage Entries of one Game ID are kept. The platform validates every call before it reaches a
/// backend, so backends only store and enumerate.
/// </summary>
public interface IStorageBackend
{
    /// <summary>The entry's data, or null if it does not exist.</summary>
    byte[]? Read(string type, string name);

    /// <summary>Creates or replaces an entry.</summary>
    void Write(string type, string name, byte[] data);

    /// <summary>Removes an entry. Returns false if it did not exist.</summary>
    bool Delete(string type, string name);

    /// <summary>All entries as (type, name, size).</summary>
    IReadOnlyList<(string Type, string Name, int Size)> List();
}

/// <summary>Entries kept in memory only; for tests and programs without a Game ID.</summary>
public sealed class MemoryStorage : IStorageBackend
{
    private readonly Dictionary<(string, string), byte[]> _entries = new();

    /// <inheritdoc />
    public byte[]? Read(string type, string name) => _entries.TryGetValue((type, name), out byte[]? data) ? data.ToArray() : null;

    /// <inheritdoc />
    public void Write(string type, string name, byte[] data) => _entries[(type, name)] = data.ToArray();

    /// <inheritdoc />
    public bool Delete(string type, string name) => _entries.Remove((type, name));

    /// <inheritdoc />
    public IReadOnlyList<(string Type, string Name, int Size)> List() =>
        _entries.Select(e => (e.Key.Item1, e.Key.Item2, e.Value.Length)).ToList();
}

/// <summary>
/// Entries kept on disk: <c>root/&lt;type&gt;/&lt;name&gt;.entry</c>. Types and names are escaped so that any
/// valid name maps to a safe, case-distinct file name (everything except <c>a-z 0-9 _ -</c> becomes
/// <c>~XXXX</c>). Writes go to a temporary file first and then replace the entry, so an entry is never
/// left half-written.
/// </summary>
public sealed class DirectoryStorage : IStorageBackend
{
    private const string Extension = ".entry";

    /// <summary>Uses (and creates on first write) <paramref name="root"/>.</summary>
    public DirectoryStorage(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>The directory of this Game ID.</summary>
    public string Root { get; }

    /// <inheritdoc />
    public byte[]? Read(string type, string name)
    {
        string path = PathOf(type, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <inheritdoc />
    public void Write(string type, string name, byte[] data)
    {
        string path = PathOf(type, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    /// <inheritdoc />
    public bool Delete(string type, string name)
    {
        string path = PathOf(type, name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }

        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Type, string Name, int Size)> List()
    {
        var result = new List<(string, string, int)>();
        if (!Directory.Exists(Root))
        {
            return result;
        }

        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            string? type = Unescape(Path.GetFileName(directory));
            if (type is null)
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*" + Extension))
            {
                string? name = Unescape(Path.GetFileNameWithoutExtension(file));
                if (name is not null)
                {
                    result.Add((type, name, (int)Math.Min(int.MaxValue, new FileInfo(file).Length)));
                }
            }
        }

        return result;
    }

    /// <summary>Escapes a type, name or Game ID into a file name.</summary>
    public static string Escape(string value)
    {
        var text = new StringBuilder();
        foreach (char c in value)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
            {
                text.Append(c);
            }
            else
            {
                text.Append('~').Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    /// <summary>Reverses <see cref="Escape"/>; null for a file name it did not produce.</summary>
    public static string? Unescape(string file)
    {
        var text = new StringBuilder();
        for (int i = 0; i < file.Length; i++)
        {
            char c = file[i];
            if (c == '~')
            {
                if (i + 4 >= file.Length)
                {
                    return null;
                }

                if (!int.TryParse(file.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                {
                    return null;
                }

                text.Append((char)code);
                i += 4;
            }
            else if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
            {
                text.Append(c);
            }
            else
            {
                return null;
            }
        }

        return text.Length == 0 ? null : text.ToString();
    }

    private string PathOf(string type, string name) => Path.Combine(Root, Escape(type), Escape(name) + Extension);
}

/// <summary>
/// ZTX16 Storage for the running cartridge: persistent Storage Entries (type, name, opaque data) in the
/// cartridge's own Game ID namespace. It validates names and enforces the limits before the backend is used.
/// </summary>
/// <remarks>
/// Provisional limits: type and name 1..64 characters without control characters; an entry up to 1 MiB; at
/// most 1024 entries and 16 MiB in total per Game ID.
/// </remarks>
public sealed class StorageSystem
{
    /// <summary>Longest type or name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Largest entry in bytes.</summary>
    public const int MaxEntrySize = 1 << 20;

    /// <summary>Most entries per Game ID.</summary>
    public const int MaxEntries = 1024;

    /// <summary>Most bytes per Game ID.</summary>
    public const long MaxTotalSize = 16L << 20;

    private readonly IStorageBackend _backend;

    /// <summary>Creates storage over a backend (in memory if none is given).</summary>
    public StorageSystem(IStorageBackend? backend = null)
    {
        _backend = backend ?? new MemoryStorage();
    }

    /// <summary>True if entries survive the session (not kept in memory only).</summary>
    public bool IsPersistent => _backend is not MemoryStorage;

    /// <summary>Creates or replaces an entry.</summary>
    public void Write(string type, string name, byte[] data)
    {
        Check(type, name);
        if (data.Length > MaxEntrySize)
        {
            throw new ArgumentException($"A Storage Entry holds at most {MaxEntrySize} bytes ({data.Length} given).");
        }

        IReadOnlyList<(string Type, string Name, int Size)> entries = _backend.List();
        int existing = -1;
        long total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            total += entries[i].Size;
            if (entries[i].Type == type && entries[i].Name == name)
            {
                existing = i;
            }
        }

        if (existing < 0 && entries.Count >= MaxEntries)
        {
            throw new InvalidOperationException($"Storage is full: at most {MaxEntries} entries per game.");
        }

        long after = total - (existing >= 0 ? entries[existing].Size : 0) + data.Length;
        if (after > MaxTotalSize)
        {
            throw new InvalidOperationException($"Storage is full: at most {MaxTotalSize >> 20} MiB per game.");
        }

        _backend.Write(type, name, data);
    }

    /// <summary>The entry's data, or null.</summary>
    public byte[]? Read(string type, string name)
    {
        Check(type, name);
        return _backend.Read(type, name);
    }

    /// <summary>True if the entry exists.</summary>
    public bool Exists(string type, string name)
    {
        Check(type, name);
        return _backend.Read(type, name) is not null;
    }

    /// <summary>Removes an entry; false if it did not exist.</summary>
    public bool Delete(string type, string name)
    {
        Check(type, name);
        return _backend.Delete(type, name);
    }

    /// <summary>Entry types in use, sorted (ordinal).</summary>
    public IReadOnlyList<string> Types() =>
        _backend.List().Select(e => e.Type).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Names of the entries of <paramref name="type"/>, sorted (ordinal).</summary>
    public IReadOnlyList<string> Names(string type)
    {
        CheckName(type, "type");
        return _backend.List().Where(e => e.Type == type).Select(e => e.Name).Order(StringComparer.Ordinal).ToList();
    }

    private static void Check(string type, string name)
    {
        CheckName(type, "type");
        CheckName(name, "name");
    }

    private static void CheckName(string value, string what)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxNameLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"A Storage Entry {what} must be 1..{MaxNameLength} characters without control characters.");
        }
    }
}
