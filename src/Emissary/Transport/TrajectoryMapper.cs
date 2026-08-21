using System.Collections.Immutable;

namespace Emissary.Transport;

/// <summary>Pure mapping between transport types and trajectory records.</summary>
internal static class TrajectoryMapper
{
    public static TrajectoryRequest ToTrajectoryRequest(ModelRequest request) => new(
        request.Model,
        request.System,
        request.MaxTokens,
        request.Thinking,
        request.Effort,
        request.OutputSchemaJson,
        [.. request.Messages],
        [.. request.Tools.Select(t => t.Name)]);

    public static TrajectoryResponse ToTrajectoryResponse(ModelResponse response) => new(
        [.. response.Content],
        response.StopReason,
        response.InputTokens,
        response.OutputTokens);

    public static ModelResponse ToModelResponse(TrajectoryResponse response) => new(
        [.. response.Content],
        response.StopReason,
        response.InputTokens,
        response.OutputTokens);

    /// <summary>Synthesizes the delta events a live stream would have produced for a response.</summary>
    public static IEnumerable<StreamEvent> SynthesizeEvents(ModelResponse response)
    {
        foreach (ContentBlock block in response.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    yield return new StreamTextDelta(text.Text);
                    break;
                case ThinkingBlock thinking:
                    yield return new StreamThinkingDelta(thinking.Thinking);
                    break;
                case ToolUseBlock toolUse:
                    yield return new StreamToolUseStart(toolUse.Id, toolUse.Name);
                    break;
                default:
                    break;
            }
        }
    }
}
