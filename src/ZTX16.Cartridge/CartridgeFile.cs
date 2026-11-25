using System.IO.Compression;
using System.Text;

namespace ZTX16.Cartridge;

/// <summary>A packaged resource.</summary>
public sealed record CartridgeResource(ResourceType Type, string Name, byte[] Data);

/// <summary>A packaged project source file (kept so the cartridge can be extracted back into a project).</summary>
public sealed record CartridgeSource(string Path, byte[] Data);

/// <summary>Everything a cartridge contains.</summary>
/// <param name="ConfigJson">The Project Config text, verbatim.</param>
/// <param name="Executable">The ZTX16 executable.</param>
/// <param name="Resources">Resources in type then name order.</param>
/// <param name="Sources">Project source files.</param>
public sealed record CartridgeContent(
    string ConfigJson,
    byte[] Executable,
    IReadOnlyList<CartridgeResource> Resources,
    IReadOnlyList<CartridgeSource> Sources)
{
    /// <summary>The parsed config.</summary>
    public ProjectConfig Config => ProjectConfig.Parse(ConfigJson);
}

/// <summary>Thrown when cartridge bytes are malformed or of an unsupported version.</summary>
public sealed class CartridgeFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CartridgeFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The packed cartridge file (<c>.ztx</c>): one self-contained file with the executable,
/// resources, metadata and project sources.
/// </summary>
/// <remarks>
/// Layout: <c>"ZTXC" | u16 version | deflate( str config | blob executable | u32 n { u8 type, str name, blob data }
/// | u32 n { str path, blob data } )</c>, where <c>str</c> is u32 length + UTF-8 and <c>blob</c> is u32 length +
/// bytes. The layout is an implementation detail versioned by <see cref="FormatVersion"/>.
/// </remarks>
public static class CartridgeFile
{
    /// <summary>File extension of cartridges.</summary>
    public const string Extension = ".ztx";

    /// <summary>Current format version.</summary>
    public const ushort FormatVersion = 1;

    private const int MaxBlob = 64 * 1024 * 1024;
    private static readonly byte[] Magic = "ZTXC"u8.ToArray();

    /// <summary>Serializes a cartridge.</summary>
    public static byte[] Write(CartridgeContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(BitConverter.GetBytes(FormatVersion));

        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate, Encoding.UTF8))
        {
            WriteString(writer, content.ConfigJson);
            WriteBlob(writer, content.Executable);

            writer.Write((uint)content.Resources.Count);
            foreach (CartridgeResource resource in content.Resources)
            {
                writer.Write((byte)resource.Type);
                WriteString(writer, resource.Name);
                WriteBlob(writer, resource.Data);
            }

            writer.Write((uint)content.Sources.Count);
            foreach (CartridgeSource source in content.Sources)
            {
                WriteString(writer, source.Path);
                WriteBlob(writer, source.Data);
            }
        }

        return output.ToArray();
    }

    /// <summary>Deserializes a cartridge.</summary>
    /// <exception cref="CartridgeFormatException">The bytes are not a valid cartridge.</exception>
    public static CartridgeContent Read(byte[] bytes)
    {
        if (bytes.Length < 6 || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new CartridgeFormatException("Not a ZTX16 cartridge (bad magic).");
        }

        ushort version = BitConverter.ToUInt16(bytes, 4);
        if (version != FormatVersion)
        {
            throw new CartridgeFormatException($"Unsupported cartridge format version {version}; expected {FormatVersion}.");
        }

        try
        {
            using var deflate = new DeflateStream(new MemoryStream(bytes, 6, bytes.Length - 6), CompressionMode.Decompress);
            using var reader = new BinaryReader(deflate, Encoding.UTF8);

            string config = ReadString(reader);
            byte[] executable = ReadBlob(reader);

            uint resourceCount = reader.ReadUInt32();
            var resources = new List<CartridgeResource>();
            for (uint i = 0; i < resourceCount; i++)
            {
                byte type = reader.ReadByte();
                if (!Enum.IsDefined((ResourceType)type))
                {
                    throw new CartridgeFormatException($"Unknown resource type {type}.");
                }

                resources.Add(new CartridgeResource((ResourceType)type, ReadString(reader), ReadBlob(reader)));
            }

            uint sourceCount = reader.ReadUInt32();
            var sources = new List<CartridgeSource>();
            for (uint i = 0; i < sourceCount; i++)
            {
                sources.Add(new CartridgeSource(ReadString(reader), ReadBlob(reader)));
            }

            return new CartridgeContent(config, executable, resources, sources);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or DecoderFallbackException)
        {
            throw new CartridgeFormatException("Cartridge is truncated or corrupted.", e);
        }
    }

    private static void WriteString(BinaryWriter writer, string value) => WriteBlob(writer, Encoding.UTF8.GetBytes(value));

    private static void WriteBlob(BinaryWriter writer, byte[] data)
    {
        writer.Write((uint)data.Length);
        writer.Write(data);
    }

    private static string ReadString(BinaryReader reader) => new UTF8Encoding(false, true).GetString(ReadBlob(reader));

    private static byte[] ReadBlob(BinaryReader reader)
    {
        uint length = reader.ReadUInt32();
        if (length > MaxBlob)
        {
            throw new CartridgeFormatException($"Cartridge entry too large ({length} bytes).");
        }

        byte[] data = reader.ReadBytes((int)length);
        return data.Length == length ? data : throw new EndOfStreamException();
    }
}
