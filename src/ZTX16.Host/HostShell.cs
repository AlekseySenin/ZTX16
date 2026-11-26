using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SDL;
using static SDL.SDL3;

namespace ZTX16.Host;

/// <summary>
/// How the host talks to the person who started it: the console when there is one (started from a terminal),
/// otherwise message boxes; and the cartridge picker shown when the host starts without arguments.
/// </summary>
internal static unsafe partial class HostShell
{
    private const int AttachParentProcess = -1;

    private static string? _picked;
    private static volatile bool _pickerDone;

    /// <summary>True if output reaches a console (the host was started from a terminal).</summary>
    public static bool HasConsole { get; private set; }

    /// <summary>
    /// The host is a GUI executable, so double-clicking it opens no console window. When it was started from a
    /// terminal, attach to that terminal so messages still appear there.
    /// </summary>
    public static void AttachConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            HasConsole = true;
            return;
        }

        HasConsole = AttachConsoleNative(AttachParentProcess) || GetConsoleWindow() != IntPtr.Zero;
        if (HasConsole)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            Console.Error.WriteLine();
        }
    }

    /// <summary>Reports an error: on the console, or in a message box when there is none.</summary>
    public static void Error(string message)
    {
        if (HasConsole)
        {
            Console.Error.WriteLine($"ZTX16: {message}");
            return;
        }

        SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "ZTX16", message, null);
    }

    /// <summary>
    /// Shows the system "open file" dialog for a cartridge. Returns the chosen path, or null if cancelled.
    /// It starts in the <c>cartridges</c> folder next to the executable when there is one.
    /// </summary>
    public static string? PickCartridge()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Error($"Cannot open the cartridge picker: {SDL_GetError()}");
            return null;
        }

        IntPtr name = Marshal.StringToCoTaskMemUTF8("ZTX16 cartridges");
        IntPtr pattern = Marshal.StringToCoTaskMemUTF8("ztx;ztxe");
        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "cartridges");
            string start = (Directory.Exists(folder) ? folder : AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var filter = new SDL_DialogFileFilter { name = (byte*)name, pattern = (byte*)pattern };
            _picked = null;
            _pickerDone = false;
            SDL_ShowOpenFileDialog(&OnPicked, IntPtr.Zero, null, &filter, 1, start, false);
            while (!_pickerDone)
            {
                SDL_WaitEventTimeout(null, 50);
            }

            return _picked;
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(pattern);
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_VIDEO);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPicked(IntPtr userdata, byte** files, int filter)
    {
        if (files != null && files[0] != null)
        {
            int length = 0;
            while (files[0][length] != 0)
            {
                length++;
            }

            _picked = Encoding.UTF8.GetString(files[0], length);
        }

        _pickerDone = true;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsoleNative(int processId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();
}
