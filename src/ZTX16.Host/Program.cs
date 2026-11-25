using System.Globalization;
using ZTX16.Platform.Execution;
using ZTX16.Host;
using ZTX16.Host.Diagnostics;
using ZTX16.Platform.Runtime;
using ZTX16.VM;

HostOptions options;
IRuntimeProgram program;
try
{
    options = HostOptions.Parse(args);
    program = options.Program switch
    {
        HostProgram.VmDemo => VmProgram.Load(VmDemoCartridge.BuildExecutable()),
        HostProgram.Executable => VmProgram.Load(File.ReadAllBytes(options.ExecutablePath!)),
        _ => new DiagnosticProgram(),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or ExecutableFormatException or VerificationException)
{
    Console.Error.WriteLine($"ZTX16: {e.Message}");
    return 2;
}

using var host = new SdlHost(options);
try
{
    return host.Run(program);
}
catch (Exception e) when (e is VmTrapException or LinkException)
{
    // Unhandled cartridge failure: the session ends without Deinitialize.
    Console.Error.WriteLine($"ZTX16: cartridge failed: {e.Message}");
    return 1;
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
    }

    /// <summary>Command-line options of the reference host.</summary>
    internal sealed record HostOptions(int Scale, bool Fullscreen, HostProgram Program, string? ExecutablePath)
    {
        public const int MinScale = 1;
        public const int MaxScale = 8;

        private const string Usage = "Usage: [--scale N] [--fullscreen] [--vm-demo | --exe <file>]";

        public static HostOptions Parse(string[] args)
        {
            int scale = 3;
            bool fullscreen = false;
            HostProgram program = HostProgram.Diagnostic;
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
                    case "--exe" when i + 1 < args.Length:
                        program = HostProgram.Executable;
                        executable = args[++i];
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
