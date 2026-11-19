using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Tools;

/// <summary>
/// Validates JSON values against the subset of JSON Schema the tools use:
/// type (string or array of types: object, array, string, number, integer, boolean, null), enum, const,
/// properties, required, additionalProperties (bool or schema), items, minItems, maxItems, uniqueItems,
/// minimum, maximum, exclusiveMinimum, exclusiveMaximum, minLength, maxLength, pattern,
/// anyOf, oneOf, allOf. Annotation keywords (description, title, default, examples, format) are ignored.
/// Errors are reported as "$.path: message".
/// </summary>
public static class JsonSchemaValidator
{
    private const double IntegerTolerance = 1e-9;

    /// <summary>Validates <paramref name="instance"/> against <paramref name="schema"/>; returns the errors (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(JsonObject schema, JsonNode? instance)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var errors = new List<string>();
        ValidateNode(schema, instance, "$", errors);
        return errors;
    }

    /// <summary>Returns true when <paramref name="instance"/> is valid.</summary>
    public static bool IsValid(JsonObject schema, JsonNode? instance) => Validate(schema, instance).Count == 0;

    private static void ValidateNode(JsonObject schema, JsonNode? instance, string path, List<string> errors)
    {
        if (schema["type"] is { } typeNode && !MatchesType(typeNode, instance))
        {
            errors.Add($"{path}: expected {DescribeType(typeNode)}, got {CopilotJson.KindName(instance)}.");
            return;
        }

        if (schema["const"] is { } constNode && !JsonNode.DeepEquals(constNode, instance))
        {
            errors.Add($"{path}: must be {constNode.ToJsonString()}.");
        }

        if (schema["enum"] is JsonArray enumValues && !enumValues.Any(v => JsonNode.DeepEquals(v, instance)))
        {
            errors.Add($"{path}: must be one of {string.Join(", ", enumValues.Select(v => v?.ToJsonString() ?? "null"))}; got {instance?.ToJsonString() ?? "null"}.");
        }

        switch (instance)
        {
            case JsonObject obj:
                ValidateObject(schema, obj, path, errors);
                break;
            case JsonArray array:
                ValidateArray(schema, array, path, errors);
                break;
            case JsonValue value:
                ValidateScalar(schema, value, path, errors);
                break;
        }

        ValidateCombinators(schema, instance, path, errors);
    }

    private static void ValidateObject(JsonObject schema, JsonObject obj, string path, List<string> errors)
    {
        var properties = schema["properties"] as JsonObject;
        if (schema["required"] is JsonArray required)
        {
            foreach (var name in required.Select(n => CopilotJson.TryGetString(n)).OfType<string>())
            {
                if (!obj.ContainsKey(name))
                {
                    errors.Add($"{path}: missing required property '{name}'.");
                }
            }
        }

        foreach (var (name, value) in obj)
        {
            var childPath = $"{path}.{name}";
            if (properties?[name] is JsonObject propertySchema)
            {
                ValidateNode(propertySchema, value, childPath, errors);
                continue;
            }

            switch (schema["additionalProperties"])
            {
                case JsonValue flag when flag.GetValueKind() == JsonValueKind.False:
                    var allowed = properties is null ? "none" : string.Join(", ", properties.Select(p => p.Key));
                    errors.Add($"{path}: unknown property '{name}'. Allowed: {allowed}.");
                    break;
                case JsonObject additionalSchema:
                    ValidateNode(additionalSchema, value, childPath, errors);
                    break;
            }
        }
    }

    private static void ValidateArray(JsonObject schema, JsonArray array, string path, List<string> errors)
    {
        if (ReadInt(schema, "minItems") is { } minItems && array.Count < minItems)
        {
            errors.Add($"{path}: needs at least {minItems} item(s), got {array.Count}.");
        }

        if (ReadInt(schema, "maxItems") is { } maxItems && array.Count > maxItems)
        {
            errors.Add($"{path}: allows at most {maxItems} item(s), got {array.Count}.");
        }

        if (CopilotJson.TryGetBool(schema["uniqueItems"], out var unique) && unique)
        {
            for (var i = 0; i < array.Count; i++)
            {
                for (var j = i + 1; j < array.Count; j++)
                {
                    if (JsonNode.DeepEquals(array[i], array[j]))
                    {
                        errors.Add($"{path}: items {i} and {j} are duplicates.");
                    }
                }
            }
        }

        if (schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < array.Count; i++)
            {
                ValidateNode(itemSchema, array[i], $"{path}[{i}]", errors);
            }
        }
    }

    private static void ValidateScalar(JsonObject schema, JsonValue value, string path, List<string> errors)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                var text = value.GetValue<string>();
                var length = new System.Globalization.StringInfo(text).LengthInTextElements;
                if (ReadInt(schema, "minLength") is { } minLength && length < minLength)
                {
                    errors.Add($"{path}: must be at least {minLength} character(s).");
                }

                if (ReadInt(schema, "maxLength") is { } maxLength && length > maxLength)
                {
                    errors.Add($"{path}: must be at most {maxLength} character(s).");
                }

                if (CopilotJson.TryGetString(schema["pattern"]) is { } pattern && !Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    errors.Add($"{path}: must match pattern {pattern}.");
                }

                break;

            case JsonValueKind.Number:
                if (!CopilotJson.TryGetDouble(value, out var number))
                {
                    errors.Add($"{path}: is not a readable number.");
                    break;
                }

                if (CopilotJson.TryGetDouble(schema["minimum"], out var minimum) && number < minimum)
                {
                    errors.Add($"{path}: must be >= {minimum}, got {number}.");
                }

                if (CopilotJson.TryGetDouble(schema["maximum"], out var maximum) && number > maximum)
                {
                    errors.Add($"{path}: must be <= {maximum}, got {number}.");
                }

                if (CopilotJson.TryGetDouble(schema["exclusiveMinimum"], out var exclusiveMinimum) && number <= exclusiveMinimum)
                {
                    errors.Add($"{path}: must be > {exclusiveMinimum}, got {number}.");
                }

                if (CopilotJson.TryGetDouble(schema["exclusiveMaximum"], out var exclusiveMaximum) && number >= exclusiveMaximum)
                {
                    errors.Add($"{path}: must be < {exclusiveMaximum}, got {number}.");
                }

                break;
        }
    }

    private static void ValidateCombinators(JsonObject schema, JsonNode? instance, string path, List<string> errors)
    {
        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var sub in allOf.OfType<JsonObject>())
            {
                ValidateNode(sub, instance, path, errors);
            }
        }

        if (schema["anyOf"] is JsonArray anyOf)
        {
            var branches = anyOf.OfType<JsonObject>().Select(sub => Validate(sub, instance, path)).ToList();
            if (branches.Count > 0 && branches.All(b => b.Count > 0))
            {
                errors.Add($"{path}: does not match any allowed form ({string.Join(" | ", branches.Select(b => b[0]))}).");
            }
        }

        if (schema["oneOf"] is JsonArray oneOf)
        {
            var branches = oneOf.OfType<JsonObject>().Select(sub => Validate(sub, instance, path)).ToList();
            var matches = branches.Count(b => b.Count == 0);
            if (matches == 0)
            {
                errors.Add($"{path}: does not match any allowed form ({string.Join(" | ", branches.Select(b => b.Count > 0 ? b[0] : string.Empty))}).");
            }
            else if (matches > 1)
            {
                errors.Add($"{path}: matches more than one allowed form.");
            }
        }
    }

    private static List<string> Validate(JsonObject schema, JsonNode? instance, string path)
    {
        var errors = new List<string>();
        ValidateNode(schema, instance, path, errors);
        return errors;
    }

    private static bool MatchesType(JsonNode typeNode, JsonNode? instance)
    {
        if (typeNode is JsonArray types)
        {
            return types.Any(t => MatchesSingleType(CopilotJson.TryGetString(t), instance));
        }

        return MatchesSingleType(CopilotJson.TryGetString(typeNode), instance);
    }

    private static bool MatchesSingleType(string? type, JsonNode? instance)
    {
        var kind = CopilotJson.KindName(instance);
        return type switch
        {
            "object" or "array" or "string" or "boolean" or "null" => kind == type,
            "number" => kind == "number",
            "integer" => kind == "number" && CopilotJson.TryGetDouble(instance, out var n) && Math.Abs(n - Math.Round(n)) < IntegerTolerance,
            null => true,
            _ => false,
        };
    }

    private static string DescribeType(JsonNode typeNode) =>
        typeNode is JsonArray types
            ? string.Join(" or ", types.Select(t => CopilotJson.TryGetString(t) ?? "?"))
            : CopilotJson.TryGetString(typeNode) ?? "?";

    private static int? ReadInt(JsonObject schema, string keyword) =>
        CopilotJson.TryGetDouble(schema[keyword], out var value) ? (int)value : null;
}
