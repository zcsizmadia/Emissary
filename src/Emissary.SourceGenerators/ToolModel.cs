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
    Object,
}

internal sealed class ParameterModel
{
    public string CSharpName = "";
    public string JsonName = "";
    public string DeclaredTypeFullName = "";
    public JsonKind Kind;
    public JsonKind ElementKind;
    public INamedTypeSymbol? EnumType;
    public int PocoIndex;
    public string? Description;
    public bool IsCancellationToken;
    public bool IsOptional;
    public string DefaultLiteral = "";
}

/// <summary>A bindable object type (record or POCO) used by a tool parameter, possibly nested.</summary>
internal sealed class PocoModel
{
    public string FullName = "";
    public string BinderName = "";
    public bool UsesConstructor;
    public List<ParameterModel> Members { get; } = new();
}

internal sealed class ToolModel
{
    public List<Diagnostic> Diagnostics { get; } = new();
    public List<ParameterModel> Parameters { get; } = new();
    public List<PocoModel> Pocos { get; } = new();
    public List<string> Containers { get; } = new();
    public string? Namespace;
    public string MethodName = "";
    public string ToolName = "";
    public string Description = "";
    public string HintName = "";
    public string? RequiredPolicy;
    public string? CompensatedBy;
    public int MaxResultLength;
    public bool Untrusted;
    public bool Privileged;
    public JsonKind ReturnKind;
    public bool IsAsync;

    /// <summary>
    /// The tool method is an instance method, so the generated <c>{Method}Tool</c> is an instance
    /// property whose handler is bound to <c>this</c> — the seam for tools with injected
    /// dependencies.
    /// </summary>
    public bool IsInstance;

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
