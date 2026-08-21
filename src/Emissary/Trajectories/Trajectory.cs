using System.Text.Json;
using Emissary.Serialization;

namespace Emissary;

/// <summary>
/// A recorded agent run: every model request and response, replayable deterministically and
/// serializable as a <c>.trajectory</c> JSON file. Record with <see cref="TrajectoryRecorder"/>,
/// replay by constructing a <see cref="ClaudeAgent"/> with the trajectory.
/// </summary>
/// <param name="Version">The format version; currently <see cref="CurrentVersion"/>.</param>
/// <param name="Turns">One entry per model call, in order.</param>
public sealed record Trajectory(int Version, IReadOnlyList<TrajectoryTurn> Turns)
{
    /// <summary>The current <c>.trajectory</c> format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Serializes the trajectory as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, EmissaryJsonContext.Default.Trajectory);

    /// <summary>Deserializes a trajectory from JSON.</summary>
    /// <param name="json">The trajectory JSON.</param>
    /// <exception cref="InvalidOperationException">The JSON is the null literal.</exception>
    public static Trajectory FromJson(string json) =>
        JsonSerializer.Deserialize(json, EmissaryJsonContext.Default.Trajectory)
            ?? throw new InvalidOperationException("The trajectory JSON deserialized to null.");

    /// <summary>Writes the trajectory to a file.</summary>
    /// <param name="path">The file path, conventionally ending in <c>.trajectory</c>.</param>
    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>Reads a trajectory from a file.</summary>
    /// <param name="path">The file path.</param>
    public static Trajectory Load(string path) => FromJson(File.ReadAllText(path));
}

/// <summary>One recorded model call.</summary>
/// <param name="Request">What the agent sent.</param>
/// <param name="Response">What the model returned.</param>
public sealed record TrajectoryTurn(TrajectoryRequest Request, TrajectoryResponse Response);

/// <summary>The recorded shape of one model request.</summary>
/// <param name="Model">The model id.</param>
/// <param name="System">The system prompt, if any.</param>
/// <param name="MaxTokens">The output token limit.</param>
/// <param name="Thinking">The thinking mode.</param>
/// <param name="Effort">The effort level, if set.</param>
/// <param name="OutputSchemaJson">The structured-output schema, if set.</param>
/// <param name="Messages">The conversation sent.</param>
/// <param name="ToolNames">The wire names of the tools offered.</param>
public sealed record TrajectoryRequest(
    string Model,
    string? System,
    int MaxTokens,
    ThinkingMode Thinking,
    EffortLevel? Effort,
    string? OutputSchemaJson,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<string> ToolNames);

/// <summary>The recorded shape of one model response.</summary>
/// <param name="Content">The assistant content blocks.</param>
/// <param name="StopReason">The wire stop reason.</param>
/// <param name="InputTokens">Input tokens for the call.</param>
/// <param name="OutputTokens">Output tokens for the call.</param>
public sealed record TrajectoryResponse(
    IReadOnlyList<ContentBlock> Content,
    string StopReason,
    long InputTokens,
    long OutputTokens);
