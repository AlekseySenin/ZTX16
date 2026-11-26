namespace ZTX16.Authoring;

// Authoring capabilities. Only the system apps of the ZTX16 environment (the Console and the editors) can use
// these syscalls: a normal cartridge that calls them fails to load, because they are not in its syscall table.

/// <summary>
/// The user's authoring storage as a virtual file tree. Paths are absolute: <c>/MyGame/code/Game.cs</c>
/// (no <c>.</c> or <c>..</c>). Problems are returned (an error message, null or false), never thrown.
/// </summary>
public static class Workspace
{
    /// <summary>Entries of a directory, one per line; directories end with '/'. Null if not a directory.</summary>
    [Syscall("workspace.list")]
    public static string? List(string path) => throw new NotInVmException();

    /// <summary>True if a file or directory exists.</summary>
    [Syscall("workspace.exists")]
    public static bool Exists(string path) => throw new NotInVmException();

    /// <summary>True if the path is a directory.</summary>
    [Syscall("workspace.is_directory")]
    public static bool IsDirectory(string path) => throw new NotInVmException();

    /// <summary>Size of a file in bytes, or −1.</summary>
    [Syscall("workspace.size")]
    public static long Size(string path) => throw new NotInVmException();

    /// <summary>Creates a directory and missing parents. Returns an error message, or "" on success.</summary>
    [Syscall("workspace.create_directory")]
    public static string CreateDirectory(string path) => throw new NotInVmException();

    /// <summary>Deletes a file or a directory (non-empty only if recursive). Returns an error message or "".</summary>
    [Syscall("workspace.delete")]
    public static string Delete(string path, bool recursive) => throw new NotInVmException();

    /// <summary>Moves or renames. Returns an error message or "".</summary>
    [Syscall("workspace.move")]
    public static string Move(string from, string to) => throw new NotInVmException();

    /// <summary>Copies a file or directory tree. Returns an error message or "".</summary>
    [Syscall("workspace.copy")]
    public static string Copy(string from, string to) => throw new NotInVmException();

    /// <summary>A text file (UTF-8), or null.</summary>
    [Syscall("workspace.read_text")]
    public static string? ReadText(string path) => throw new NotInVmException();

    /// <summary>Writes a text file (UTF-8). Returns an error message or "".</summary>
    [Syscall("workspace.write_text")]
    public static string WriteText(string path, string text) => throw new NotInVmException();

    /// <summary>A file's bytes, or null.</summary>
    [Syscall("workspace.read_bytes")]
    public static byte[]? ReadBytes(string path) => throw new NotInVmException();

    /// <summary>Writes a file. Returns an error message or "".</summary>
    [Syscall("workspace.write_bytes")]
    public static string WriteBytes(string path, byte[] data) => throw new NotInVmException();
}

/// <summary>Project operations: create, build, extract and run.</summary>
public static class Projects
{
    /// <summary>True if the directory holds a project (a <c>ztx16.project.json</c>).</summary>
    [Syscall("projects.is_project")]
    public static bool IsProject(string path) => throw new NotInVmException();

    /// <summary>Creates a project in a new or empty directory. Returns an error message or "".</summary>
    [Syscall("projects.create")]
    public static string Create(string path, string name) => throw new NotInVmException();

    /// <summary>Builds a project into <c>bin/&lt;name&gt;.ztx</c>. See <see cref="Output"/> and <see cref="LastCartridge"/>.</summary>
    [Syscall("projects.build")]
    public static bool Build(string path) => throw new NotInVmException();

    /// <summary>The last build's summary, or its errors.</summary>
    [Syscall("projects.output")]
    public static string Output() => throw new NotInVmException();

    /// <summary>Virtual path of the cartridge the last successful build wrote.</summary>
    [Syscall("projects.last_cartridge")]
    public static string LastCartridge() => throw new NotInVmException();

    /// <summary>Extracts a cartridge into a project directory. Returns an error message or "".</summary>
    [Syscall("projects.extract")]
    public static string Extract(string cartridge, string path) => throw new NotInVmException();

    /// <summary>
    /// Runs a cartridge file, or builds and runs a project directory. The cartridge becomes the active app after
    /// this tick; when it ends, this app is active again. Returns an error message or "".
    /// </summary>
    [Syscall("projects.run")]
    public static string Run(string path) => throw new NotInVmException();
}

/// <summary>The environment's apps: switching between the Console and the editors.</summary>
public static class Apps
{
    /// <summary>Number of system apps.</summary>
    [Syscall("apps.count")]
    public static int Count() => throw new NotInVmException();

    /// <summary>Name of a system app.</summary>
    [Syscall("apps.name")]
    public static string Name(int index) => throw new NotInVmException();

    /// <summary>
    /// Switches to a system app after this tick (starting it if needed) and hands it <paramref name="request"/>
    /// (for example a file to open). False if there is no such app.
    /// </summary>
    [Syscall("apps.launch")]
    public static bool Launch(string app, string request) => throw new NotInVmException();

    /// <summary>The pending open request for this app ("" if none); reading it clears it.</summary>
    [Syscall("apps.request")]
    public static string Request() => throw new NotInVmException();

    /// <summary>The next message from the environment for this app ("" if none), e.g. why a cartridge stopped.</summary>
    [Syscall("apps.message")]
    public static string Message() => throw new NotInVmException();

    /// <summary>Returns to the app that launched this one (the Console if none) after this tick.</summary>
    [Syscall("apps.back")]
    public static void Back() => throw new NotInVmException();

    /// <summary>The environment's version text.</summary>
    [Syscall("apps.version")]
    public static string Version() => throw new NotInVmException();
}
