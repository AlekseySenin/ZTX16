using System.Globalization;
using ZTX16.Host;
using ZTX16.Host.Diagnostics;

var options = HostOptions.Parse(args);
using var host = new SdlHost(options);
return host.Run(new DiagnosticProgram());

namespace ZTX16.Host
{
    /// <summary>Command-line options of the reference host.</summary>
    internal sealed record HostOptions(int Scale, bool Fullscreen)
    {
        public const int MinScale = 1;
        public const int MaxScale = 8;

        public static HostOptions Parse(string[] args)
        {
            int scale = 3;
            bool fullscreen = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--fullscreen":
                        fullscreen = true;
                        break;
                    case "--scale" when i + 1 < args.Length:
                        scale = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[i]}'. Usage: [--scale N] [--fullscreen]");
                }
            }

            if (scale < MinScale || scale > MaxScale)
            {
                throw new ArgumentOutOfRangeException(nameof(args), scale, $"Scale must be {MinScale}..{MaxScale}.");
            }

            return new HostOptions(scale, fullscreen);
        }
    }
}
