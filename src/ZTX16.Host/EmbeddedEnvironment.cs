using System.Reflection;
using ZTX16.Toolchain;

namespace ZTX16.Host;

/// <summary>
/// The environment files embedded in ZTX16.exe: system apps (<c>system/*.ztx</c>), the core library sources
/// (<c>core/*.cs</c>) and reference assemblies (<c>ref/*.dll</c>). They make the executable work on its own;
/// a <c>system/</c> folder next to it still takes precedence (custom editors).
/// </summary>
internal static class EmbeddedEnvironment
{
    private static readonly Assembly Host = typeof(EmbeddedEnvironment).Assembly;

    /// <summary>Names of the embedded system apps (lower case, without extension).</summary>
    public static string[] SystemApps() => Host.GetManifestResourceNames()
        .Where(n => n.StartsWith("system/", StringComparison.Ordinal) && n.EndsWith(".ztx", StringComparison.OrdinalIgnoreCase))
        .Select(n => Path.GetFileNameWithoutExtension(n).ToLowerInvariant())
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>The bytes of an embedded system app.</summary>
    public static byte[] SystemApp(string name) => Read($"system/{name}.ztx")
        ?? throw new InvalidOperationException($"The system app '{name}' is not embedded.");

    /// <summary>
    /// Makes cartridge builds work when the toolchain files are not next to the executable (a single-file
    /// ZTX16.exe on its own): unpacks the embedded core sources and reference assemblies once per build of the
    /// host into %LOCALAPPDATA%/ZTX16/toolchain/&lt;id&gt; and points the toolchain at them.
    /// </summary>
    public static void PrepareToolchain()
    {
#pragma warning disable IL3000 // Empty in a single-file host: that is what this checks.
        bool hasLocation = !string.IsNullOrEmpty(typeof(SyscallAttribute).Assembly.Location);
#pragma warning restore IL3000
        bool hasCore = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "core"));
        bool hasRef = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "ref"));
        if (hasCore && (hasLocation || hasRef))
        {
            return;
        }

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZTX16", "toolchain",
            Host.ManifestModule.ModuleVersionId.ToString("N"));
        string done = Path.Combine(root, ".complete");
        if (!File.Exists(done))
        {
            foreach (string name in Host.GetManifestResourceNames())
            {
                if (name.StartsWith("core/", StringComparison.Ordinal) || name.StartsWith("ref/", StringComparison.Ordinal))
                {
                    string target = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, Read(name)!);
                }
            }

            File.WriteAllText(done, "ok");
        }

        if (!hasCore)
        {
            ProjectBuilder.CoreDirectoryOverride = Path.Combine(root, "core");
        }

        if (!hasLocation && !hasRef)
        {
            ProjectBuilder.ReferenceDirectory = Path.Combine(root, "ref");
        }
    }

    private static byte[]? Read(string name)
    {
        using Stream? stream = Host.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
