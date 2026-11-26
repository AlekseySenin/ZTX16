using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SDL;
using ZTX16.Platform.Authoring;
using static SDL.SDL3;

namespace ZTX16.Host;

/// <summary>
/// The host's "open" and "save" dialogs through SDL3 (system apps' import and export). SDL calls back on
/// another thread, possibly after the call returns; the result goes to the waiting app.
/// </summary>
internal sealed unsafe class SdlFileDialogs : IHostDialogs
{
    private readonly SDL_Window* _window;

    public SdlFileDialogs(SDL_Window* window)
    {
        _window = window;
    }

    public void Open(string[] extensions, Action<string?> done) => Show(extensions, null, done);

    public void Save(string defaultName, string[] extensions, Action<string?> done) => Show(extensions, defaultName, done);

    private void Show(string[] extensions, string? defaultName, Action<string?> done)
    {
        var request = new Request(done, extensions);
        GCHandle handle = GCHandle.Alloc(request);
        string start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(start))
        {
            start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        string location = defaultName is null ? start + Path.DirectorySeparatorChar : Path.Combine(start, defaultName);
        if (defaultName is null)
        {
            SDL_ShowOpenFileDialog(&OnChosen, GCHandle.ToIntPtr(handle), _window, request.Filter, 1, location, false);
        }
        else
        {
            SDL_ShowSaveFileDialog(&OnChosen, GCHandle.ToIntPtr(handle), _window, request.Filter, 1, location);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnChosen(IntPtr userdata, byte** files, int filter)
    {
        GCHandle handle = GCHandle.FromIntPtr(userdata);
        var request = (Request)handle.Target!;
        string? path = null;
        if (files != null && files[0] != null)
        {
            int length = 0;
            while (files[0][length] != 0)
            {
                length++;
            }

            path = Encoding.UTF8.GetString(files[0], length);
        }

        request.Free();
        handle.Free();
        request.Done(path);
    }

    // Keeps the filter strings alive until the dialog answers.
    private sealed class Request
    {
        private readonly IntPtr _name;
        private readonly IntPtr _pattern;

        public Request(Action<string?> done, string[] extensions)
        {
            Done = done;
            _name = Marshal.StringToCoTaskMemUTF8(string.Join(", ", extensions).ToUpperInvariant() + " files");
            _pattern = Marshal.StringToCoTaskMemUTF8(string.Join(";", extensions));
            // SDL reads the filter until the dialog answers, so it lives in unmanaged memory.
            Filter = (SDL_DialogFileFilter*)Marshal.AllocHGlobal(sizeof(SDL_DialogFileFilter));
            *Filter = new SDL_DialogFileFilter { name = (byte*)_name, pattern = (byte*)_pattern };
        }

        public Action<string?> Done { get; }

        public SDL_DialogFileFilter* Filter { get; }

        public void Free()
        {
            Marshal.FreeCoTaskMem(_name);
            Marshal.FreeCoTaskMem(_pattern);
            Marshal.FreeHGlobal((IntPtr)Filter);
        }
    }
}
