using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Tools;

/// <summary>One property of an object schema built with <see cref="Schema.Object(SchemaProperty[])"/>.</summary>
/// <param name="Name">Property name (snake_case).</param>
/// <param name="Definition">Property schema.</param>
/// <param name="Required">Whether the property must be present.</param>
public sealed record SchemaProperty(string Name, JsonObject Definition, bool Required);

/// <summary>
/// Small builder for strict JSON Schemas (only keywords <see cref="JsonSchemaValidator"/> understands).
/// Every call returns a NEW JsonObject, so results can be embedded freely.
/// <example>
/// <code>
/// InputSchema = Schema.Object(
///     Schema.Req("level", Schema.String("Level name, e.g. 'Level 1'.")),
///     Schema.Req("start", Schema.Point2D("Start point (project length units).")),
///     Schema.Opt("height", Schema.Length("Unconnected height.", minimum: 0)))
/// </code>
/// </example>
/// </summary>
public static class Schema
{
    /// <summary>A required property.</summary>
    public static SchemaProperty Req(string name, JsonObject definition) => new(name, definition, Required: true);

    /// <summary>An optional property.</summary>
    public static SchemaProperty Opt(string name, JsonObject definition) => new(name, definition, Required: false);

    /// <summary>Object schema with "additionalProperties": false.</summary>
    public static JsonObject Object(params SchemaProperty[] properties) => Object(description: null, properties);

    /// <summary>Object schema with a description and "additionalProperties": false.</summary>
    public static JsonObject Object(string? description, params SchemaProperty[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var property in properties)
        {
            props[property.Name] = property.Definition.Parent is null ? property.Definition : property.Definition.DeepClone();
            if (property.Required)
            {
                required.Add(property.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object" };
        AddDescription(schema, description);
        schema["properties"] = props;
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        schema["additionalProperties"] = false;
        return schema;
    }

    /// <summary>String schema, optionally restricted to <paramref name="enumValues"/>.</summary>
    public static JsonObject String(string description, IEnumerable<string>? enumValues = null, int? minLength = null, int? maxLength = null, string? pattern = null)
    {
        var schema = Typed("string", description);
        if (enumValues is not null)
        {
            var values = new JsonArray();
            foreach (var value in enumValues)
            {
                values.Add(value);
            }

            schema["enum"] = values;
        }

        if (minLength is not null)
        {
            schema["minLength"] = minLength;
        }

        if (maxLength is not null)
        {
            schema["maxLength"] = maxLength;
        }

        if (pattern is not null)
        {
            schema["pattern"] = pattern;
        }

        return schema;
    }

    /// <summary>Number schema with optional inclusive bounds.</summary>
    public static JsonObject Number(string description, double? minimum = null, double? maximum = null)
    {
        var schema = Typed("number", description);
        if (minimum is not null)
        {
            schema["minimum"] = minimum;
        }

        if (maximum is not null)
        {
            schema["maximum"] = maximum;
        }

        return schema;
    }

    /// <summary>Integer schema with optional inclusive bounds.</summary>
    public static JsonObject Integer(string description, long? minimum = null, long? maximum = null)
    {
        var schema = Typed("integer", description);
        if (minimum is not null)
        {
            schema["minimum"] = minimum;
        }

        if (maximum is not null)
        {
            schema["maximum"] = maximum;
        }

        return schema;
    }

    /// <summary>Boolean schema.</summary>
    public static JsonObject Boolean(string description) => Typed("boolean", description);

    /// <summary>
    /// A length in the project's length units (see get_model_info "units"). A plain number is read in project
    /// units; tools read it with ToolContext.ReadLength in the add-in.
    /// </summary>
    public static JsonObject Length(string description, double? minimum = null, double? maximum = null) =>
        Number(description + " In project length units (see get_model_info).", minimum, maximum);

    /// <summary>An AutoCAD object id: the entity handle as an integer (Handle.Value).</summary>
    public static JsonObject ElementId(string description) => Integer(description);

    /// <summary>An array of AutoCAD object ids (handle values).</summary>
    public static JsonObject ElementIds(string description, int minItems = 1, int? maxItems = null) =>
        Array(description, Integer("Element id."), minItems, maxItems);

    /// <summary>Array schema.</summary>
    public static JsonObject Array(string description, JsonObject items, int? minItems = null, int? maxItems = null)
    {
        var schema = Typed("array", description);
        schema["items"] = items.Parent is null ? items : items.DeepClone();
        if (minItems is not null)
        {
            schema["minItems"] = minItems;
        }

        if (maxItems is not null)
        {
            schema["maxItems"] = maxItems;
        }

        return schema;
    }

    /// <summary>2D point {x, y} in project length units.</summary>
    public static JsonObject Point2D(string description) =>
        Object(
            description + " Coordinates in project length units.",
            Req("x", Number("X coordinate.")),
            Req("y", Number("Y coordinate.")));

    /// <summary>3D point {x, y, z} in project length units (z optional, defaults to 0 / the level).</summary>
    public static JsonObject Point3D(string description) =>
        Object(
            description + " Coordinates in project length units.",
            Req("x", Number("X coordinate.")),
            Req("y", Number("Y coordinate.")),
            Opt("z", Number("Z coordinate (default 0, relative to the level when a level applies).")));

    private static JsonObject Typed(string type, string description)
    {
        var schema = new JsonObject { ["type"] = type };
        AddDescription(schema, description);
        return schema;
    }

    private static void AddDescription(JsonObject schema, string? description)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            schema["description"] = description;
        }
    }
}
