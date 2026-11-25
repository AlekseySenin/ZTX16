using System.Text;

namespace ZTX16.VM;

/// <summary>Thrown when executable bytes are malformed or of an unsupported version.</summary>
public sealed class ExecutableFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ExecutableFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Binary serialization of <see cref="VmModule"/>: the ZTX16 executable format.
/// </summary>
/// <remarks>
/// Layout (little-endian):
/// <code>
/// magic "ZTXE" | u16 formatVersion
/// u16 globalCount   { u8 type }
/// u16 importCount   { str name, sig }
/// u16 functionCount { str name, sig, u16 localCount { u8 type }, u32 codeLength, code }
/// u16 exportCount   { str name, u16 functionIndex }
/// sig = u8 paramCount { u8 type }, u8 resultType (0 = none)
/// str = u16 byteLength, UTF-8 bytes
/// </code>
/// This layout is an implementation detail and is versioned by <see cref="FormatVersion"/>.
/// </remarks>
public static class ExecutableFormat
{
    /// <summary>Current executable format version.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>Upper bound on a single function's code size.</summary>
    public const int MaxCodeLength = 1 << 24;

    private static readonly byte[] Magic = "ZTXE"u8.ToArray();

    /// <summary>Serializes <paramref name="module"/>.</summary>
    public static byte[] Write(VmModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);

            WriteCount(writer, module.Globals.Count);
            foreach (VmType type in module.Globals)
            {
                writer.Write((byte)type);
            }

            WriteCount(writer, module.Imports.Count);
            foreach (VmImport import in module.Imports)
            {
                WriteString(writer, import.Name);
                WriteSignature(writer, import.Signature);
            }

            WriteCount(writer, module.Functions.Count);
            foreach (VmFunction function in module.Functions)
            {
                WriteString(writer, function.Name);
                WriteSignature(writer, function.Signature);
                WriteCount(writer, function.Locals.Count);
                foreach (VmType type in function.Locals)
                {
                    writer.Write((byte)type);
                }

                writer.Write((uint)function.Code.Length);
                writer.Write(function.Code);
            }

            WriteCount(writer, module.Exports.Count);
            foreach ((string name, int index) in module.Exports.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                WriteString(writer, name);
                WriteCount(writer, index);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Deserializes an executable. The result still needs verification before execution.</summary>
    /// <exception cref="ExecutableFormatException">The bytes are malformed or the version is unsupported.</exception>
    public static VmModule Read(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        try
        {
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                throw new ExecutableFormatException("Not a ZTX16 executable (bad magic).");
            }

            ushort version = reader.ReadUInt16();
            if (version != FormatVersion)
            {
                throw new ExecutableFormatException($"Unsupported executable format version {version}; expected {FormatVersion}.");
            }

            var globals = new VmType[reader.ReadUInt16()];
            for (int i = 0; i < globals.Length; i++)
            {
                globals[i] = (VmType)reader.ReadByte();
            }

            var imports = new VmImport[reader.ReadUInt16()];
            for (int i = 0; i < imports.Length; i++)
            {
                imports[i] = new VmImport(ReadString(reader), ReadSignature(reader));
            }

            var functions = new VmFunction[reader.ReadUInt16()];
            for (int i = 0; i < functions.Length; i++)
            {
                string name = ReadString(reader);
                VmSignature signature = ReadSignature(reader);
                var locals = new VmType[reader.ReadUInt16()];
                for (int l = 0; l < locals.Length; l++)
                {
                    locals[l] = (VmType)reader.ReadByte();
                }

                uint length = reader.ReadUInt32();
                if (length > MaxCodeLength)
                {
                    throw new ExecutableFormatException($"Function '{name}' code is too large ({length} bytes).");
                }

                byte[] code = reader.ReadBytes((int)length);
                if (code.Length != length)
                {
                    throw new EndOfStreamException();
                }

                functions[i] = new VmFunction(name, signature, locals, code);
            }

            int exportCount = reader.ReadUInt16();
            var exports = new Dictionary<string, int>(exportCount, StringComparer.Ordinal);
            for (int i = 0; i < exportCount; i++)
            {
                string name = ReadString(reader);
                if (!exports.TryAdd(name, reader.ReadUInt16()))
                {
                    throw new ExecutableFormatException($"Duplicate export '{name}'.");
                }
            }

            if (stream.Position != stream.Length)
            {
                throw new ExecutableFormatException("Trailing bytes after executable.");
            }

            return new VmModule(globals, imports, functions, exports);
        }
        catch (EndOfStreamException e)
        {
            throw new ExecutableFormatException("Executable is truncated.", e);
        }
        catch (DecoderFallbackException e)
        {
            throw new ExecutableFormatException("Executable contains an invalid UTF-8 name.", e);
        }
    }

    private static void WriteCount(BinaryWriter writer, int count)
    {
        if ((uint)count > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Count {count} exceeds the format limit of {ushort.MaxValue}.");
        }

        writer.Write((ushort)count);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        WriteCount(writer, utf8.Length);
        writer.Write(utf8);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        byte[] utf8 = reader.ReadBytes(length);
        if (utf8.Length != length)
        {
            throw new EndOfStreamException();
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(utf8);
    }

    private static void WriteSignature(BinaryWriter writer, VmSignature signature)
    {
        if (signature.Parameters.Count > byte.MaxValue)
        {
            throw new InvalidOperationException("Too many parameters.");
        }

        writer.Write((byte)signature.Parameters.Count);
        foreach (VmType type in signature.Parameters)
        {
            writer.Write((byte)type);
        }

        writer.Write(signature.Result is { } result ? (byte)result : (byte)0);
    }

    private static VmSignature ReadSignature(BinaryReader reader)
    {
        var parameters = new VmType[reader.ReadByte()];
        for (int i = 0; i < parameters.Length; i++)
        {
            parameters[i] = (VmType)reader.ReadByte();
        }

        byte result = reader.ReadByte();
        return new VmSignature(parameters, result == 0 ? null : (VmType)result);
    }
}
