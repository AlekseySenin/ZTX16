using ZTX16.Platform.Authoring;
using ZTX16.Toolchain;

namespace ZTX16.Host;

/// <summary>Live code analysis for the code editor, by the toolchain's Roslyn-based <see cref="CodeIntelligence"/>.</summary>
internal sealed class HostCodeAnalysis : ICodeAnalysis
{
    public CodeAnalysisInfo Analyze(string projectDirectory, string file, string text)
    {
        CodeAnalysisResult result = CodeIntelligence.Analyze(projectDirectory, file, text);
        return new CodeAnalysisInfo(
            result.Problems.Select(p => new CodeProblemInfo(p.Line, p.Column, p.EndLine, p.EndColumn, p.IsError, p.Id, p.Message)).ToList(),
            result.Colors.Select(c => new CodeColorInfo(c.Line, c.Column, c.Length, (int)c.Kind)).ToList());
    }

    public string ApiReference() => ZTX16.Toolchain.ApiReference.Generate();

    public CodeCompletionInfo Complete(string projectDirectory, string file, string text, int line, int column)
    {
        CodeCompletionResult result = CodeIntelligence.Complete(projectDirectory, file, text, line, column);
        return new CodeCompletionInfo(result.StartColumn, result.Items.Select(i => new CodeCompletionEntry(i.Label, (int)i.Kind, i.Detail)).ToList());
    }
}
