using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Tools;

/// <summary>
/// Outcome of one tool call. Sent back over the pipe and, as JSON text, to the LLM.
/// Element ids are AutoCAD entity handles (Handle.Value) as <see cref="long"/>.
/// </summary>
public sealed record ToolResult
{
    private readonly IReadOnlyList<ToolImage> _images = [];

    /// <summary>True when the tool did what was asked (the transaction committed for write tools).</summary>
    public bool Success { get; init; }

    /// <summary>One-line human summary ("Created 4 walls on Level 1").</summary>
    public string? Message { get; init; }

    /// <summary>Structured payload for the LLM (lengths in project units unless the tool says otherwise).</summary>
    public JsonNode? Data { get; init; }

    /// <summary>Ids of elements created by this call.</summary>
    public IReadOnlyList<long> CreatedIds { get; init; } = [];

    /// <summary>Ids of elements changed by this call.</summary>
    public IReadOnlyList<long> ModifiedIds { get; init; } = [];

    /// <summary>Ids of elements deleted by this call.</summary>
    public IReadOnlyList<long> DeletedIds { get; init; } = [];

    /// <summary>AutoCAD warnings (failure messages of warning severity) and tool notes.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Errors that made the call fail (AutoCAD errors, validation errors, exceptions).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Model revision after the call (increments on every AutoCAD document change, including undo).</summary>
    public long Revision { get; init; }

    /// <summary>
    /// Images for the LLM, for example capture_view's PNG of the drawing view. Empty for most tools (never null).
    /// The MCP bridge sends them as MCP image content after the JSON text; <see cref="ToTextJson"/> describes them
    /// without their data. On the wire they appear as "images" only when there is at least one, so results without
    /// images keep exactly the JSON they had before images existed.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ToolImage> Images
    {
        get => _images;
        init => _images = value ?? [];
    }

    /// <summary>Wire form of <see cref="Images"/>: null (omitted) when empty; a missing or null "images" reads as empty.</summary>
    [JsonInclude]
    [JsonPropertyName("images")]
    private IReadOnlyList<ToolImage>? WireImages
    {
        get => _images.Count == 0 ? null : _images;
        init => _images = value ?? [];
    }

    /// <summary>A successful result.</summary>
    public static ToolResult Ok(string? message = null, JsonNode? data = null) =>
        new() { Success = true, Message = message, Data = data };

    /// <summary>A failed result with one or more errors; the first error is also the message.</summary>
    public static ToolResult Fail(string error, params string[] moreErrors) =>
        new() { Success = false, Message = error, Errors = [error, .. moreErrors] };

    /// <summary>A failed result listing several errors under a summary message.</summary>
    public static ToolResult Fail(string message, IEnumerable<string> errors) =>
        new() { Success = false, Message = message, Errors = [.. errors] };

    /// <summary>Compact JSON with the shared <see cref="CopilotJson.Options"/>, image data included (the pipe format).</summary>
    public string ToJson() => CopilotJson.Serialize(this);

    /// <summary>
    /// Compact JSON for text channels: the text part of the MCP result the LLM reads, logs and the panel. Identical to
    /// <see cref="ToJson"/> except that each image is described ({mimeType, width, height, bytes}) instead of carrying
    /// its base64 data.
    /// </summary>
    public string ToTextJson()
    {
        if (_images.Count == 0)
        {
            return ToJson();
        }

        var node = CopilotJson.ToNode(this with { Images = [] })!.AsObject();
        node["images"] = new JsonArray([.. _images.Select(image => (JsonNode)image.Describe())]);
        return node.ToJsonString(CopilotJson.Options);
    }
}
