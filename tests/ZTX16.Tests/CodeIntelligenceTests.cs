using ZTX16.Toolchain;

namespace ZTX16.Tests;

public class CodeIntelligenceTests
{
    private const string Source = """
        using ZTX16;

        public sealed class Game : ICartridge
        {
            private int score;

            public void Initialize()
            {
                int lives = 3;
                Graphics.
            }

            public void Tick() { }

            public void Render() { }
        }
        """;

    private static string Project(TempDirectory dir, string code)
    {
        ProjectTools.Create(dir.Path, "Test", "dev.test.intel");
        File.WriteAllText(Path.Combine(dir.Path, "code", "Game.cs"), "// saved version differs");
        return code;
    }

    private static (int Line, int Column) Find(string text, string marker, int offset)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int at = lines[i].IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                return (i, at + offset);
            }
        }

        throw new InvalidOperationException(marker);
    }

    [Fact]
    public void CompletesMembersAfterADot()
    {
        using var dir = new TempDirectory();
        string text = Project(dir, Source).Replace("\r", "");
        (int line, int column) = Find(text, "Graphics.", "Graphics.".Length);
        CodeCompletionResult result = CodeIntelligence.Complete(dir.Path, "code/Game.cs", text, line, column, ProjectFixtures.CoreDirectory);
        Assert.Equal(column, result.StartColumn);
        CodeCompletionItem fill = Assert.Single(result.Items, i => i.Label == "FillRect");
        Assert.Equal(CodeSymbolKind.Method, fill.Kind);
        Assert.Contains("int layer", fill.Detail);
        Assert.Contains(result.Items, i => i.Label == "Clear");
        Assert.DoesNotContain(result.Items, i => i.Label is "ToString" or "GetHashCode");    // object noise hidden
    }

    [Fact]
    public void CompletesNamesInScopeWithAPrefix()
    {
        using var dir = new TempDirectory();
        string text = Project(dir, Source).Replace("\r", "").Replace("Graphics.", "li");
        (int line, int column) = Find(text, "        li", "        li".Length);
        CodeCompletionResult result = CodeIntelligence.Complete(dir.Path, "code/Game.cs", text, line, column, ProjectFixtures.CoreDirectory);
        Assert.Equal(column - 2, result.StartColumn);
        Assert.Contains(result.Items, i => i.Label == "lives" && i.Kind == CodeSymbolKind.Local);
        Assert.Contains(result.Items, i => i.Label == "score" && i.Kind == CodeSymbolKind.Field);
        Assert.Contains(result.Items, i => i.Label == "Graphics" && i.Kind == CodeSymbolKind.Type);
        Assert.Contains(result.Items, i => i.Label == "while" && i.Kind == CodeSymbolKind.Keyword);
        Assert.DoesNotContain(result.Items, i => i.Label == "Console");                    // BCL types hidden
    }

    [Fact]
    public void ReportsProblemsAndColoursNamesOfTheUnsavedText()
    {
        using var dir = new TempDirectory();
        string text = Project(dir, Source).Replace("\r", "").Replace("Graphics.", "Graphics.Clear(lives);\n        undefinedThing = 1;");
        CodeAnalysisResult result = CodeIntelligence.Analyze(dir.Path, "code/Game.cs", text, ProjectFixtures.CoreDirectory);
        CodeProblem problem = Assert.Single(result.Problems, p => p.IsError);
        Assert.Equal("CS0103", problem.Id);                                               // name does not exist
        Assert.Equal(Find(text, "undefinedThing", 0), (problem.Line, problem.Column));

        (int line, int column) = Find(text, "Graphics.Clear", 0);
        Assert.Contains(result.Colors, c => c.Line == line && c.Column == column && c.Kind == CodeSymbolKind.Type);
        Assert.Contains(result.Colors, c => c.Line == line && c.Column == column + 9 && c.Kind == CodeSymbolKind.Method);
        Assert.Contains(result.Colors, c => c.Line == line && c.Column == column + 15 && c.Kind == CodeSymbolKind.Local);
    }

    [Fact]
    public void NoCompletionInsideStringsOrComments()
    {
        using var dir = new TempDirectory();
        string text = Project(dir, Source).Replace("\r", "").Replace("Graphics.", "// Gra");
        (int line, int column) = Find(text, "// Gra", 6);
        Assert.Empty(CodeIntelligence.Complete(dir.Path, "code/Game.cs", text, line, column, ProjectFixtures.CoreDirectory).Items);
    }
}
