using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Learning;

/// <summary>
/// Pure text helpers of the "Learn from me" recorder: which command-line lines of a command are worth keeping, how a
/// line splits into the prompt and the user's answer, feet-inch notes for typed distances, and one-line descriptions of
/// the objects a command created (from the plug-in's entity summaries).
/// </summary>
public static class RecordingText
{
    private static readonly string[] LengthWords = ["distance", "length", "radius", "diameter", "height", "width", "offset", "spacing", "thickness"];

    /// <summary>
    /// The lines worth keeping of a command's new command-line history (oldest first): cleaned, without empty lines,
    /// "Command:" echoes, SYSMON notices and repeats of the line before.
    /// </summary>
    public static IReadOnlyList<string> CommandLines(IEnumerable<string> lines, string? command)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var kept = new List<string>();
        foreach (var raw in lines)
        {
            var line = CommandLineText.Clean(raw);
            if (line.Length == 0 || CommandLineText.IsCommandPrompt(line) || CommandLineText.IsSystemNotice(line))
            {
                continue;
            }

            if (line.StartsWith("Command:", StringComparison.OrdinalIgnoreCase))
            {
                // "Command: LINE" / "Command: _line": the command itself is the action's name.
                continue;
            }

            if (kept.Count > 0 && string.Equals(kept[^1], line, StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(KnowledgeText.Truncate(line, UserRecording.MaxTextLength));
        }

        return kept;
    }

    /// <summary>
    /// Splits a command-line line into its prompt and the user's answer: "Specify offset distance or [Through] &lt;1'&gt;: 9"
    /// gives ("Specify offset distance or [Through] &lt;1'&gt;:", "9"). A bare prompt ("Specify next point or [Undo]:") has
    /// no typed answer (a pick on screen or Enter). Anything else is a message ("1 found", "Current settings: ...").
    /// </summary>
    public static (string? Prompt, string? Answer, string? Message) SplitPromptLine(string? line)
    {
        var text = CommandLineText.Clean(line);
        if (text.Length == 0)
        {
            return (null, null, null);
        }

        if (text[^1] == ':')
        {
            return (text, null, null);
        }

        var cut = text.LastIndexOf(": ", StringComparison.Ordinal);
        if (cut <= 0 || text.StartsWith("Current settings", StringComparison.OrdinalIgnoreCase) || (text.Contains(" = ", StringComparison.Ordinal) && !IsPromptStart(text)))
        {
            return (null, null, text);
        }

        var prompt = text[..(cut + 1)];
        if (!IsPromptStart(prompt) && !prompt.Contains('[', StringComparison.Ordinal) && !prompt.Contains('<', StringComparison.Ordinal))
        {
            return (null, null, text);
        }

        return (prompt, text[(cut + 2)..].Trim(), null);
    }

    /// <summary>
    /// A note for a typed distance at a prompt about a distance, length, radius, height or width: "= 0'-9"" for a bare
    /// number, "= 36 drawing units" for feet-inch input. Null for points, options and when the scale is unknown.
    /// </summary>
    public static string? LengthNote(string? prompt, string? answer, DrawingUnits? units)
    {
        if (units?.InchesPerUnit is not { } perUnit || string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        // Only the question itself counts, not the options ("[Arc/Halfwidth/Length/Undo/Width]") or the default ("<9>").
        var question = prompt.Split('[', '<')[0].ToLowerInvariant();
        if (!LengthWords.Any(w => question.Contains(w, StringComparison.Ordinal)))
        {
            return null;
        }

        var value = answer.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return units.ToFeetInches(number) is { } ftIn ? "= " + ftIn : null;
        }

        if (FeetInches.ContainsFeetInchNotation(value) && FeetInches.TryParse(value, out var inches))
        {
            var du = DrawingUnits.Round(inches / perUnit);
            return "= " + du.ToString("0.######", CultureInfo.InvariantCulture) + " drawing units";
        }

        return null;
    }

    /// <summary>
    /// One line for an object from its compact entity summary (the plug-in's EntitySummary: type, layer, geometry with
    /// {du, ftIn} lengths and {x, y} points): "from (0,0) to (120,0), length 120 (10'-0")". Unknown fields are skipped.
    /// </summary>
    public static string DescribeObject(JsonObject summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var parts = new List<string>();
        var type = Text(summary["type"]) ?? "object";
        switch (type)
        {
            case "LINE":
                AddRange(parts, summary);
                AddLength(parts, "length", summary["length"]);
                if (Number(summary["angle"]) is { } angle)
                {
                    parts.Add("angle " + Format(angle) + "°");
                }

                break;
            case "ARC":
                AddPoint(parts, "centre", summary["center"]);
                AddLength(parts, "radius", summary["radius"]);
                if (Number(summary["startAngle"]) is { } start && Number(summary["endAngle"]) is { } end)
                {
                    parts.Add($"{Format(start)}° to {Format(end)}°");
                }

                break;
            case "CIRCLE":
                AddPoint(parts, "centre", summary["center"]);
                AddLength(parts, "radius", summary["radius"]);
                break;
            case "LWPOLYLINE":
                if (summary["closed"] is JsonValue closed && closed.TryGetValue<bool>(out var isClosed) && isClosed)
                {
                    parts.Add("closed");
                }

                if (Number(summary["vertexCount"]) is { } vertices)
                {
                    parts.Add(Format(vertices) + " vertices");
                }

                if (summary["points"] is JsonArray points && points.Count > 0)
                {
                    var shown = points.Take(5).Select(PairText).Where(p => p is not null).ToList();
                    parts.Add(string.Join(" ", shown) + ((points.Count > 5 || summary["pointsTruncated"] is not null) ? " ..." : string.Empty));
                }

                if (summary["hasArcs"] is not null)
                {
                    parts.Add("with arcs");
                }

                AddLength(parts, "width", summary["width"]);
                AddLength(parts, "length", summary["length"]);
                break;
            case "TEXT" or "MTEXT" or "ATTRIB":
                if (Text(summary["text"]) is { Length: > 0 } text)
                {
                    parts.Add("\"" + KnowledgeText.Truncate(text, 60) + "\"");
                }

                AddPoint(parts, "at", summary["position"]);
                AddLength(parts, "height", summary["height"]);
                AddName(parts, "style", summary["style"]);
                if (Number(summary["rotation"]) is { } rotation && Math.Abs(rotation) > 1e-6)
                {
                    parts.Add("rotated " + Format(rotation) + "°");
                }

                break;
            case "DIMENSION":
                AddName(parts, "", summary["kind"]);
                AddLength(parts, "measures", summary["measurement"]);
                AddName(parts, "text", summary["textOverride"]);
                AddName(parts, "style", summary["style"]);
                if (PointText(summary["point1"]) is { } p1 && PointText(summary["point2"]) is { } p2)
                {
                    parts.Add($"from {p1} to {p2}");
                }

                break;
            case "HATCH":
                AddName(parts, "pattern", summary["pattern"]);
                if (Number(summary["patternScale"]) is { } scale)
                {
                    parts.Add("scale " + Format(scale));
                }

                AddArea(parts, summary["area"]);
                break;
            case "INSERT":
                AddName(parts, "block", summary["block"]);
                AddPoint(parts, "at", summary["position"]);
                if (Number(summary["rotation"]) is { } blockRotation && Math.Abs(blockRotation) > 1e-6)
                {
                    parts.Add("rotated " + Format(blockRotation) + "°");
                }

                if (summary["scale"] is JsonArray blockScale && blockScale.Count >= 2 && Number(blockScale[0]) is { } sx && Number(blockScale[1]) is { } sy && (Math.Abs(sx - 1) > 1e-6 || Math.Abs(sy - 1) > 1e-6))
                {
                    parts.Add($"scale {Format(sx)},{Format(sy)}");
                }

                break;
            default:
                AddRange(parts, summary);
                AddLength(parts, "length", summary["length"]);
                AddLength(parts, "radius", summary["radius"]);
                AddArea(parts, summary["area"]);
                break;
        }

        if (parts.Count == 0 && summary["box"] is JsonObject box)
        {
            AddBox(parts, box);
        }

        AddName(parts, "colour", summary["color"]);
        AddName(parts, "linetype", summary["linetype"]);
        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    private static bool IsPromptStart(string text) =>
        text.StartsWith("Specify", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Select", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("Enter", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Pick", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("Object", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Choose", StringComparison.OrdinalIgnoreCase);

    private static void AddRange(List<string> parts, JsonObject summary)
    {
        if (PointText(summary["start"]) is { } start && PointText(summary["end"]) is { } end)
        {
            parts.Add($"from {start} to {end}");
        }
    }

    private static void AddPoint(List<string> parts, string label, JsonNode? node)
    {
        if (PointText(node) is { } point)
        {
            parts.Add(label + " " + point);
        }
    }

    private static void AddLength(List<string> parts, string label, JsonNode? node)
    {
        if (LengthText(node) is { } length)
        {
            parts.Add(label + " " + length);
        }
    }

    private static void AddName(List<string> parts, string label, JsonNode? node)
    {
        if (Text(node) is { Length: > 0 } value)
        {
            parts.Add(label.Length == 0 ? value : label + " " + value);
        }
    }

    private static void AddArea(List<string> parts, JsonNode? node)
    {
        if (node is JsonObject area && Number(area["sqDu"]) is { } sqDu)
        {
            parts.Add("area " + Format(sqDu) + (Number(area["sqFt"]) is { } sqFt ? $" ({Format(sqFt)} sq ft)" : string.Empty));
        }
    }

    private static void AddBox(List<string> parts, JsonObject box)
    {
        var min = PointText(box["min"]);
        var max = PointText(box["max"]);
        if (min is not null && max is not null)
        {
            parts.Add($"box {min} to {max}" + (Text(box["size"]) is { Length: > 0 } size ? $" ({size})" : string.Empty));
        }
    }

    /// <summary>"(x,y)" of a point node ({x, y} or [x, y]).</summary>
    private static string? PointText(JsonNode? node) => node switch
    {
        JsonObject point when Number(point["x"]) is { } x && Number(point["y"]) is { } y => $"({Format(x)},{Format(y)})",
        JsonArray => PairText(node),
        _ => null,
    };

    private static string? PairText(JsonNode? node) =>
        node is JsonArray pair && pair.Count >= 2 && Number(pair[0]) is { } x && Number(pair[1]) is { } y ? $"({Format(x)},{Format(y)})" : PointTextOrNull(node);

    private static string? PointTextOrNull(JsonNode? node) =>
        node is JsonObject point && Number(point["x"]) is { } x && Number(point["y"]) is { } y ? $"({Format(x)},{Format(y)})" : null;

    /// <summary>"120 (10'-0")" of a length node ({du, ftIn}) or a plain number.</summary>
    private static string? LengthText(JsonNode? node) => node switch
    {
        JsonObject length when Number(length["du"]) is { } du => Format(du) + (Text(length["ftIn"]) is { Length: > 0 } ftIn ? $" ({ftIn})" : string.Empty),
        JsonValue => Number(node) is { } plain ? Format(plain) : null,
        _ => null,
    };

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        return value.TryGetValue<int>(out var i) ? i : null;
    }

    private static string? Text(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return KnowledgeText.OneLine(text);
        }

        return null;
    }

    private static string Format(double value) => DrawingUnits.Round(Math.Round(value, 4)).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Text safe inside a Markdown code span (backticks become quotes).</summary>
    internal static string Code(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('`').Append(text.Replace('`', '\'')).Append('`');
        return builder.ToString();
    }
}
