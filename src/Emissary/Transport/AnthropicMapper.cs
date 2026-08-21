using System.Text.Json;
using Anthropic.Models.Messages;

namespace Emissary.Transport;

/// <summary>Pure mapping from Emissary's model to Anthropic SDK request types.</summary>
internal static class AnthropicMapper
{
    public static MessageCreateParams ToCreateParams(ModelRequest request)
    {
        ThinkingConfigParam thinking = request.Thinking == ThinkingMode.Adaptive
            ? new ThinkingConfigAdaptive()
            : new ThinkingConfigDisabled();

        return new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = request.System is { } system ? (MessageCreateParamsSystem)system : null,
            Thinking = thinking,
            OutputConfig = BuildOutputConfig(request),
            Tools = request.Tools.Count > 0 ? request.Tools.Select(t => (ToolUnion)ToTool(t)).ToList() : null,
            Messages = request.Messages.Select(ToMessageParam).ToList(),
        };
    }

    private static OutputConfig? BuildOutputConfig(ModelRequest request)
    {
        if (request.Effort is null && request.OutputSchemaJson is null)
        {
            return null;
        }

        return new OutputConfig
        {
            Effort = request.Effort is { } effort ? ToEffort(effort) : default,
            Format = request.OutputSchemaJson is { } schema
                ? new JsonOutputFormat { Schema = ParseSchemaObject(schema) }
                : default,
        };
    }

    internal static Dictionary<string, JsonElement> ParseSchemaObject(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        var schema = new Dictionary<string, JsonElement>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            schema[property.Name] = property.Value.Clone();
        }

        return schema;
    }

    public static Tool ToTool(ToolDefinition tool)
    {
        var (properties, required) = ParseSchema(tool.InputSchemaJson);
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new()
            {
                Properties = properties,
                Required = required,
            },
        };
    }

    internal static (Dictionary<string, JsonElement> Properties, List<string>? Required) ParseSchema(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        var properties = new Dictionary<string, JsonElement>();
        foreach (var property in document.RootElement.GetProperty("properties").EnumerateObject())
        {
            properties[property.Name] = property.Value.Clone();
        }

        List<string>? required = null;
        if (document.RootElement.TryGetProperty("required", out JsonElement requiredElement))
        {
            required = requiredElement.EnumerateArray().Select(e => e.GetString()!).ToList();
        }

        return (properties, required);
    }

    public static MessageParam ToMessageParam(Message message) => new()
    {
        Role = message.Role == MessageRole.User ? Role.User : Role.Assistant,
        Content = message.Content.Select(ToContentParam).ToList(),
    };

    public static ContentBlockParam ToContentParam(ContentBlock block) => block switch
    {
        TextBlock text => new TextBlockParam { Text = text.Text },
        ThinkingBlock thinking => new ThinkingBlockParam
        {
            Thinking = thinking.Thinking,
            Signature = thinking.Signature ?? "",
        },
        RedactedThinkingBlock redacted => new RedactedThinkingBlockParam { Data = redacted.Data },
        ToolUseBlock toolUse => new ToolUseBlockParam
        {
            ID = toolUse.Id,
            Name = toolUse.Name,
            Input = ToInputDictionary(toolUse.Input),
        },
        _ => ToToolResultParam((ToolResultBlock)block),
    };

    private static ToolResultBlockParam ToToolResultParam(ToolResultBlock result) => new()
    {
        ToolUseID = result.ToolUseId,
        Content = result.Content,
        IsError = result.IsError,
    };

    internal static Dictionary<string, JsonElement> ToInputDictionary(JsonElement input)
    {
        var dictionary = new Dictionary<string, JsonElement>();
        foreach (var property in input.EnumerateObject())
        {
            dictionary[property.Name] = property.Value;
        }

        return dictionary;
    }

    internal static Effort ToEffort(EffortLevel effort) => effort switch
    {
        EffortLevel.Low => Effort.Low,
        EffortLevel.Medium => Effort.Medium,
        EffortLevel.High => Effort.High,
        _ => Effort.Max,
    };
}
