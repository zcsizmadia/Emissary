using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Emissary.SourceGenerators;

/// <summary>The JSON-representable kinds a tool parameter or return value can have.</summary>
internal enum JsonKind
{
    String,
    Bool,
    Int,
    Long,
    Double,
    Enum,
    Array,
}

internal sealed class ParameterModel
{
    public string CSharpName = "";
    public string JsonName = "";
    public string DeclaredTypeFullName = "";
    public JsonKind Kind;
    public JsonKind ElementKind;
    public INamedTypeSymbol? EnumType;
    public bool IsCancellationToken;
    public bool IsOptional;
    public string DefaultLiteral = "";
}

internal sealed class ToolModel
{
    public List<Diagnostic> Diagnostics { get; } = new();
    public List<ParameterModel> Parameters { get; } = new();
    public List<string> Containers { get; } = new();
    public string? Namespace;
    public string MethodName = "";
    public string ToolName = "";
    public string Description = "";
    public string HintName = "";
    public JsonKind ReturnKind;
    public bool IsAsync;

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
