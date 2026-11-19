using System.Text.Json.Nodes;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Tools;

/// <summary>
/// Thrown by tools (and argument getters) for problems the LLM can fix: bad input, missing level,
/// nothing selected... The message is returned to the LLM as the tool error, so make it actionable.
/// </summary>
public class ToolException : Exception
{
    /// <summary>Creates the exception with an actionable message.</summary>
    public ToolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an actionable message and the underlying cause.</summary>
    public ToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Typed read access to a tool's JSON arguments. Arguments are schema-validated before a tool runs,
/// so these getters mostly guard against schema/implementation mismatches; they throw
/// <see cref="ToolException"/> with a clear message on wrong or missing values.
/// </summary>
public sealed class ToolArguments
{
    /// <summary>Wraps <paramref name="raw"/> (null means "no arguments").</summary>
    public ToolArguments(JsonObject? raw)
    {
        Raw = raw ?? [];
    }

    /// <summary>The raw argument object.</summary>
    public JsonObject Raw { get; }

    /// <summary>True when the argument is present and not JSON null.</summary>
    public bool Has(string name) => Raw.TryGetPropertyValue(name, out var node) && node is not null;

    /// <summary>The raw node of an argument, or null.</summary>
    public JsonNode? Node(string name) => Raw.TryGetPropertyValue(name, out var node) ? node : null;

    /// <summary>Required string.</summary>
    public string GetString(string name) =>
        CopilotJson.TryGetString(Require(name)) ?? throw Wrong(name, "a string");

    /// <summary>Optional string (null when absent).</summary>
    public string? GetStringOrNull(string name) =>
        Has(name) ? CopilotJson.TryGetString(Node(name)) ?? throw Wrong(name, "a string") : null;

    /// <summary>Required number.</summary>
    public double GetDouble(string name) =>
        CopilotJson.TryGetDouble(Require(name), out var value) ? value : throw Wrong(name, "a number");

    /// <summary>Optional number (<paramref name="fallback"/> when absent).</summary>
    public double GetDouble(string name, double fallback) => Has(name) ? GetDouble(name) : fallback;

    /// <summary>Optional number (null when absent).</summary>
    public double? GetDoubleOrNull(string name) => Has(name) ? GetDouble(name) : null;

    /// <summary>Required integer (also used for element ids).</summary>
    public long GetLong(string name)
    {
        var value = GetDouble(name);
        if (Math.Abs(value - Math.Round(value)) > 1e-9)
        {
            throw Wrong(name, "an integer");
        }

        return (long)Math.Round(value);
    }

    /// <summary>Optional integer (null when absent).</summary>
    public long? GetLongOrNull(string name) => Has(name) ? GetLong(name) : null;

    /// <summary>Optional integer (<paramref name="fallback"/> when absent).</summary>
    public long GetLong(string name, long fallback) => Has(name) ? GetLong(name) : fallback;

    /// <summary>Optional boolean (<paramref name="fallback"/> when absent).</summary>
    public bool GetBool(string name, bool fallback = false)
    {
        if (!Has(name))
        {
            return fallback;
        }

        return CopilotJson.TryGetBool(Node(name), out var value) ? value : throw Wrong(name, "true or false");
    }

    /// <summary>Required array of integers (element ids).</summary>
    public IReadOnlyList<long> GetLongList(string name) =>
        GetArray(name).Select((node, i) => CopilotJson.TryGetDouble(node, out var v) ? (long)Math.Round(v) : throw Wrong($"{name}[{i}]", "an integer")).ToList();

    /// <summary>Required array of strings.</summary>
    public IReadOnlyList<string> GetStringList(string name) =>
        GetArray(name).Select((node, i) => CopilotJson.TryGetString(node) ?? throw Wrong($"{name}[{i}]", "a string")).ToList();

    /// <summary>Required array.</summary>
    public JsonArray GetArray(string name) => Require(name) as JsonArray ?? throw Wrong(name, "an array");

    /// <summary>Required object, wrapped as arguments for nested reads.</summary>
    public ToolArguments GetObject(string name) =>
        new(Require(name) as JsonObject ?? throw Wrong(name, "an object"));

    /// <summary>Optional object (null when absent).</summary>
    public ToolArguments? GetObjectOrNull(string name) => Has(name) ? GetObject(name) : null;

    /// <summary>Wraps each element of an array of objects.</summary>
    public IReadOnlyList<ToolArguments> GetObjectList(string name) =>
        GetArray(name).Select((node, i) => new ToolArguments(node as JsonObject ?? throw Wrong($"{name}[{i}]", "an object"))).ToList();

    private JsonNode Require(string name) =>
        Raw.TryGetPropertyValue(name, out var node) && node is not null
            ? node
            : throw new ToolException($"Missing required argument '{name}'.");

    private static ToolException Wrong(string name, string expected) =>
        new($"Argument '{name}' must be {expected}.");
}
