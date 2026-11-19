using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AcadCopilot.Core.Json;

/// <summary>
/// Shared System.Text.Json settings used on every wire (named pipe, MCP bridge, settings files).
/// camelCase property names, enums as snake_case strings ("in_progress", "destructive"),
/// nulls omitted, case-insensitive reading.
/// </summary>
public static class CopilotJson
{
    /// <summary>Compact options used for all wire traffic.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    /// <summary>Indented options used for files people may read (settings, discovery files).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Serializes a value to a <see cref="JsonNode"/> with <see cref="Options"/>.</summary>
    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>Serializes a value to a compact JSON string with <see cref="Options"/>.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializes a node with <see cref="Options"/>; throws <see cref="JsonException"/> when it is null or malformed.</summary>
    public static T FromNode<T>(JsonNode? node)
    {
        if (node is null)
        {
            throw new JsonException($"Expected a JSON value for {typeof(T).Name} but got null.");
        }

        return node.Deserialize<T>(Options) ?? throw new JsonException($"Could not read {typeof(T).Name} from JSON.");
    }

    /// <summary>Deserializes a JSON string with <see cref="Options"/>; throws <see cref="JsonException"/> when malformed.</summary>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Could not read {typeof(T).Name} from JSON.");

    /// <summary>
    /// Reads a JSON number as a double regardless of how the node is backed
    /// (parsed JSON, or a CLR int/long/double placed into a node). Returns false for non-numbers.
    /// </summary>
    public static bool TryGetDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue || jsonValue.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        return double.TryParse(jsonValue.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Returns the string held by a JSON string node, or null for any other kind.</summary>
    public static string? TryGetString(JsonNode? node) =>
        node is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String ? jsonValue.GetValue<string>() : null;

    /// <summary>Returns true when the node is a JSON boolean, reporting its value.</summary>
    public static bool TryGetBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        switch (jsonValue.GetValueKind())
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Human-readable JSON kind name used in validation messages ("string", "number", "object", ...).</summary>
    public static string KindName(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "unknown",
        },
        _ => "unknown",
    };
}
