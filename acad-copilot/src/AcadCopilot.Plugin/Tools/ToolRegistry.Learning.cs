using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Inspect;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Tools;

/// <summary>
/// Learning tools of the plug-in: learn_style_from_drawing (reads the active drawing on AutoCAD's main thread, analyses
/// it with <see cref="StyleAnalyzer"/> and merges the result into style.md). The other learning tools (remember_rule,
/// save_recipe, save_lesson, recall_knowledge, forget) only touch the knowledge files and are answered by the MCP bridge
/// itself (<see cref="KnowledgeTools.Execute"/>), so they never wait for AutoCAD.
/// </summary>
public sealed partial class ToolRegistry
{
    /// <summary>Most model-space objects read when learning a style (larger drawings are sampled up to here).</summary>
    private const int MaxStyleEntities = 60_000;

    /// <summary>Most straight segments collected for the wall-thickness analysis.</summary>
    private const int MaxStyleSegments = 20_000;

    static partial void AddLearningTools(List<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Add(new ToolSpec(KnowledgeTools.LearnStyleFromDrawingDefinition(), LearnStyleFromDrawingAsync));
    }

    private static async Task<ToolResult> LearnStyleFromDrawingAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var sample = await context.ReadAsync((document, transaction) => ReadStyleSample(document, transaction), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = StyleAnalyzer.Analyze(sample);
        if (profile.Sections.Count == 0)
        {
            return ToolResult.Fail("This drawing has nothing to learn a style from (no layers in use, text, dimensions, lines or blocks in model space). Open a finished drawing of the user and try again.");
        }

        KnowledgeWrite write;
        try
        {
            write = new KnowledgeStore(AppPaths.Knowledge).MergeStyle(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolException("Could not write style.md in " + AppPaths.Knowledge + ": " + ex.Message);
        }

        var sections = new JsonObject();
        foreach (var (title, lines) in profile.Sections)
        {
            sections[title] = new JsonArray([.. lines.Select(l => (JsonNode)l)]);
        }

        context.Log($"Style learned from {sample.DrawingName}: {string.Join(", ", profile.Sections.Select(s => s.Title))}.");
        return ToolResult.Ok(
            $"Learned the style of {sample.DrawingName}: {write.Message} Tell the user what you learned in a few words; they can edit {write.File}.",
            new JsonObject { ["file"] = write.File, ["learned"] = sections });
    }

    /// <summary>
    /// Reads the style sample of <paramref name="document"/>: model space, or only the objects in <paramref name="only"/>
    /// (the user's new objects of a "Learn from me" recording). Main thread, inside an open transaction.
    /// </summary>
    internal static StyleSample ReadStyleSample(Document document, Transaction transaction, IEnumerable<ObjectId>? only = null, string? sourceName = null)
    {
        var database = document.Database;
        var units = ToolContext.GetUnits(database);
        var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var texts = new List<TextSample>();
        var dimUses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var segments = new List<SegmentSample>();
        var widths = new List<(double, string)>();
        var arcs = new List<ArcSample>();
        var blocks = new Dictionary<(string Name, string Layer), int>();
        var hatches = new Dictionary<(string Pattern, double Scale, string Layer), int>();

        IEnumerable<ObjectId> ids = only ?? ((BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead)).Cast<ObjectId>();
        var read = 0;
        foreach (var id in ids)
        {
            if (++read > MaxStyleEntities)
            {
                break;
            }

            if (id.IsErased || transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            var layer = entity.Layer;
            layerCounts[layer] = layerCounts.TryGetValue(layer, out var n) ? n + 1 : 1;
            try
            {
                switch (entity)
                {
                    case Line line when segments.Count < MaxStyleSegments:
                        segments.Add(new SegmentSample(line.StartPoint.X, line.StartPoint.Y, line.EndPoint.X, line.EndPoint.Y, layer));
                        break;
                    case Polyline polyline:
                        AddPolyline(polyline, layer, segments, widths);
                        break;
                    case Arc arc:
                        arcs.Add(new ArcSample(arc.Radius, arc.TotalAngle * 180 / Math.PI, layer));
                        break;
                    case DBText text:
                        texts.Add(new TextSample(text.TextStyleName, text.Height, layer));
                        break;
                    case MText mtext:
                        texts.Add(new TextSample(mtext.TextStyleName, mtext.TextHeight, layer));
                        break;
                    case Dimension dimension:
                        var style = dimension.DimensionStyleName;
                        dimUses[style] = dimUses.TryGetValue(style, out var d) ? d + 1 : 1;
                        break;
                    case BlockReference reference:
                        var name = BlockName(reference, transaction);
                        if (name is not null)
                        {
                            blocks[(name, layer)] = blocks.TryGetValue((name, layer), out var b) ? b + 1 : 1;
                        }

                        break;
                    case Hatch hatch:
                        var key = (hatch.PatternName, Math.Round(hatch.PatternScale, 4), layer);
                        hatches[key] = hatches.TryGetValue(key, out var h) ? h + 1 : 1;
                        break;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Objects that cannot report a property are skipped; the style comes from the rest.
            }
        }

        return new StyleSample
        {
            DrawingName = sourceName ?? Path.GetFileName(document.Name),
            InchesPerUnit = units.InchesPerUnit ?? 0,
            UnitsSummary = units.Describe(),
            Layers = ReadLayers(database, transaction, layerCounts),
            Texts = texts,
            TextStyles = ReadTextStyles(database, transaction),
            DimStyles = ReadDimStyles(database, transaction, dimUses),
            CurrentDimStyle = ((DimStyleTableRecord)transaction.GetObject(database.Dimstyle, OpenMode.ForRead)).Name,
            Segments = segments,
            PolylineWidths = widths,
            Arcs = arcs,
            Blocks = [.. blocks.Select(p => new BlockSample(p.Key.Name, p.Key.Layer, p.Value))],
            Hatches = [.. hatches.Select(p => new HatchSample(p.Key.Pattern, p.Key.Scale, p.Key.Layer, p.Value))],
            Ltscale = ToolContext.Round(database.Ltscale),
        };
    }

    private static void AddPolyline(Polyline polyline, string layer, List<SegmentSample> segments, List<(double, string)> widths)
    {
        if (polyline.HasWidth)
        {
            try
            {
                widths.Add((polyline.ConstantWidth, layer));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Varying widths (arrows, tapers): no constant width to learn.
                widths.Add((polyline.GetStartWidthAt(0), layer));
            }
        }

        var count = polyline.NumberOfVertices;
        var last = polyline.Closed ? count : count - 1;
        for (var i = 0; i < last && segments.Count < MaxStyleSegments; i++)
        {
            if (polyline.GetSegmentType(i) != SegmentType.Line)
            {
                continue;
            }

            var a = polyline.GetPoint2dAt(i);
            var b = polyline.GetPoint2dAt((i + 1) % count);
            segments.Add(new SegmentSample(a.X, a.Y, b.X, b.Y, layer));
        }
    }

    private static string? BlockName(BlockReference reference, Transaction transaction)
    {
        var definitionId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
        if (transaction.GetObject(definitionId, OpenMode.ForRead) is not BlockTableRecord definition ||
            definition.IsLayout || definition.IsFromExternalReference || definition.IsAnonymous)
        {
            return null;
        }

        return definition.Name;
    }

    private static List<LayerSample> ReadLayers(Database database, Transaction transaction, Dictionary<string, int> counts)
    {
        var layers = new List<LayerSample>();
        var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            var linetype = transaction.GetObject(layer.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord lt ? lt.Name : "Continuous";
            var weight = (int)layer.LineWeight;
            layers.Add(new LayerSample(
                layer.Name,
                EntitySummary.ColorText(layer.Color),
                linetype,
                weight >= 0 ? weight / 100.0 : null,
                counts.TryGetValue(layer.Name, out var n) ? n : 0));
        }

        return layers;
    }

    private static List<TextStyleSample> ReadTextStyles(Database database, Transaction transaction)
    {
        var styles = new List<TextStyleSample>();
        var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var style = (TextStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (!string.IsNullOrEmpty(style.Name))
            {
                styles.Add(new TextStyleSample(style.Name, style.FileName, style.TextSize));
            }
        }

        return styles;
    }

    private static List<DimStyleSample> ReadDimStyles(Database database, Transaction transaction, Dictionary<string, int> uses)
    {
        var styles = new List<DimStyleSample>();
        var table = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var style = (DimStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            var used = uses.TryGetValue(style.Name, out var n) ? n : 0;
            if (used == 0 && id != database.Dimstyle)
            {
                continue;
            }

            string Number(double value) => ToolContext.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string Name(ObjectId recordId, string none) =>
                recordId.IsNull ? none : (transaction.GetObject(recordId, OpenMode.ForRead) as SymbolTableRecord)?.Name ?? none;
            var variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DIMSCALE"] = Number(style.Dimscale),
                ["DIMTXT"] = Number(style.Dimtxt),
                ["DIMTXSTY"] = Name(style.Dimtxsty, "Standard"),
                ["DIMTSZ"] = Number(style.Dimtsz),
                ["DIMASZ"] = Number(style.Dimasz),
                ["DIMBLK"] = Name(style.Dimblk, "closed filled"),
                ["DIMLUNIT"] = style.Dimlunit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DIMDEC"] = style.Dimdec.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DIMZIN"] = style.Dimzin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DIMTAD"] = style.Dimtad.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DIMTIH"] = style.Dimtih ? "1" : "0",
                ["DIMEXO"] = Number(style.Dimexo),
                ["DIMEXE"] = Number(style.Dimexe),
                ["DIMDLE"] = Number(style.Dimdle),
                ["DIMGAP"] = Number(style.Dimgap),
            };
            styles.Add(new DimStyleSample(style.Name, used, variables));
        }

        return styles;
    }
}
