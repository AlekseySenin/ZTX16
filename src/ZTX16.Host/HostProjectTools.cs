using ZTX16.Cartridge;
using ZTX16.Platform.Authoring;
using ZTX16.Toolchain;

namespace ZTX16.Host;

/// <summary>The toolchain behind the environment's project operations (build runs Roslyn inside the host).</summary>
internal sealed class HostProjectTools : IProjectTools
{
    public string Create(string directory, string name) => ProjectTools.Create(directory, name);

    public (string Path, string Message) Build(string directory) => ProjectTools.Build(directory);

    public string Extract(string cartridgePath, string directory) => ProjectTools.Extract(cartridgePath, directory);

    public bool IsProject(string directory) => File.Exists(Path.Combine(directory, ProjectConfig.FileName));
}
