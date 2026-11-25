using ZTX16.Toolchain;

// ztx16c <cartridge.dll> [-o <output.ztxe>]
if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "-o"))
{
    Console.Error.WriteLine("Usage: ztx16c <cartridge.dll> [-o <output.ztxe>]");
    return 2;
}

string input = args[0];
string output = args.Length == 3 ? args[2] : Path.ChangeExtension(input, ".ztxe");

try
{
    byte[] executable = IlTranslator.TranslateToExecutable(File.ReadAllBytes(input));
    File.WriteAllBytes(output, executable);
    Console.WriteLine($"ztx16c: {Path.GetFileName(input)} -> {output} ({executable.Length} bytes)");
    return 0;
}
catch (TranslationException e)
{
    // MSBuild-recognizable error format.
    Console.Error.WriteLine($"{input}: error ZTX0001: {e.Message}");
    return 1;
}
catch (IOException e)
{
    Console.Error.WriteLine($"ztx16c: {e.Message}");
    return 2;
}
