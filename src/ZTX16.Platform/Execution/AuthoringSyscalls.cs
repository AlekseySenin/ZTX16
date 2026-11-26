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
    public static readonly string[] Prefixes = ["workspace.", "projects.", "apps."];

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
