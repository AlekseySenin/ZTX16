using ZTX16.Platform.Runtime;
using ZTX16.Platform.Storage;

namespace ZTX16.Platform.Authoring;

/// <summary>Project operations the environment needs from the toolchain (implemented by the host).</summary>
public interface IProjectTools
{
    /// <summary>Creates a project in a host directory. Returns a message.</summary>
    string Create(string directory, string name);

    /// <summary>Builds the project in a host directory into a cartridge file. Returns its host path and a message.</summary>
    (string Path, string Message) Build(string directory);

    /// <summary>Extracts a cartridge file into a host directory. Returns a message.</summary>
    string Extract(string cartridgePath, string directory);

    /// <summary>True if the host directory holds a project.</summary>
    bool IsProject(string directory);
}

/// <summary>A program ready to run: its code, resources, window title and storage.</summary>
public sealed record LoadedProgram(IRuntimeProgram Program, ResourceStore Resources, string Title, StorageSystem Storage);

/// <summary>One running application of the environment.</summary>
public sealed class AppSession
{
    internal AppSession(string name, bool system, RuntimeSession session, string title, AppSession? launcher)
    {
        Name = name;
        IsSystem = system;
        Session = session;
        Title = title;
        Launcher = launcher;
    }

    /// <summary>App name (system apps) or cartridge file name.</summary>
    public string Name { get; }

    /// <summary>True for a system app (Console, editors), which holds the authoring capabilities.</summary>
    public bool IsSystem { get; }

    /// <summary>The runtime session.</summary>
    public RuntimeSession Session { get; }

    /// <summary>Window title.</summary>
    public string Title { get; }

    /// <summary>The app that launched this one; it becomes active again when this one ends.</summary>
    public AppSession? Launcher { get; internal set; }

    /// <summary>Pending open request (an argument such as a file path), consumed by <c>apps.request</c>.</summary>
    internal string Request { get; set; } = string.Empty;

    /// <summary>Pending messages from the environment (for example why a cartridge stopped), consumed by <c>apps.message</c>.</summary>
    internal Queue<string> Messages { get; } = new();
}

/// <summary>
/// The ZTX16 environment: the system apps (Console and editors) as persistent sessions plus at most one
/// running user cartridge. Exactly one app is active — it receives input, ticks, renders and is heard; the
/// others keep their state, untouched, until they are switched to again.
/// </summary>
/// <remarks>
/// Switching is requested during a tick and happens after it (<see cref="AfterTick"/>), so a tick always runs
/// to completion in one app. System apps receive the authoring capabilities (<see cref="Workspace"/>, project
/// tools, app switching); user cartridges never do.
/// </remarks>
public sealed class AppEnvironment
{
    /// <summary>The app the environment starts with and returns to.</summary>
    public const string ShellApp = "console";

    private readonly Dictionary<string, AppSession> _system = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, LoadedProgram> _loadSystemApp;
    private readonly Func<string, LoadedProgram> _loadCartridge;
    private readonly int _sampleRate;
    private AppSession? _user;
    private AppSession? _next;

    /// <summary>Creates the environment.</summary>
    /// <param name="workspace">The authoring storage.</param>
    /// <param name="tools">Project build tools.</param>
    /// <param name="systemApps">Names of the available system apps.</param>
    /// <param name="loadSystemApp">Loads a system app by name.</param>
    /// <param name="loadCartridge">Loads a cartridge file by host path.</param>
    /// <param name="sampleRate">Audio output rate for new sessions.</param>
    public AppEnvironment(Workspace workspace, IProjectTools tools, IReadOnlyList<string> systemApps,
        Func<string, LoadedProgram> loadSystemApp, Func<string, LoadedProgram> loadCartridge, int sampleRate)
    {
        Workspace = workspace;
        Tools = tools;
        SystemApps = systemApps;
        _loadSystemApp = loadSystemApp;
        _loadCartridge = loadCartridge;
        _sampleRate = sampleRate;
    }

    /// <summary>The authoring storage.</summary>
    public Workspace Workspace { get; }

    /// <summary>Project build tools.</summary>
    public IProjectTools Tools { get; }

    /// <summary>Names of the available system apps.</summary>
    public IReadOnlyList<string> SystemApps { get; }

    /// <summary>The active app, or null once the environment has ended.</summary>
    public AppSession? Active { get; private set; }

    /// <summary>Raised when a system app fails (its message), before it is closed.</summary>
    public event Action<AppSession, string>? AppFailed;

    /// <summary>Starts the shell app.</summary>
    /// <exception cref="InvalidOperationException">The shell app is not available.</exception>
    public void Start()
    {
        if (!SystemApps.Contains(ShellApp, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The '{ShellApp}' system app is missing.");
        }

        Active = StartSystem(ShellApp, null);
    }

    /// <summary>Requests a switch to a system app (started if needed), passing it an open request. False if no such app.</summary>
    public bool Launch(AppSession from, string app, string request)
    {
        if (!SystemApps.Contains(app, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        AppSession target = _system.TryGetValue(app, out AppSession? running) ? running : StartSystem(app, from);
        if (target != from && target.Launcher is null && !target.Name.Equals(ShellApp, StringComparison.OrdinalIgnoreCase))
        {
            target.Launcher = from;
        }

        target.Request = request;
        _next = target;
        return true;
    }

    /// <summary>Requests running a cartridge file; it replaces any running user cartridge. Returns an error or "".</summary>
    public string Run(AppSession from, string cartridgePath)
    {
        LoadedProgram loaded;
        try
        {
            loaded = _loadCartridge(cartridgePath);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException or InvalidOperationException
            or ZTX16.VM.ExecutableFormatException or ZTX16.VM.VerificationException or ZTX16.VM.LinkException
            or ZTX16.Cartridge.CartridgeFormatException or ZTX16.Cartridge.ProjectException)
        {
            return e.Message;
        }

        CloseUser();
        var session = new RuntimeSession(loaded.Program, _sampleRate, loaded.Resources, loaded.Storage);
        var app = new AppSession(Path.GetFileName(cartridgePath), false, session, loaded.Title, from);
        try
        {
            session.Initialize();
        }
        catch (Exception e) when (e is ZTX16.VM.VmTrapException or ZTX16.VM.LinkException)
        {
            return $"the cartridge failed while starting: {e.Message}";
        }

        _user = app;
        _next = app;
        return string.Empty;
    }

    /// <summary>Requests returning from <paramref name="app"/> to the app that launched it (or the shell).</summary>
    public void Back(AppSession app) => _next = app.Launcher ?? Shell ?? app;

    /// <summary>Host shortcut: return to the shell; a running user cartridge is stopped.</summary>
    public void ReturnToShell()
    {
        if (Active is not null && !Active.IsSystem)
        {
            CloseUser();
        }

        _next = Shell;
    }

    /// <summary>
    /// Handles what the tick requested: apps that asked to exit are closed (closing the shell ends the
    /// environment) and a requested switch happens. Returns false when the environment has ended.
    /// </summary>
    public bool AfterTick()
    {
        AppSession? active = Active;
        if (active is not null && active.Session.ShouldExit)
        {
            Close(active);
            if (active.Name.Equals(ShellApp, StringComparison.OrdinalIgnoreCase) && active.IsSystem)
            {
                Active = null;
                return false;
            }

            _next ??= active.Launcher is { } launcher && IsOpen(launcher) ? launcher : Shell;
        }

        if (_next is not null)
        {
            if (Active is not null && Active != _next)
            {
                Active.Session.Input.EnqueueReleaseAll();
            }

            Active = _next;
            _next = null;
        }

        return Active is not null;
    }

    /// <summary>
    /// Handles an unhandled failure of the active app: a user cartridge is closed and its launcher told why;
    /// a failing system app is closed (the shell's failure ends the environment). Returns false when the
    /// environment has ended.
    /// </summary>
    public bool Fail(string message)
    {
        AppSession? failed = Active;
        if (failed is null)
        {
            return false;
        }

        if (failed.IsSystem)
        {
            AppFailed?.Invoke(failed, message);
            _system.Remove(failed.Name);
            if (failed.Name.Equals(ShellApp, StringComparison.OrdinalIgnoreCase))
            {
                Active = null;
                return false;
            }
        }
        else
        {
            _user = null;
        }

        AppSession back = failed.Launcher is { } launcher && IsOpen(launcher) ? launcher : Shell!;
        back.Messages.Enqueue($"{failed.Name} stopped: {message}");
        Active = back;
        _next = null;
        return true;
    }

    /// <summary>Controlled shutdown of every app.</summary>
    public void Shutdown()
    {
        CloseUser();
        foreach (AppSession app in _system.Values.ToList())
        {
            Close(app);
        }

        Active = null;
    }

    private AppSession? Shell => _system.GetValueOrDefault(ShellApp);

    private bool IsOpen(AppSession app) => app == _user || _system.ContainsValue(app);

    private AppSession StartSystem(string name, AppSession? launcher)
    {
        LoadedProgram loaded = _loadSystemApp(name);
        var session = new RuntimeSession(loaded.Program, _sampleRate, loaded.Resources, loaded.Storage);
        var app = new AppSession(name.ToLowerInvariant(), true, session, loaded.Title, launcher);
        session.Authoring = new AuthoringContext(this, app);
        _system[app.Name] = app;
        session.Initialize();
        return app;
    }

    private void CloseUser()
    {
        if (_user is not null)
        {
            Close(_user);
        }
    }

    private void Close(AppSession app)
    {
        app.Session.Deinitialize();
        if (app == _user)
        {
            _user = null;
        }
        else
        {
            _system.Remove(app.Name);
        }

        if (_next == app)
        {
            _next = null;
        }
    }
}

/// <summary>The authoring capabilities of one system app session: the environment and the app itself.</summary>
public sealed class AuthoringContext
{
    internal AuthoringContext(AppEnvironment environment, AppSession app)
    {
        Environment = environment;
        App = app;
    }

    /// <summary>The environment.</summary>
    public AppEnvironment Environment { get; }

    /// <summary>The app these capabilities belong to.</summary>
    public AppSession App { get; }

    /// <summary>Output of the last project build (summary or errors).</summary>
    internal string LastOutput { get; set; } = string.Empty;

    /// <summary>Virtual path of the last built cartridge.</summary>
    internal string LastCartridge { get; set; } = string.Empty;
}
