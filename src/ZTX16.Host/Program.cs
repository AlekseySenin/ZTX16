using System.Globalization;
using ZTX16.Platform.Execution;
using ZTX16.Host;
using ZTX16.Host.Diagnostics;
using ZTX16.Platform.Runtime;
using ZTX16.VM;
using ZTX16.Cartridge;

// ZTX16.exe is a GUI program: double-clicking it opens no console. Started from a terminal it attaches to
// that terminal; otherwise errors are shown in message boxes.
HostShell.AttachConsole();

HostOptions options;
IRuntimeProgram program;
ResourceStore? resources = null;
ZTX16.Platform.Storage.StorageSystem? storage = null;
string title = "ZTX16";
try
{
    options = HostOptions.Parse(args);
    if (options.Program == HostProgram.Picker)
    {
        // Started without a cartridge (for example by double-clicking the executable): ask for one.
        string? picked = HostShell.PickCartridge();
        if (picked is null)
        {
            return 0;
        }

        options = options with { Program = HostProgram.Executable, ExecutablePath = picked };
    }

    program = options.Program switch
    {
        HostProgram.VmDemo => VmProgram.Load(VmDemoCartridge.BuildExecutable()),
        HostProgram.Executable when options.ExecutablePath!.EndsWith(CartridgeFile.Extension, StringComparison.OrdinalIgnoreCase)
            => LoadCartridge(options.ExecutablePath, out resources, out title, out storage),
        HostProgram.Executable => VmProgram.Load(File.ReadAllBytes(options.ExecutablePath)),
        _ => new DiagnosticProgram(),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or ExecutableFormatException
    or VerificationException or CartridgeFormatException or ProjectException or LinkException)
{
    HostShell.Error(e.Message);
    return 2;
}

using var host = new SdlHost(options, title);
try
{
    return host.Run(program, resources, storage);
}
catch (Exception e) when (e is VmTrapException or LinkException)
{
    // Unhandled cartridge failure: the session ends without Deinitialize.
    HostShell.Error($"The cartridge failed: {e.Message}");
    return 1;
}
catch (InvalidOperationException e)
{
    HostShell.Error(e.Message);
    return 3;
}

// Loads a packed cartridge: the executable runs in the VM, the resources go to the session.
static IRuntimeProgram LoadCartridge(string path, out ResourceStore? resources, out string title, out ZTX16.Platform.Storage.StorageSystem storage)
{
    CartridgeContent cartridge = CartridgeFile.Read(File.ReadAllBytes(path));
    resources = new ResourceStore(cartridge.Resources);
    string name = cartridge.Config.Name;
    title = string.IsNullOrWhiteSpace(name) ? "ZTX16" : $"{name} — ZTX16";

    // Storage lives in the user profile, one directory per Game ID: %LOCALAPPDATA%/ZTX16/storage/<game id>.
    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZTX16", "storage",
        ZTX16.Platform.Storage.DirectoryStorage.Escape(cartridge.Config.GameId));
    storage = new ZTX16.Platform.Storage.StorageSystem(new ZTX16.Platform.Storage.DirectoryStorage(root));
    return VmProgram.Load(cartridge.Executable);
}

namespace ZTX16.Host
{
    /// <summary>What the host runs.</summary>
    internal enum HostProgram
    {
        /// <summary>Built-in host diagnostic (trusted host code).</summary>
        Diagnostic,

        /// <summary>Hand-assembled VM demo executable.</summary>
        VmDemo,

        /// <summary>A ZTX16 executable file from disk.</summary>
        Executable,

        /// <summary>No program given: ask for a cartridge with the system file dialog.</summary>
        Picker,
    }

    /// <summary>Command-line options of the reference host.</summary>
    internal sealed record HostOptions(int Scale, bool Fullscreen, HostProgram Program, string? ExecutablePath)
    {
        public const int MinScale = 1;
        public const int MaxScale = 8;

        private const string Usage =
            "Usage: ZTX16 [--scale N] [--fullscreen] [<file.ztx | file.ztxe> | --exe <file> | --vm-demo | --diagnostic]";

        public static HostOptions Parse(string[] args)
        {
            int scale = 3;
            bool fullscreen = false;
            HostProgram program = HostProgram.Picker;
            string? executable = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--fullscreen":
                        fullscreen = true;
                        break;
                    case "--scale" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out scale))
                        {
                            throw new ArgumentException($"Invalid scale '{args[i]}'. {Usage}");
                        }

                        break;
                    case "--vm-demo":
                        program = HostProgram.VmDemo;
                        break;
                    case "--diagnostic":
                        program = HostProgram.Diagnostic;
                        break;
                    case "--exe" when i + 1 < args.Length:
                        program = HostProgram.Executable;
                        executable = args[++i];
                        break;
                    case string path when !path.StartsWith("--", StringComparison.Ordinal) && executable is null:
                        // A bare path: "ZTX16 game.ztx", a file dropped on the executable, or "Open with".
                        program = HostProgram.Executable;
                        executable = path;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[i]}'. {Usage}");
                }
            }

            if (scale < MinScale || scale > MaxScale)
            {
                throw new ArgumentException($"Scale must be {MinScale}..{MaxScale}. {Usage}");
            }

            return new HostOptions(scale, fullscreen, program, executable);
        }
    }
}
