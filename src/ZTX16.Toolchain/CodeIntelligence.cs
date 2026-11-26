using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ZTX16.Cartridge;

namespace ZTX16.Toolchain;

/// <summary>What a name in the code is, for colouring and completion.</summary>
public enum CodeSymbolKind
{
    /// <summary>Not a symbol.</summary>
    None = 0,

    /// <summary>A class, struct, interface, enum or delegate.</summary>
    Type = 1,

    /// <summary>A method or local function.</summary>
    Method = 2,

    /// <summary>A property.</summary>
    Property = 3,

    /// <summary>A field.</summary>
    Field = 4,

    /// <summary>A local variable.</summary>
    Local = 5,

    /// <summary>A parameter.</summary>
    Parameter = 6,

    /// <summary>A namespace.</summary>
    Namespace = 7,

    /// <summary>An enum member.</summary>
    EnumMember = 8,

    /// <summary>An event.</summary>
    Event = 9,

    /// <summary>A C# keyword (completion only).</summary>
    Keyword = 10,

    /// <summary>A constant.</summary>
    Constant = 11,
}

/// <summary>A compiler diagnostic in the analysed file (0-based lines and columns).</summary>
public sealed record CodeProblem(int Line, int Column, int EndLine, int EndColumn, bool IsError, string Id, string Message);

/// <summary>A name in the analysed file and what it refers to.</summary>
public sealed record CodeColor(int Line, int Column, int Length, CodeSymbolKind Kind);

/// <summary>Problems and name kinds of one file.</summary>
public sealed record CodeAnalysisResult(IReadOnlyList<CodeProblem> Problems, IReadOnlyList<CodeColor> Colors);

/// <summary>One completion proposal.</summary>
public sealed record CodeCompletionItem(string Label, CodeSymbolKind Kind, string Detail);

/// <summary>Completion proposals and the column where the word being completed starts.</summary>
public sealed record CodeCompletionResult(int StartColumn, IReadOnlyList<CodeCompletionItem> Items);

/// <summary>
/// Live code analysis for editors: diagnostics, semantic colours and completion for a file of a project, using
/// the text being edited (not yet saved). The compilation is the same one a build uses (C# 12, the core library,
/// the ZTX16 SDK). Thread-safe; meant to run off the UI thread.
/// </summary>
public static class CodeIntelligence
{
    private static readonly string[] Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "char", "class", "const", "continue", "default", "do",
        "double", "else", "enum", "false", "float", "for", "foreach", "if", "in", "int", "interface", "internal", "is",
        "long", "namespace", "new", "null", "out", "override", "private", "protected", "public", "readonly", "ref",
        "return", "sbyte", "sealed", "short", "static", "string", "struct", "switch", "this", "true", "uint", "ulong",
        "ushort", "using", "var", "virtual", "void", "while",
    ];

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Time, SyntaxTree Tree)> FileTrees = new(StringComparer.OrdinalIgnoreCase);
    private static string? _coreDirectory;
    private static SyntaxTree[] _coreTrees = [];
    private static MetadataReference[]? _references;

    /// <summary>Problems and name kinds of <paramref name="file"/> (project-relative path) with content <paramref name="text"/>.</summary>
    public static CodeAnalysisResult Analyze(string projectDirectory, string file, string text, string? coreDirectory = null)
    {
        if (Compile(projectDirectory, file, text, coreDirectory) is not var (compilation, tree))
        {
            return new CodeAnalysisResult([], []);
        }

        SemanticModel model = compilation.GetSemanticModel(tree);
        var problems = new List<CodeProblem>();
        foreach (Diagnostic d in model.GetDiagnostics())
        {
            if (d.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning) || d.IsSuppressed)
            {
                continue;
            }

            FileLinePositionSpan span = d.Location.GetLineSpan();
            problems.Add(new CodeProblem(span.StartLinePosition.Line, span.StartLinePosition.Character, span.EndLinePosition.Line,
                span.EndLinePosition.Character, d.Severity == DiagnosticSeverity.Error, d.Id, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var colors = new List<CodeColor>();
        SourceText source = tree.GetText();
        foreach (SyntaxToken token in tree.GetRoot().DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken) || token.Text is "var" or "nameof" || token.Parent is null)
            {
                continue;
            }

            ISymbol? symbol = model.GetDeclaredSymbol(token.Parent) is { } declared && declared.Name == token.Text
                ? declared
                : model.GetSymbolInfo(token.Parent).Symbol ?? model.GetSymbolInfo(token.Parent).CandidateSymbols.FirstOrDefault();
            CodeSymbolKind kind = KindOf(symbol);
            if (kind != CodeSymbolKind.None)
            {
                LinePosition at = source.Lines.GetLinePosition(token.SpanStart);
                colors.Add(new CodeColor(at.Line, at.Character, token.Span.Length, kind));
            }
        }

        return new CodeAnalysisResult(problems, colors);
    }

    /// <summary>Completion proposals at (<paramref name="line"/>, <paramref name="column"/>), 0-based.</summary>
    public static CodeCompletionResult Complete(string projectDirectory, string file, string text, int line, int column, string? coreDirectory = null)
    {
        if (Compile(projectDirectory, file, text, coreDirectory) is not var (compilation, tree))
        {
            return new CodeCompletionResult(column, []);
        }

        SourceText source = tree.GetText();
        if (line < 0 || line >= source.Lines.Count)
        {
            return new CodeCompletionResult(column, []);
        }

        int position = Math.Min(source.Lines[line].Start + column, source.Length);
        int start = position;
        while (start > 0 && IsIdentifierChar(source[start - 1]))
        {
            start--;
        }

        int startColumn = column - (position - start);
        SemanticModel model = compilation.GetSemanticModel(tree);
        SyntaxNode root = tree.GetRoot();
        IEnumerable<ISymbol> symbols;
        bool memberAccess = start > 0 && source[start - 1] == '.';
        if (memberAccess)
        {
            symbols = MemberSymbols(model, root.FindToken(start - 1), start);
        }
        else
        {
            if (IsInCommentOrString(root, position))
            {
                return new CodeCompletionResult(startColumn, []);
            }

            symbols = model.LookupSymbols(start).Where(IsUsefulInScope);
        }

        var items = new Dictionary<string, CodeCompletionItem>(StringComparer.Ordinal);
        var overloads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ISymbol symbol in symbols)
        {
            if (!symbol.CanBeReferencedByName || !model.IsAccessible(start, symbol) || IsObjectNoise(symbol)
                || (symbol is IMethodSymbol m && m.MethodKind is not (MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.LocalFunction)))
            {
                continue;
            }

            CodeSymbolKind kind = KindOf(symbol);
            if (kind == CodeSymbolKind.None)
            {
                continue;
            }

            overloads[symbol.Name] = overloads.GetValueOrDefault(symbol.Name) + 1;
            items.TryAdd(symbol.Name, new CodeCompletionItem(symbol.Name, kind, Detail(symbol)));
        }

        if (!memberAccess)
        {
            foreach (string keyword in Keywords)
            {
                items.TryAdd(keyword, new CodeCompletionItem(keyword, CodeSymbolKind.Keyword, "keyword"));
            }
        }

        List<CodeCompletionItem> result = items.Values
            .Select(i => overloads.GetValueOrDefault(i.Label) > 1 ? i with { Detail = $"{i.Detail}  (+{overloads[i.Label] - 1} overloads)" } : i)
            .OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new CodeCompletionResult(startColumn, result);
    }

    private static IEnumerable<ISymbol> MemberSymbols(SemanticModel model, SyntaxToken dot, int position)
    {
        ExpressionSyntax? left = dot.Parent switch
        {
            MemberAccessExpressionSyntax access => access.Expression,
            QualifiedNameSyntax qualified => qualified.Left,
            _ => null,
        };
        if (left is null)
        {
            return [];
        }

        ISymbol? target = model.GetSymbolInfo(left).Symbol;
        if (target is INamespaceSymbol ns)
        {
            return model.LookupNamespacesAndTypes(position, ns);
        }

        if (target is INamedTypeSymbol type)
        {
            return model.LookupStaticMembers(position, type);
        }

        ITypeSymbol? instance = model.GetTypeInfo(left).Type;
        return instance is null || instance.TypeKind == TypeKind.Error
            ? []
            : model.LookupSymbols(position, instance, includeReducedExtensionMethods: true).Where(s => !s.IsStatic || s is IMethodSymbol { IsExtensionMethod: true });
    }

    // In scope (not after a dot): skip the many BCL namespaces and types the VM cannot use anyway.
    private static bool IsUsefulInScope(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol ns => ns.Name == "ZTX16",
        INamedTypeSymbol type => type.ContainingNamespace is { IsGlobalNamespace: true } || type.ContainingNamespace.ToDisplayString().StartsWith("ZTX16", StringComparison.Ordinal),
        _ => true,
    };

    private static bool IsObjectNoise(ISymbol symbol) =>
        symbol.ContainingType?.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum;

    private static bool IsInCommentOrString(SyntaxNode root, int position)
    {
        SyntaxTrivia trivia = root.FindTrivia(position > 0 ? position - 1 : 0);
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
        {
            return true;
        }

        SyntaxToken token = root.FindToken(position > 0 ? position - 1 : 0);
        return token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.CharacterLiteralToken)
            || token.IsKind(SyntaxKind.InterpolatedStringTextToken);
    }

    private static CodeSymbolKind KindOf(ISymbol? symbol) => symbol switch
    {
        INamedTypeSymbol => CodeSymbolKind.Type,
        IMethodSymbol => CodeSymbolKind.Method,
        IPropertySymbol => CodeSymbolKind.Property,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => CodeSymbolKind.EnumMember,
        IFieldSymbol { IsConst: true } => CodeSymbolKind.Constant,
        IFieldSymbol => CodeSymbolKind.Field,
        ILocalSymbol { IsConst: true } => CodeSymbolKind.Constant,
        ILocalSymbol => CodeSymbolKind.Local,
        IParameterSymbol => CodeSymbolKind.Parameter,
        INamespaceSymbol => CodeSymbolKind.Namespace,
        IEventSymbol => CodeSymbolKind.Event,
        _ => CodeSymbolKind.None,
    };

    private static string Detail(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IPropertySymbol p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}",
        IFieldSymbol f => $"{f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {f.Name}",
        ILocalSymbol l => $"{l.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {l.Name}",
        IParameterSymbol p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}",
        INamedTypeSymbol t => $"{t.TypeKind.ToString().ToLowerInvariant()} {t.ToDisplayString()}",
        INamespaceSymbol n => $"namespace {n.ToDisplayString()}",
        _ => symbol.ToDisplayString(),
    };

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // The project's compilation with 'file' replaced by 'text'. Other files and the core library are cached.
    private static (CSharpCompilation Compilation, SyntaxTree Tree)? Compile(string projectDirectory, string file, string text, string? coreDirectory)
    {
        Project project;
        try
        {
            project = Project.Open(projectDirectory);
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string relative = file.Replace('\\', '/');
        CSharpParseOptions parse = ProjectBuilder.ParseOptions;
        var trees = new List<SyntaxTree>();
        SyntaxTree? edited = null;
        lock (Gate)
        {
            _references ??= ProjectBuilder.References();
            string core = coreDirectory ?? ProjectBuilder.DefaultCoreDirectory;
            if (_coreDirectory != core)
            {
                _coreTrees = Directory.Exists(core)
                    ? ProjectBuilder.CoreFiles(core).Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), parse, path: $"<core>/{Path.GetFileName(p)}", encoding: Encoding.UTF8)).ToArray()
                    : [];
                _coreDirectory = core;
            }

            foreach (ProjectSource source in project.Sources)
            {
                if (string.Equals(source.RelativePath.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime time = File.GetLastWriteTimeUtc(source.FullPath);
                if (!FileTrees.TryGetValue(source.FullPath, out var cached) || cached.Time != time)
                {
                    cached = (time, CSharpSyntaxTree.ParseText(File.ReadAllText(source.FullPath), parse, path: source.RelativePath, encoding: Encoding.UTF8));
                    FileTrees[source.FullPath] = cached;
                }

                trees.Add(cached.Tree);
            }

            trees.AddRange(_coreTrees);
        }

        edited = CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8), parse, path: relative);
        trees.Add(edited);
        return (CSharpCompilation.Create("Cartridge", trees, _references, ProjectBuilder.CompilationOptions), edited);
    }
}
