using ZTX16.Platform.Authoring;
using ZTX16.Platform.Runtime;
using ZTX16.VM;
using static ZTX16.VM.VmValue;

namespace ZTX16.Platform.Execution;

/// <summary>
/// The authoring syscalls (<c>workspace.*</c>, <c>projects.*</c>, <c>apps.*</c>). They are implemented for every
/// session but enter the syscall table only of a session with an <see cref="AuthoringContext"/> — a system app
/// of the environment. Anything else that imports them fails to link.
/// </summary>
internal static class AuthoringSyscalls
{
    /// <summary>Name prefixes of the authoring syscalls.</summary>
    public static readonly string[] Prefixes = ["workspace.", "projects.", "apps.", "code.", "images."];

    /// <summary>True if <paramref name="name"/> is an authoring syscall.</summary>
    public static bool IsAuthoring(string name) => Prefixes.Any(name.StartsWith);

    public static void Register(RuntimeSession session, Action<string, SyscallHandler> bind)
    {
        AuthoringContext? context = session.Authoring;
        AppEnvironment Env() => context!.Environment;
        Workspace Ws() => context!.Environment.Workspace;

        // Workspace
        bind("workspace.list", (c, a) => Ws().List(Text(c, a[0])) is { } list ? c.NewString(list) : 0);
        bind("workspace.exists", (c, a) => Ws().Exists(Text(c, a[0])) ? 1 : 0);
        bind("workspace.is_directory", (c, a) => Ws().IsDirectory(Text(c, a[0])) ? 1 : 0);
        bind("workspace.size", (c, a) => Ws().Size(Text(c, a[0])));
        bind("workspace.create_directory", (c, a) => c.NewString(Ws().CreateDirectory(Text(c, a[0]))));
        bind("workspace.delete", (c, a) => c.NewString(Ws().Delete(Text(c, a[0]), ToI32(a[1]) != 0)));
        bind("workspace.move", (c, a) => c.NewString(Ws().Move(Text(c, a[0]), Text(c, a[1]))));
        bind("workspace.copy", (c, a) => c.NewString(Ws().Copy(Text(c, a[0]), Text(c, a[1]))));
        bind("workspace.read_text", (c, a) => Ws().ReadText(Text(c, a[0])) is { } text ? c.NewString(text) : 0);
        bind("workspace.write_text", (c, a) => c.NewString(Ws().WriteText(Text(c, a[0]), Text(c, a[1]))));
        bind("workspace.read_bytes", (c, a) => Ws().ReadBytes(Text(c, a[0])) is { } bytes ? c.NewByteArray(bytes) : 0);
        bind("workspace.write_bytes", (c, a) => c.NewString(Ws().WriteBytes(Text(c, a[0]), c.ReadByteArray(a[1]) ?? [])));

        // Projects
        bind("projects.is_project", (c, a) => Ws().Resolve(Text(c, a[0]), out _) is { } dir && Env().Tools.IsProject(dir) ? 1 : 0);
        bind("projects.create", (c, a) =>
        {
            string? dir = Ws().Resolve(Text(c, a[0]), out string error);
            string name = Text(c, a[1]);
            return c.NewString(dir is null ? error : Guard(() => { Env().Tools.Create(dir, name); return string.Empty; }));
        });
        bind("projects.build", (c, a) => Build(Text(c, a[0])) ? 1 : 0);
        bind("projects.output", (c, _) => c.NewString(context!.LastOutput));
        bind("projects.last_cartridge", (c, _) => c.NewString(context!.LastCartridge));
        bind("projects.extract", (c, a) =>
        {
            string? cartridge = Ws().Resolve(Text(c, a[0]), out string error);
            string? dir = cartridge is null ? null : Ws().Resolve(Text(c, a[1]), out error);
            return c.NewString(cartridge is null || dir is null ? error
                : !File.Exists(cartridge) ? "no such cartridge"
                : Guard(() => { Env().Tools.Extract(cartridge, dir); return string.Empty; }));
        });
        bind("projects.run", (c, a) =>
        {
            string path = Text(c, a[0]);
            string? full = Ws().Resolve(path, out string error);
            if (full is null)
            {
                return c.NewString(error);
            }

            if (Directory.Exists(full))
            {
                if (!Build(path))
                {
                    return c.NewString(context!.LastOutput);
                }

                full = Ws().Resolve(context!.LastCartridge, out _)!;
            }
            else if (!File.Exists(full))
            {
                return c.NewString("not found");
            }

            return c.NewString(Env().Run(context!.App, full));
        });

        // Apps
        bind("apps.count", (_, _) => Env().SystemApps.Count);
        bind("apps.name", (c, a) =>
        {
            int index = ToI32(a[0]);
            IReadOnlyList<string> apps = Env().SystemApps;
            return (uint)index < (uint)apps.Count ? c.NewString(apps[index]) : throw new ArgumentOutOfRangeException(nameof(a), index, $"There are {apps.Count} apps.");
        });
        bind("apps.launch", (c, a) => Env().Launch(context!.App, Text(c, a[0]), Text(c, a[1])) ? 1 : 0);
        bind("apps.request", (c, _) =>
        {
            string request = context!.App.Request;
            context.App.Request = string.Empty;
            return c.NewString(request);
        });
        bind("apps.message", (c, _) => c.NewString(context!.App.Messages.TryDequeue(out string? message) ? message : string.Empty));
        bind("apps.back", (_, _) => { Env().Back(context!.App); return 0; });
        bind("apps.get_clipboard", (c, _) => c.NewString(Env().Clipboard.GetText()));
        bind("apps.set_clipboard", (c, a) => { Env().Clipboard.SetText(Text(c, a[0])); return 0; });
        bind("apps.version", (c, _) => c.NewString("ZTX16 " + (typeof(AppEnvironment).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")));

        // Live code analysis (code editor): requests run on a worker thread; the app polls for the result.
        bind("code.analyze", (c, a) =>
        {
            string text = Text(c, a[1]);
            if (Env().CodeAnalysis is not { } analysis || context!.AnalysisTask is { IsCompleted: false } || ProjectFile(Text(c, a[0])) is not var (dir, file))
            {
                return 0;
            }

            context.AnalysisTask = Task.Run(() => analysis.Analyze(dir, file, text));
            return 1;
        });
        bind("code.analysis_ready", (_, _) =>
        {
            if (context!.AnalysisTask is not { IsCompleted: true } task)
            {
                return 0;
            }

            context.Analysis = task.IsCompletedSuccessfully ? task.Result : new CodeAnalysisInfo([], []);
            context.AnalysisTask = null;
            return 1;
        });
        bind("code.problems", (c, _) => c.NewString(string.Join("\n", (context!.Analysis?.Problems ?? []).Select(p =>
            $"{(p.IsError ? 'E' : 'W')}\t{p.Line}\t{p.Column}\t{p.EndLine}\t{p.EndColumn}\t{p.Id}\t{p.Message.Replace('\n', ' ').Replace('\t', ' ')}"))));
        bind("code.colors", (c, _) =>
        {
            IReadOnlyList<CodeColorInfo> colors = context!.Analysis?.Colors ?? [];
            int[] packed = new int[colors.Count * 4];
            for (int i = 0; i < colors.Count; i++)
            {
                (packed[i * 4], packed[(i * 4) + 1], packed[(i * 4) + 2], packed[(i * 4) + 3]) = (colors[i].Line, colors[i].Column, colors[i].Length, colors[i].Kind);
            }

            return c.NewInt32Array(packed);
        });
        bind("code.complete", (c, a) =>
        {
            string text = Text(c, a[1]);
            int line = ToI32(a[2]), column = ToI32(a[3]);
            if (Env().CodeAnalysis is not { } analysis || context!.CompletionTask is { IsCompleted: false } || ProjectFile(Text(c, a[0])) is not var (dir, file))
            {
                return 0;
            }

            context.CompletionTask = Task.Run(() => analysis.Complete(dir, file, text, line, column));
            return 1;
        });
        bind("code.completion_ready", (_, _) =>
        {
            if (context!.CompletionTask is not { IsCompleted: true } task)
            {
                return 0;
            }

            context.Completion = task.IsCompletedSuccessfully ? task.Result : new CodeCompletionInfo(0, []);
            context.CompletionTask = null;
            return 1;
        });
        bind("code.completion", (c, _) =>
        {
            CodeCompletionInfo? completion = context!.Completion;
            if (completion is null)
            {
                return c.NewString("");
            }

            IEnumerable<string> items = completion.Items.Select(i => $"{i.Kind}\t{i.Label}\t{i.Detail.Replace('\n', ' ').Replace('\t', ' ')}");
            return c.NewString(string.Join("\n", items.Prepend(completion.StartColumn.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        });

        // (project directory, project-relative file) of a workspace file, or null if it is not in a project.
        (string Directory, string File)? ProjectFile(string path)
        {
            string? full = Ws().Resolve(path, out _);
            string? dir = full is null ? null : System.IO.Path.GetDirectoryName(full);
            while (dir is not null && dir.Length >= Ws().Root.Length)
            {
                if (Env().Tools.IsProject(dir))
                {
                    return (dir, System.IO.Path.GetRelativePath(dir, full!).Replace('\\', '/'));
                }

                dir = System.IO.Path.GetDirectoryName(dir);
            }

            return null;
        }

        // Host file dialogs (import/export): the app never sees host paths, only the chosen file's name.
        bind("apps.open_dialog", (c, a) => StartDialog(Text(c, a[0]), null));
        bind("apps.save_dialog", (c, a) => StartDialog(Text(c, a[1]), Text(c, a[0])));
        bind("apps.dialog_state", (_, _) => context!.DialogState);
        bind("apps.dialog_name", (c, _) => c.NewString(context!.DialogPath is { } chosen ? System.IO.Path.GetFileName(chosen) : ""));

        // Images: tilemap PNGs in the workspace, import/export through the dialogs.
        bind("images.load_tilemap", (c, a) =>
        {
            byte[]? png = Ws().ReadBytes(Text(c, a[0]));
            if (png is null)
            {
                context!.ImageError = "cannot read the file";
                return 0;
            }

            try
            {
                return c.NewByteArray(ZTX16.Cartridge.TilemapFormat.Pixels(ZTX16.Cartridge.TilemapFormat.FromPng(png, Text(c, a[0])), "tilemap"));
            }
            catch (ZTX16.Cartridge.ProjectException e)
            {
                context!.ImageError = e.Message;
                return 0;
            }
        });
        bind("images.save_tilemap", (c, a) =>
        {
            byte[]? png = TilemapPng(c.ReadByteArray(a[1]), c.ReadByteArray(a[2]));
            return c.NewString(png is null ? context!.ImageError : Ws().WriteBytes(Text(c, a[0]), png));
        });
        bind("images.import", (c, a) =>
        {
            byte[]? palette = c.ReadByteArray(a[0]);
            if (context!.DialogState != 1 || context.DialogPath is not { } chosen || palette is not { Length: 96 })
            {
                context.ImageError = "no file chosen";
                return 0;
            }

            try
            {
                byte[] indices = ZTX16.Cartridge.PngImport.ToIndices(File.ReadAllBytes(chosen), palette, out int width, out int height);
                (context.ImportWidth, context.ImportHeight) = (width, height);
                return c.NewByteArray(indices);
            }
            catch (Exception e) when (e is ZTX16.Cartridge.ProjectException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                context.ImageError = e.Message;
                return 0;
            }
        });
        bind("images.import_width", (_, _) => context!.ImportWidth);
        bind("images.import_height", (_, _) => context!.ImportHeight);
        bind("images.export", (c, a) =>
        {
            if (context!.DialogState != 1 || context.DialogPath is not { } chosen)
            {
                return c.NewString("no file chosen");
            }

            byte[]? png = TilemapPng(c.ReadByteArray(a[0]), c.ReadByteArray(a[1]));
            if (png is null)
            {
                return c.NewString(context.ImageError);
            }

            try
            {
                File.WriteAllBytes(chosen, png);
                return c.NewString("");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return c.NewString(e.Message);
            }
        });
        bind("images.error", (c, _) => c.NewString(context!.ImageError));

        // A 256×256 indexed PNG from tilemap pixels (row 0 at the bottom) and a 32 × RGB palette.
        byte[]? TilemapPng(byte[]? pixels, byte[]? palette)
        {
            if (pixels is not { Length: 65536 } || palette is not { Length: 96 })
            {
                context!.ImageError = "a tilemap is 256×256 pixels with a 32-colour palette";
                return null;
            }

            byte[] data = new byte[65536 + 96];
            pixels.CopyTo(data, 0);
            palette.CopyTo(data, 65536);
            return ZTX16.Cartridge.TilemapFormat.ToPng(data, "tilemap");
        }

        long StartDialog(string extensions, string? defaultName)
        {
            if (Env().Dialogs is not { } dialogs || context!.DialogState == 0)
            {
                return 0;
            }

            string[] list = extensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            context.DialogPath = null;
            context.DialogState = 0;
            AuthoringContext target = context;
            void Done(string? path)
            {
                target.DialogPath = path;
                target.DialogState = path is null ? 2 : 1;
            }

            if (defaultName is null)
            {
                dialogs.Open(list, Done);
            }
            else
            {
                dialogs.Save(defaultName, list, Done);
            }

            return 1;
        }

        bool Build(string path)
        {
            string? dir = Ws().Resolve(path, out string error);
            context!.LastCartridge = string.Empty;
            if (dir is null || !Directory.Exists(dir) || !Env().Tools.IsProject(dir))
            {
                context.LastOutput = dir is null ? error : "not a project directory";
                return false;
            }

            try
            {
                (string cartridge, string message) = Env().Tools.Build(dir);
                string virtualCartridge = Ws().ToVirtual(cartridge) ?? string.Empty;
                context.LastOutput = virtualCartridge.Length > 0 ? message.Replace(cartridge, virtualCartridge, StringComparison.OrdinalIgnoreCase) : message;
                context.LastCartridge = virtualCartridge;
                return true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                context.LastOutput = e.Message;
                return false;
            }
        }
    }

    private static string Text(ISyscallContext context, long slot) => context.ReadString(slot) ?? string.Empty;

    private static string Guard(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return e.Message;
        }
    }
}
