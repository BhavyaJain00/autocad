using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Accuracy;

/// <summary>A layer of the template.</summary>
/// <param name="Name">Name.</param>
/// <param name="Color">AutoCAD colour index (1-255).</param>
/// <param name="Linetype">Linetype (Continuous, CENTER ...).</param>
/// <param name="Lineweight">Lineweight in mm (0.25).</param>
/// <param name="Description">What goes on it.</param>
public sealed record TemplateLayer(string Name, short Color, string Linetype, double Lineweight, string Description);

/// <summary>A text style.</summary>
/// <param name="Name">Name.</param>
/// <param name="Font">TrueType file (arial.ttf).</param>
/// <param name="Height">Fixed height (0 = asked by TEXT).</param>
/// <param name="WidthFactor">Width factor.</param>
public sealed record TemplateTextStyle(string Name, string Font, double Height, double WidthFactor);

/// <summary>A dimension style: system-variable names (DIMSCALE ...) and values; DIMTXSTY by name.</summary>
/// <param name="Name">Name (ARCH-48).</param>
/// <param name="TextStyle">Text style name.</param>
/// <param name="Variables">DIM* variables.</param>
public sealed record TemplateDimStyle(string Name, string TextStyle, IReadOnlyDictionary<string, double> Variables);

/// <summary>A text height for one use at the two plan scales (model-space inches).</summary>
/// <param name="Use">"room names".</param>
/// <param name="Paper">Height on paper.</param>
/// <param name="QuarterInch">At 1/4" = 1'-0" (x48).</param>
/// <param name="EighthInch">At 1/8" = 1'-0" (x96).</param>
public sealed record TemplateTextHeight(string Use, double Paper, double QuarterInch, double EighthInch);

/// <summary>A line of the title block (paper inches, block origin = lower-left corner of the sheet).</summary>
public sealed record TitleBlockLine(double X1, double Y1, double X2, double Y2);

/// <summary>A fixed text of the title block.</summary>
public sealed record TitleBlockText(string Text, double X, double Y, double Height);

/// <summary>An attribute of the title block.</summary>
public sealed record TitleBlockAttribute(string Tag, string Prompt, string Default, double X, double Y, double Height);

/// <summary>The title block block definition (drawn at paper size in inches; insert it at the plot scale, 48 or 96).</summary>
public sealed record TemplateTitleBlock(
    string Name,
    double Width,
    double Height,
    IReadOnlyList<TitleBlockLine> Lines,
    IReadOnlyList<TitleBlockText> Texts,
    IReadOnlyList<TitleBlockAttribute> Attributes);

/// <summary>
/// The standard drawing template (data, applied by apply_drawing_template in one undo step): architectural units in
/// inches, linetypes, the A-* layers, the ARCH text style and heights, dimension styles ARCH-48 / ARCH-96 and an A3 title
/// block. The bundle carries the same data as Contents\Templates\AcadCopilot-Architectural.json (a test keeps them equal).
/// </summary>
public sealed class DrawingTemplate
{
    /// <summary>File name of the template in the bundle's Templates folder.</summary>
    public const string FileName = "AcadCopilot-Architectural.json";

    /// <summary>Template name.</summary>
    public string Name { get; init; } = "AcadCopilot Architectural (feet-inches)";

    /// <summary>Units system variables: LUNITS, LUPREC, INSUNITS, MEASUREMENT, AUNITS, AUPREC, LTSCALE, PSLTSCALE, MSLTSCALE.</summary>
    public IReadOnlyDictionary<string, double> Units { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Linetypes loaded from acad.lin.</summary>
    public IReadOnlyList<string> Linetypes { get; init; } = [];

    /// <summary>Layers.</summary>
    public IReadOnlyList<TemplateLayer> Layers { get; init; } = [];

    /// <summary>Text styles.</summary>
    public IReadOnlyList<TemplateTextStyle> TextStyles { get; init; } = [];

    /// <summary>Text heights by use.</summary>
    public IReadOnlyList<TemplateTextHeight> TextHeights { get; init; } = [];

    /// <summary>Dimension styles.</summary>
    public IReadOnlyList<TemplateDimStyle> DimStyles { get; init; } = [];

    /// <summary>The title block, or null.</summary>
    public TemplateTitleBlock? TitleBlock { get; init; }

    /// <summary>The built-in template.</summary>
    public static DrawingTemplate Default { get; } = CreateDefault();

    /// <summary>The dimension style for a plan scale ("1/4" or "1/8").</summary>
    public static string DimStyleFor(string scale) => scale == "1/8" ? "ARCH-96" : "ARCH-48";

    /// <summary>The scale factor for a plan scale ("1/4" = 48, "1/8" = 96).</summary>
    public static double FactorFor(string scale) => scale == "1/8" ? 96 : 48;

    private static DrawingTemplate CreateDefault()
    {
        static Dictionary<string, double> Dims(double scale) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["DIMSCALE"] = scale,
            ["DIMLUNIT"] = 4,
            ["DIMDEC"] = 1,
            ["DIMFRAC"] = 2,
            ["DIMZIN"] = 3,
            ["DIMTSZ"] = 0.09375,
            ["DIMASZ"] = 0.09375,
            ["DIMTXT"] = 0.09375,
            ["DIMTAD"] = 1,
            ["DIMTIH"] = 0,
            ["DIMTOH"] = 0,
            ["DIMEXO"] = 0.0625,
            ["DIMEXE"] = 0.125,
            ["DIMDLE"] = 0.0625,
            ["DIMGAP"] = 0.0625,
            ["DIMATFIT"] = 3,
            ["DIMTMOVE"] = 0,
            ["DIMJUST"] = 0,
        };

        // A3 landscape (420 x 297 mm) in inches; 20 mm binding margin on the left, 10 mm elsewhere.
        const double w = 16.535, h = 11.693, l = 0.787, m = 0.394;
        const double tx = w - m - 6.5, ty = m, tw = 6.5, th = 2.4;
        var lines = new List<TitleBlockLine>
        {
            new(0, 0, w, 0), new(w, 0, w, h), new(w, h, 0, h), new(0, h, 0, 0),
            new(l, m, w - m, m), new(w - m, m, w - m, h - m), new(w - m, h - m, l, h - m), new(l, h - m, l, m),
            new(tx, ty + th, tx + tw, ty + th), new(tx, ty, tx, ty + th),
            new(tx, ty + 1.8, tx + tw, ty + 1.8), new(tx, ty + 1.1, tx + tw, ty + 1.1), new(tx, ty + 0.55, tx + tw, ty + 0.55),
            new(tx + 3.25, ty, tx + 3.25, ty + 1.1),
        };
        var texts = new List<TitleBlockText>
        {
            new("PROJECT", tx + 0.08, ty + 2.24, 0.07),
            new("DRAWING", tx + 0.08, ty + 1.64, 0.07),
            new("SCALE", tx + 0.08, ty + 0.94, 0.07),
            new("DATE", tx + 3.33, ty + 0.94, 0.07),
            new("DRAWN BY", tx + 0.08, ty + 0.39, 0.07),
            new("SHEET", tx + 3.33, ty + 0.39, 0.07),
        };
        var attributes = new List<TitleBlockAttribute>
        {
            new("PROJECT", "Project name", "RESIDENCE", tx + 0.08, ty + 1.92, 0.16),
            new("TITLE", "Drawing title", "GROUND FLOOR PLAN", tx + 0.08, ty + 1.22, 0.2),
            new("SCALE", "Scale", "1/4\" = 1'-0\"", tx + 0.08, ty + 0.66, 0.12),
            new("DATE", "Date", "-", tx + 3.33, ty + 0.66, 0.12),
            new("DRAWN", "Drawn by", "-", tx + 0.08, ty + 0.11, 0.12),
            new("SHEET", "Sheet number", "A-101", tx + 3.33, ty + 0.11, 0.12),
            new("NOTE", "Note", "ALL DIMENSIONS IN FEET AND INCHES", l + 0.1, m + 0.1, 0.09),
        };

        return new DrawingTemplate
        {
            Units = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["LUNITS"] = 4,
                ["LUPREC"] = 4,
                ["INSUNITS"] = 1,
                ["MEASUREMENT"] = 0,
                ["AUNITS"] = 0,
                ["AUPREC"] = 0,
                ["LTSCALE"] = 24,
                ["PSLTSCALE"] = 1,
                ["MSLTSCALE"] = 1,
            },
            Linetypes = ["CENTER", "HIDDEN", "DASHED", "PHANTOM"],
            Layers =
            [
                new("A-WALL", 7, "Continuous", 0.50, "Walls"),
                new("A-DOOR", 4, "Continuous", 0.25, "Doors: leaves and swings"),
                new("A-GLAZ", 5, "Continuous", 0.25, "Windows and ventilators"),
                new("A-FURN", 6, "Continuous", 0.18, "Furniture and fixtures"),
                new("A-STRS", 4, "Continuous", 0.25, "Stairs"),
                new("A-ANNO-TEXT", 7, "Continuous", 0.25, "Room names, notes, titles"),
                new("A-ANNO-DIMS", 3, "Continuous", 0.18, "Dimensions"),
                new("A-SITE", 2, "Continuous", 0.35, "Plot, setbacks, compound wall, road"),
                new("A-GRID", 1, "CENTER", 0.18, "Centre lines and grids"),
                new("A-HATCH", 8, "Continuous", 0.13, "Hatching"),
                new("A-TTLB", 7, "Continuous", 0.35, "Title block"),
                new("A-ELEV", 7, "Continuous", 0.35, "Elevations and sections"),
            ],
            TextStyles = [new("ARCH", "arial.ttf", 0, 1)],
            TextHeights =
            [
                new("room names", 0.1875, 9, 18),
                new("room sizes and notes", 0.125, 6, 12),
                new("door and window tags", 0.09375, 4.5, 9),
                new("drawing titles", 0.25, 12, 24),
                new("dimension text", 0.09375, 4.5, 9),
            ],
            DimStyles = [new("ARCH-48", "ARCH", Dims(48)), new("ARCH-96", "ARCH", Dims(96))],
            TitleBlock = new TemplateTitleBlock("A-TTLB-A3", w, h, lines, texts, attributes),
        };
    }

    /// <summary>The template as JSON (the Templates\AcadCopilot-Architectural.json format).</summary>
    public JsonObject ToJson()
    {
        static JsonObject Vars(IReadOnlyDictionary<string, double> values)
        {
            var o = new JsonObject();
            foreach (var (k, v) in values)
            {
                o[k] = v;
            }

            return o;
        }

        var json = new JsonObject
        {
            ["name"] = Name,
            ["units"] = Vars(Units),
            ["linetypes"] = new JsonArray([.. Linetypes.Select(l => (JsonNode)l)]),
            ["layers"] = new JsonArray([.. Layers.Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["color"] = l.Color, ["linetype"] = l.Linetype, ["lineweight"] = l.Lineweight, ["description"] = l.Description })]),
            ["textStyles"] = new JsonArray([.. TextStyles.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["font"] = t.Font, ["height"] = t.Height, ["widthFactor"] = t.WidthFactor })]),
            ["textHeights"] = new JsonArray([.. TextHeights.Select(t => (JsonNode)new JsonObject { ["use"] = t.Use, ["paper"] = t.Paper, ["quarterInch"] = t.QuarterInch, ["eighthInch"] = t.EighthInch })]),
            ["dimStyles"] = new JsonArray([.. DimStyles.Select(d => (JsonNode)new JsonObject { ["name"] = d.Name, ["textStyle"] = d.TextStyle, ["variables"] = Vars(d.Variables) })]),
        };
        if (TitleBlock is { } tb)
        {
            json["titleBlock"] = new JsonObject
            {
                ["name"] = tb.Name,
                ["width"] = tb.Width,
                ["height"] = tb.Height,
                ["lines"] = new JsonArray([.. tb.Lines.Select(l => (JsonNode)new JsonArray(l.X1, l.Y1, l.X2, l.Y2))]),
                ["texts"] = new JsonArray([.. tb.Texts.Select(t => (JsonNode)new JsonObject { ["text"] = t.Text, ["x"] = t.X, ["y"] = t.Y, ["height"] = t.Height })]),
                ["attributes"] = new JsonArray([.. tb.Attributes.Select(a => (JsonNode)new JsonObject { ["tag"] = a.Tag, ["prompt"] = a.Prompt, ["default"] = a.Default, ["x"] = a.X, ["y"] = a.Y, ["height"] = a.Height })]),
            };
        }

        return json;
    }

    /// <summary>The JSON text of the template (indented, as stored in the bundle).</summary>
    public string ToJsonText() => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    /// <summary>Reads a template JSON; throws <see cref="FormatException"/> with the problem.</summary>
    public static DrawingTemplate FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            static Dictionary<string, double> Vars(JsonNode? node) =>
                node is JsonObject o ? o.ToDictionary(p => p.Key, p => p.Value!.GetValue<double>(), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            static double D(JsonNode? n) => n!.GetValue<double>();
            static string S(JsonNode? n) => n!.GetValue<string>();

            TemplateTitleBlock? titleBlock = null;
            if (json["titleBlock"] is JsonObject tb)
            {
                titleBlock = new TemplateTitleBlock(
                    S(tb["name"]),
                    D(tb["width"]),
                    D(tb["height"]),
                    [.. tb["lines"]!.AsArray().Select(l => new TitleBlockLine(D(l![0]), D(l[1]), D(l[2]), D(l[3])))],
                    [.. (tb["texts"]?.AsArray() ?? []).Select(t => new TitleBlockText(S(t!["text"]), D(t["x"]), D(t["y"]), D(t["height"])))],
                    [.. (tb["attributes"]?.AsArray() ?? []).Select(a => new TitleBlockAttribute(S(a!["tag"]), S(a["prompt"]), S(a["default"]), D(a["x"]), D(a["y"]), D(a["height"])))]);
            }

            var template = new DrawingTemplate
            {
                Name = json["name"]?.GetValue<string>() ?? "Template",
                Units = Vars(json["units"]),
                Linetypes = [.. (json["linetypes"]?.AsArray() ?? []).Select(S)],
                Layers = [.. (json["layers"]?.AsArray() ?? []).Select(l => new TemplateLayer(S(l!["name"]), (short)l["color"]!.GetValue<int>(), l["linetype"]?.GetValue<string>() ?? "Continuous", D(l["lineweight"]), l["description"]?.GetValue<string>() ?? string.Empty))],
                TextStyles = [.. (json["textStyles"]?.AsArray() ?? []).Select(t => new TemplateTextStyle(S(t!["name"]), S(t["font"]), D(t["height"]), t["widthFactor"] is { } wf ? D(wf) : 1))],
                TextHeights = [.. (json["textHeights"]?.AsArray() ?? []).Select(t => new TemplateTextHeight(S(t!["use"]), D(t["paper"]), D(t["quarterInch"]), D(t["eighthInch"])))],
                DimStyles = [.. (json["dimStyles"]?.AsArray() ?? []).Select(d => new TemplateDimStyle(S(d!["name"]), d["textStyle"]?.GetValue<string>() ?? "Standard", Vars(d["variables"])))],
                TitleBlock = titleBlock,
            };
            var problems = template.Problems();
            return problems.Count == 0 ? template : throw new FormatException(string.Join(" ", problems));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException or KeyNotFoundException or FormatException)
        {
            throw new FormatException("The template JSON is not valid: " + ex.Message, ex);
        }
    }

    /// <summary>Problems with the data (empty when fine).</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var layer in Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Name) || layer.Color is < 1 or > 255)
            {
                problems.Add($"Layer '{layer.Name}': a name and a colour 1-255 are needed.");
            }

            if (!ValidLineweights.Contains((int)Math.Round(layer.Lineweight * 100)))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"Layer '{layer.Name}': lineweight {layer.Lineweight} mm is not an AutoCAD lineweight."));
            }
        }

        foreach (var dim in DimStyles)
        {
            foreach (var key in dim.Variables.Keys.Where(k => !KnownDimVariables.Contains(k)))
            {
                problems.Add($"Dimension style '{dim.Name}': {key} is not supported (use {string.Join(", ", KnownDimVariables)}).");
            }
        }

        return problems;
    }

    /// <summary>AutoCAD lineweights in 1/100 mm.</summary>
    public static IReadOnlySet<int> ValidLineweights { get; } = new HashSet<int> { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };

    /// <summary>DIM variables the plug-in applies.</summary>
    public static IReadOnlySet<string> KnownDimVariables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DIMSCALE", "DIMLUNIT", "DIMDEC", "DIMFRAC", "DIMZIN", "DIMTSZ", "DIMASZ", "DIMTXT", "DIMTAD", "DIMTIH", "DIMTOH",
        "DIMEXO", "DIMEXE", "DIMDLE", "DIMGAP", "DIMATFIT", "DIMTMOVE", "DIMJUST", "DIMADEC", "DIMRND", "DIMTOFL", "DIMSAH",
    };
}
