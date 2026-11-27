using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZTX16.Toolchain;

/// <summary>
/// The API reference shown by the Help app: every public type and member a cartridge can use — the ZTX16 SDK
/// (platform syscalls) and the core library compiled into every cartridge — with signatures and the summaries
/// of their XML documentation. Generated from the same sources the compiler uses, so it never drifts.
/// </summary>
/// <remarks>
/// Text format (one item per line, tab-separated): <c>T</c>, full type name, kind, summary for a type, then
/// <c>M</c>, signature, summary for each of its members.
/// </remarks>
public static partial class ApiReference
{
    private static readonly object Gate = new();
    private static string? _cached;
    private static string? _cachedCore;

    /// <summary>The reference text (cached).</summary>
    public static string Generate(string? coreDirectory = null)
    {
        string core = coreDirectory ?? ProjectBuilder.DefaultCoreDirectory;
        lock (Gate)
        {
            if (_cached is not null && _cachedCore == core)
            {
                return _cached;
            }
        }

        CSharpParseOptions parse = ProjectBuilder.ParseOptions;
        IEnumerable<SyntaxTree> trees = Directory.Exists(core)
            ? ProjectBuilder.CoreFiles(core).Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), parse, path: p, encoding: Encoding.UTF8))
            : [];
        var references = new List<MetadataReference>();
        foreach (MetadataReference reference in ProjectBuilder.References())
        {
            if (reference is PortableExecutableReference pe && pe.FilePath is { } path
                && Path.GetFileNameWithoutExtension(path) == "ZTX16.Sdk" && File.Exists(Path.ChangeExtension(path, ".xml")))
            {
                references.Add(MetadataReference.CreateFromFile(path, documentation: new XmlDocs(Path.ChangeExtension(path, ".xml"))));
            }
            else
            {
                references.Add(reference);
            }
        }

        CSharpCompilation compilation = CSharpCompilation.Create("Reference", trees, references, ProjectBuilder.CompilationOptions);
        var types = new List<INamedTypeSymbol>();
        Collect(compilation.GlobalNamespace, types);
        var text = new StringBuilder();
        foreach (INamedTypeSymbol type in types
            .OrderBy(t => t.ContainingNamespace.ToDisplayString() == "ZTX16" ? 0 : 1)
            .ThenBy(t => t.ContainingNamespace.ToDisplayString(), StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            text.Append("T\t").Append(type.ToDisplayString()).Append('\t').Append(Kind(type)).Append('\t').Append(Summary(type)).Append('\n');
            foreach (ISymbol member in type.GetMembers().Where(IsDocumented).OrderBy(Order).ThenBy(m => m.Name, StringComparer.Ordinal))
            {
                text.Append("M\t").Append(Signature(member)).Append('\t').Append(Summary(member)).Append('\n');
            }
        }

        string result = text.ToString();
        lock (Gate)
        {
            _cached = result;
            _cachedCore = core;
        }

        return result;
    }

    private static void Collect(INamespaceSymbol ns, List<INamedTypeSymbol> types)
    {
        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
        {
            Collect(child, types);
        }

        string name = ns.ToDisplayString();
        if (name != "ZTX16" && !name.StartsWith("ZTX16.", StringComparison.Ordinal))
        {
            return;
        }

        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
        {
            if (type.DeclaredAccessibility == Accessibility.Public && !IsHidden(type) && !type.Name.EndsWith("Attribute", StringComparison.Ordinal)
                && type.Name != "NotInVmException")
            {
                types.Add(type);
            }
        }
    }

    private static bool IsHidden(ISymbol symbol) => symbol.GetAttributes().Any(a =>
        a.AttributeClass?.Name == nameof(EditorBrowsableAttribute)
        && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int state && state == (int)EditorBrowsableState.Never);

    private static bool IsDocumented(ISymbol member) =>
        member.DeclaredAccessibility == Accessibility.Public && !member.IsImplicitlyDeclared && !IsHidden(member)
        && member switch
        {
            IMethodSymbol m => m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor,
            IPropertySymbol or IFieldSymbol or IEventSymbol => true,
            _ => false,
        };

    private static int Order(ISymbol member) => member switch
    {
        IFieldSymbol => 0,
        IMethodSymbol { MethodKind: MethodKind.Constructor } => 1,
        IPropertySymbol => 2,
        IEventSymbol => 3,
        _ => 4,
    };

    private static string Kind(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Enum => "enum",
        TypeKind.Interface => "interface",
        TypeKind.Struct => "struct",
        _ => type.IsStatic ? "static class" : "class",
    };

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeConstantValue,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static string Signature(ISymbol member)
    {
        string signature = member.ToDisplayString(SignatureFormat);
        return member.IsStatic && member is not IFieldSymbol { IsConst: true } && member.ContainingType.TypeKind != TypeKind.Enum && !member.ContainingType.IsStatic
            ? "static " + signature
            : signature;
    }

    // The <summary> of a symbol's documentation as plain text.
    private static string Summary(ISymbol symbol)
    {
        string xml = symbol.GetDocumentationCommentXml(expandIncludes: false) ?? string.Empty;
        if (xml.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            XElement? summary = XDocument.Parse(xml).Root?.Element("summary");
            if (summary is null)
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            foreach (XNode node in summary.Nodes())
            {
                text.Append(node switch
                {
                    XText t => t.Value,
                    XElement { Name.LocalName: "see" or "seealso" } e => ShortName((string?)e.Attribute("cref") ?? (string?)e.Attribute("langword") ?? e.Value),
                    XElement { Name.LocalName: "paramref" or "typeparamref" } e => (string?)e.Attribute("name") ?? string.Empty,
                    XElement e => e.Value,
                    _ => string.Empty,
                });
            }

            return Ascii(Spaces().Replace(text.ToString(), " ").Trim());
        }
        catch (System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    // The ZTX16 fonts cover ASCII: typographic characters of the documentation become ASCII equivalents.
    private static string Ascii(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            result.Append(c switch
            {
                '—' or '–' or '−' => "-",
                '×' => "x",
                '…' => "...",
                '‘' or '’' => "'",
                '“' or '”' => "\"",
                '≥' => ">=",
                '≤' => "<=",
                '→' => "->",
                '²' => "^2",
                '°' => " degrees",
                '⁻' => "-",
                '⁴' => "4",
                '¹' => "1",
                _ when c < 128 => c.ToString(),
                _ => "?",
            });
        }

        return result.ToString();
    }

    // "M:ZTX16.Graphics.Clear(System.Int32)" → "Graphics.Clear".
    private static string ShortName(string cref)
    {
        string name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        int paren = name.IndexOf('(');
        if (paren >= 0)
        {
            name = name[..paren];
        }

        string[] parts = name.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : name;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>Documentation of a compiled assembly from its XML documentation file.</summary>
internal sealed class XmlDocs : DocumentationProvider
{
    private readonly string _path;
    private readonly Dictionary<string, string> _members = new(StringComparer.Ordinal);

    public XmlDocs(string path)
    {
        _path = path;
        foreach (XElement member in XDocument.Load(path).Descendants("member"))
        {
            if ((string?)member.Attribute("name") is { } name)
            {
                _members[name] = member.ToString(SaveOptions.DisableFormatting);
            }
        }
    }

    protected override string? GetDocumentationForSymbol(string documentationMemberID, System.Globalization.CultureInfo preferredCulture, CancellationToken cancellationToken = default) =>
        _members.GetValueOrDefault(documentationMemberID);

    public override bool Equals(object? obj) => obj is XmlDocs other && other._path == _path;

    public override int GetHashCode() => _path.GetHashCode(StringComparison.Ordinal);
}
