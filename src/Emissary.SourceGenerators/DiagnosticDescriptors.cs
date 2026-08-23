using Microsoft.CodeAnalysis;

namespace Emissary.SourceGenerators;

internal static class DiagnosticDescriptors
{
    private const string Category = "Emissary";

    public static readonly DiagnosticDescriptor MissingDescription = new(
        id: "EMS001",
        title: "Tool has no description",
        messageFormat: "Tool method '{0}' has no description; Claude picks tools by their descriptions. Set [ClaudeTool(Description = ...)].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingParameterDescription = new(
        id: "EMS003",
        title: "Tool parameter has no description",
        messageFormat: "Parameter '{0}' of tool method '{1}' has no description; Claude chooses argument values from parameter descriptions. Add a <param> doc comment.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SafetyAttributeWithoutTool = new(
        id: "EMS010",
        title: "Safety attribute has no effect without [ClaudeTool]",
        messageFormat: "'{0}' has [AuthorizeTool] but not [ClaudeTool], so the authorization policy is silently ignored. Add [ClaudeTool] or remove [AuthorizeTool].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedParameterType = new(
        id: "EMS002",
        title: "Unsupported tool parameter type",
        messageFormat: "Parameter or member '{0}' of '{1}' has unsupported type '{2}'. Supported: string, bool, int, long, double, enums, arrays of those, records/objects composed of those, and CancellationToken.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        id: "EMS004",
        title: "Containing type must be partial",
        messageFormat: "Type '{0}' must be declared partial so the generator can extend it (required by '{1}')",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CompensatorStaticnessMismatch = new(
        id: "EMS012",
        title: "Compensator must match the tool's static-ness",
        messageFormat: "CompensatedBy on tool method '{0}' references '{1}', which is {2}; a tool and its compensator must both be static or both be instance methods",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        id: "EMS006",
        title: "Unsupported tool return type",
        messageFormat: "Tool method '{0}' has unsupported return type '{1}'. Supported: string, bool, int, long, double, enums, and Task/ValueTask of those.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericsNotSupported = new(
        id: "EMS007",
        title: "Generic tool methods and schema types are not supported",
        messageFormat: "'{0}' is generic or is declared in a generic type; tools and schema types must be non-generic",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CompensatorNotFound = new(
        id: "EMS009",
        title: "Compensator method not found",
        messageFormat: "CompensatedBy on tool method '{0}' references '{1}', which is not a [ClaudeTool] method on the same type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidMaxResultLength = new(
        id: "EMS011",
        title: "MaxResultLength must be positive",
        messageFormat: "MaxResultLength on tool method '{0}' is {1}; use a positive character count, or 0 for no cap",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TypeNotSchemaRepresentable = new(
        id: "EMS008",
        title: "Type is not schema-representable",
        messageFormat: "Type '{0}' marked [ClaudeSchema] is not schema-representable. It needs a single public parameterized constructor or public writable properties, without cycles, generics, or abstract types.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
