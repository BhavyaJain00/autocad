using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AcadCopilot.Plugin.Interaction;

/// <summary>
/// Pure text helpers of "click to point" and "select and ask": the point tokens put into the message
/// ("[point 12'-6\",30'-0\" (150,360)]"), the selection chip and the selection block a message carries.
/// </summary>
public static class InteractionText
{
    /// <summary>Most selected objects listed in a message (the AI gets the rest with get_selection).</summary>
    public const int MaxObjectsInMessage = 25;

    /// <summary>A number in drawing units for messages: invariant, at most 2 decimals, no "-0".</summary>
    public static string Number(double value)
    {
        var rounded = Math.Round(value, 2);
        if (rounded == 0)
        {
            rounded = 0;
        }

        return rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>"[point 12'-6\",30'-0\" (150,360)]", or "[point (150,360)]" when the drawing has no feet-inch units.</summary>
    public static string PointToken(PickedPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        var units = $"({Number(point.X)},{Number(point.Y)})";
        return point.XText is { Length: > 0 } x && point.YText is { Length: > 0 } y
            ? $"[point {x},{y} {units}]"
            : $"[point {units}]";
    }

    /// <summary>The tokens of several points, separated by spaces.</summary>
    public static string PointTokens(IEnumerable<PickedPoint> points) => string.Join(" ", points.Select(PointToken));

    /// <summary>
    /// Inserts <paramref name="token"/> into <paramref name="text"/> at <paramref name="caret"/> (clamped), with a space on
    /// each side when the neighbouring characters are not spaces. Returns the new text and the caret after the token.
    /// </summary>
    public static (string Text, int Caret) Insert(string? text, int caret, string token)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        var before = text[..caret];
        var after = text[caret..];
        var lead = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : string.Empty;
        var trail = after.Length > 0 && !char.IsWhiteSpace(after[0]) ? " " : string.Empty;
        var inserted = lead + token + trail;
        return (before + inserted + after, before.Length + lead.Length + token.Length + trail.Length);
    }

    /// <summary>"1 object selected", "5 objects selected".</summary>
    public static string SelectionChip(SelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Count == 1 ? "1 object selected" : $"{selection.Count.ToString(CultureInfo.InvariantCulture)} objects selected";
    }

    /// <summary>"3 LINE, 2 LWPOLYLINE" (most common type first).</summary>
    public static string TypeBreakdown(SelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var groups = selection.Items
            .GroupBy(i => i.Type, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} {g.Key}")
            .ToList();
        var described = selection.Items.Count;
        if (selection.Count > described)
        {
            groups.Add($"{(selection.Count - described).ToString(CultureInfo.InvariantCulture)} more");
        }

        return string.Join(", ", groups);
    }

    /// <summary>Tool tip of the selection chip: the breakdown and the drawing.</summary>
    public static string SelectionToolTip(SelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var where = selection.DrawingTitle is { Length: > 0 } title ? $" in {title}" : string.Empty;
        return $"{SelectionChip(selection)}{where}: {TypeBreakdown(selection)}.\nYour message carries their handles, so you can ask about them (\"make these walls 9 inch\"). Click x to leave them out.";
    }

    /// <summary>
    /// The block appended to a message while objects are selected:
    /// <code>
    /// [selected in AutoCAD: 5 objects in House.dwg (3 LINE, 2 LWPOLYLINE)
    /// - 2A3F LINE on A-WALL, length 12'-0"
    /// ...]
    /// </code>
    /// </summary>
    public static string SelectionBlock(SelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var builder = new StringBuilder();
        var count = selection.Count == 1 ? "1 object" : $"{selection.Count.ToString(CultureInfo.InvariantCulture)} objects";
        var where = selection.DrawingTitle is { Length: > 0 } title ? $" in {title}" : string.Empty;
        builder.Append(CultureInfo.InvariantCulture, $"[selected in AutoCAD: {count}{where} ({TypeBreakdown(selection)})");
        foreach (var item in selection.Items.Take(MaxObjectsInMessage))
        {
            builder.Append('\n').Append("- ").Append(Describe(item));
        }

        var listed = Math.Min(selection.Items.Count, MaxObjectsInMessage);
        if (selection.Count > listed)
        {
            builder.Append('\n').Append(CultureInfo.InvariantCulture, $"- ... {selection.Count - listed} more (call get_selection for all handles and details)");
        }

        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>"2A3F LINE on A-WALL, length 12'-0\"".</summary>
    public static string Describe(SelectedObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = $"{item.Handle} {item.Type} on {item.Layer}";
        return string.IsNullOrWhiteSpace(item.Detail) ? text : text + ", " + item.Detail;
    }

    /// <summary>
    /// A short detail from an entity summary (the JSON of Inspect/EntitySummary.Summarize): block name, text, length,
    /// radius or area, whichever the object has. Empty when none applies.
    /// </summary>
    public static string ShortDetail(JsonObject? summary)
    {
        if (summary is null)
        {
            return string.Empty;
        }

        if (Text(summary["block"]) is { Length: > 0 } block)
        {
            return "block " + block;
        }

        if ((Text(summary["text"]) ?? Text(summary["contents"])) is { Length: > 0 } text)
        {
            var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return "\"" + (line.Length > 40 ? line[..40].TrimEnd() + "…" : line) + "\"";
        }

        if (summary["measurement"] is { } measurement && LengthText(measurement) is { } measured)
        {
            return "measures " + measured;
        }

        if (summary["length"] is { } length && LengthText(length) is { } lengthText)
        {
            return "length " + lengthText;
        }

        if (summary["radius"] is { } radius && LengthText(radius) is { } radiusText)
        {
            return "radius " + radiusText;
        }

        if (Text(summary["pattern"]) is { Length: > 0 } pattern)
        {
            return "pattern " + pattern;
        }

        return string.Empty;
    }

    // {"du": 144, "ftIn": "12'-0\""} -> 12'-0" (or 144); a plain number -> that number.
    private static string? LengthText(JsonNode node)
    {
        if (node is JsonObject json)
        {
            if (Text(json["ftIn"]) is { Length: > 0 } feet)
            {
                return feet;
            }

            return json["du"] is JsonValue du && du.TryGetValue<double>(out var value) ? Number(value) : null;
        }

        return node is JsonValue plain && plain.TryGetValue<double>(out var number) ? Number(number) : Text(node);
    }

    private static string? Text(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text.Trim();
        }

        return null;
    }
}
