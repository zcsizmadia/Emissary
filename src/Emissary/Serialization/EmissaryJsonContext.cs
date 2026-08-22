using System.Text.Json.Serialization;

namespace Emissary.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Trajectory))]
[JsonSerializable(typeof(SuspendedRun))]
[JsonSerializable(typeof(PersistedConversation))]
internal sealed partial class EmissaryJsonContext : JsonSerializerContext;
