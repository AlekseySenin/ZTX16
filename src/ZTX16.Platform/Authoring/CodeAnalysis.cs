namespace ZTX16.Platform.Authoring;

/// <summary>A compiler diagnostic in an analysed file (0-based lines and columns).</summary>
public sealed record CodeProblemInfo(int Line, int Column, int EndLine, int EndColumn, bool IsError, string Id, string Message);

/// <summary>A name in an analysed file and its kind (see the editor's colour table).</summary>
public sealed record CodeColorInfo(int Line, int Column, int Length, int Kind);

/// <summary>Problems and name kinds of one file.</summary>
public sealed record CodeAnalysisInfo(IReadOnlyList<CodeProblemInfo> Problems, IReadOnlyList<CodeColorInfo> Colors);

/// <summary>One completion proposal.</summary>
public sealed record CodeCompletionEntry(string Label, int Kind, string Detail);

/// <summary>Completion proposals and where the completed word starts.</summary>
public sealed record CodeCompletionInfo(int StartColumn, IReadOnlyList<CodeCompletionEntry> Items);

/// <summary>
/// Live code analysis for the code editor (implemented by the host with the toolchain). Called on a worker
/// thread; the editor polls for the result, so it never waits.
/// </summary>
public interface ICodeAnalysis
{
    /// <summary>Problems and name kinds of <paramref name="file"/> (relative to the project) with the text being edited.</summary>
    CodeAnalysisInfo Analyze(string projectDirectory, string file, string text);

    /// <summary>Completion proposals at a 0-based position.</summary>
    CodeCompletionInfo Complete(string projectDirectory, string file, string text, int line, int column);
}
