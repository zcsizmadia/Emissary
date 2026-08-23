using System.Text.Json;
using Anthropic.Models.Messages;

namespace Emissary.Transport;

/// <summary>Pure mapping from Emissary's model to Anthropic SDK request types.</summary>
internal static class AnthropicMapper
{
    public static MessageCreateParams ToCreateParams(ModelRequest request)
    {
        bool cache = request.PromptCaching == PromptCacheMode.Automatic;
        ThinkingConfigParam thinking = request.Thinking == ThinkingMode.Adaptive
            ? new ThinkingConfigAdaptive()
            : new ThinkingConfigDisabled();

        List<ToolUnion>? tools = null;
        if (request.Tools.Count > 0)
        {
            tools = new List<ToolUnion>(request.Tools.Count);
            for (int i = 0; i < request.Tools.Count; i++)
            {
                tools.Add(ToTool(request.Tools[i], cache && i == request.Tools.Count - 1));
            }
        }

        if (request.WebSearch is { } webSearch)
        {
            tools ??= [];
            tools.Add(new ToolUnion(ToWebSearchTool(webSearch)));
        }

        var messages = new List<MessageParam>(request.Messages.Count);
        for (int i = 0; i < request.Messages.Count; i++)
        {
            messages.Add(ToMessageParam(request.Messages[i], cache && i == request.Messages.Count - 1));
        }

        return new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = ToSystem(request.System, cache),
            Thinking = thinking,
            OutputConfig = BuildOutputConfig(request),
            Tools = tools,
            Messages = messages,
        };
    }

    /// <summary>
    /// Maps the system prompt; with caching, it becomes a text block carrying a cache breakpoint
    /// so the tools + system prefix is served from cache on every follow-up call.
    /// </summary>
    internal static MessageCreateParamsSystem? ToSystem(string? system, bool cache)
    {
        if (system is null)
        {
            return null;
        }

        if (!cache)
        {
            return system;
        }

        return new List<TextBlockParam>
        {
            new() { Text = system, CacheControl = new CacheControlEphemeral() },
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

    internal static WebSearchTool20260318 ToWebSearchTool(WebSearchOptions options) => new()
    {
        MaxUses = options.MaxUses,
        AllowedDomains = options.AllowedDomains.Count > 0 ? [.. options.AllowedDomains] : null,
        BlockedDomains = options.BlockedDomains.Count > 0 ? [.. options.BlockedDomains] : null,
    };

    public static Tool ToTool(ToolDefinition tool, bool cache = false)
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
            CacheControl = cache ? new CacheControlEphemeral() : null,
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

    public static MessageParam ToMessageParam(Message message, bool cacheLastBlock = false)
    {
        var content = new List<ContentBlockParam>(message.Content.Length);
        for (int i = 0; i < message.Content.Length; i++)
        {
            content.Add(ToContentParam(message.Content[i], cacheLastBlock && i == message.Content.Length - 1));
        }

        return new MessageParam
        {
            Role = message.Role == MessageRole.User ? Role.User : Role.Assistant,
            Content = content,
        };
    }

    // The moving conversation breakpoint only lands on cacheable block kinds (text and
    // tool_result — the shapes a request's final message actually ends with).
    public static ContentBlockParam ToContentParam(ContentBlock block, bool cache = false) => block switch
    {
        TextBlock text => new TextBlockParam
        {
            Text = text.Text,
            CacheControl = cache ? new CacheControlEphemeral() : null,
        },
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
        _ => ToToolResultParam((ToolResultBlock)block, cache),
    };

    private static ToolResultBlockParam ToToolResultParam(ToolResultBlock result, bool cache) => new()
    {
        ToolUseID = result.ToolUseId,
        Content = result.Content,
        IsError = result.IsError,
        CacheControl = cache ? new CacheControlEphemeral() : null,
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

    /// <summary>
    /// Normalizes a stop reason to Emissary's canonical wire values, tolerating every form the SDK
    /// has been observed to produce: the wire value from <c>ApiEnum.Raw()</c> (<c>tool_use</c>), the
    /// JSON rendering from <c>ToString()</c> (<c>"tool_use"</c>, quotes included), and PascalCase
    /// (<c>ToolUse</c>). Read <c>Raw()</c> at the call site; the leniency here is a safety net for
    /// an SDK still in beta, not a licence to pass <c>ToString()</c>.
    /// </summary>
    internal static string NormalizeStopReason(string raw)
    {
        switch (raw.Trim('"').Replace("_", "").ToLowerInvariant())
        {
            case "tooluse":
                return "tool_use";
            case "maxtokens":
            // The context window overflowed, so the answer is cut short for the same reason a token
            // limit cuts it short — callers that handle one should handle the other.
            case "modelcontextwindowexceeded":
                return "max_tokens";
            case "refusal":
                return "refusal";
            case "pauseturn":
                // A server-side tool (web search) paused the turn mid-flight. The answer is
                // incomplete, so reporting it as a normal completion would be a lie.
                return "pause_turn";
            default:
                // end_turn, stop_sequence, and anything else are normal completions.
                return "end_turn";
        }
    }

    internal static Effort ToEffort(EffortLevel effort) => effort switch
    {
        EffortLevel.Low => Effort.Low,
        EffortLevel.Medium => Effort.Medium,
        EffortLevel.High => Effort.High,
        _ => Effort.Max,
    };
}
