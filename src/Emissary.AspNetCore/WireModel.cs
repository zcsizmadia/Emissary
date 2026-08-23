using System.Text.Json.Serialization;

namespace Emissary.AspNetCore;

internal sealed record AgentMessageRequest(string? Message);

internal sealed record ApprovalRequest(Guid ConversationId, bool Approve);

internal sealed record DeltaDto(string Delta);

internal sealed record ToolCallDto(string Id, string Name);

internal sealed record ToolResultDto(string Id, string Name, string Result, bool IsError);

internal sealed record SuspendedDto(Guid ConversationId, IReadOnlyList<string> PendingTools);

internal sealed record ToolFailedDto(string Id, string Name, string ExceptionType, bool TimedOut);

internal sealed record HandoffDto(string TargetName, string? Reason);

internal sealed record ErrorDto(string ExceptionType);

internal sealed record CompletedDto(
    Guid ConversationId,
    string StopReason,
    string FinalText,
    long InputTokens,
    long OutputTokens,
    bool Tainted);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentMessageRequest))]
[JsonSerializable(typeof(ApprovalRequest))]
[JsonSerializable(typeof(DeltaDto))]
[JsonSerializable(typeof(ToolCallDto))]
[JsonSerializable(typeof(ToolResultDto))]
[JsonSerializable(typeof(SuspendedDto))]
[JsonSerializable(typeof(ToolFailedDto))]
[JsonSerializable(typeof(HandoffDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(CompletedDto))]
internal sealed partial class EmissaryWireContext : JsonSerializerContext;
