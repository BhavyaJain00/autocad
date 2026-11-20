using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Accuracy;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Presentation;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Core.Sheets;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Sheets;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Tools;

/// <summary>
/// Drawing-set tools: arrange_drawings (line up model space, titles and scales), create_sheets (layouts with title
/// block and viewports), plot_to_pdf (one multi-page PDF; the user approves) and plan_full_set (the full set's to-do
/// list from the ground floor plan). Definitions and the pure maths are in AcadCopilot.Core/Sheets, the AutoCAD work in
/// Plugin/Sheets. Everything touching AutoCAD runs through <see cref="ToolContext"/> on the main thread.
/// </summary>
public sealed partial class ToolRegistry
{
    /// <summary>Adds the drawing-set tools (called from CreateDefault).</summary>
    static partial void AddSheetsTools(List<ToolSpec> tools);

    static partial void AddSheetsTools(List<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Add(new ToolSpec(SheetToolDefinitions.PlanFullSetDefinition(), PlanFullSetAsync));
        tools.Add(new ToolSpec(SheetToolDefinitions.ArrangeDrawingsDefinition(), ArrangeDrawingsAsync));
        tools.Add(new ToolSpec(SheetToolDefinitions.CreateSheetsDefinition(), CreateSheetsAsync));
        tools.Add(new ToolSpec(SheetToolDefinitions.PlotToPdfDefinition(), PlotToPdfAsync));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // arrange_drawings
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> ArrangeDrawingsAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var request = new ArrangeRequest(
            Labels(args),
            args.GetDoubleOrNull("gap"),
            args.GetDoubleOrNull("cluster_gap"),
            args.GetBool("add_titles", fallback: true),
            ScaleArgument(args));
        var preview = args.GetBool("preview_only");
        var outcome = preview
            ? await context.ReadAsync((document, transaction) => DrawingArranger.Run(document.Database, transaction, request, apply: false), cancellationToken).ConfigureAwait(false)
            : await context.WriteAsync((document, transaction) => DrawingArranger.Run(document.Database, transaction, request, apply: true), cancellationToken).ConfigureAwait(false);

        var found = outcome.Found;
        if (found.Clusters.Count == 0)
        {
            return ToolResult.Fail("Model space is empty: there are no drawings to arrange.");
        }

        var units = found.Units;
        var titles = outcome.Titles.ToDictionary(t => t.Number);
        var drawings = new JsonArray();
        foreach (var drawing in outcome.Plan.Drawings)
        {
            var item = new JsonObject
            {
                ["number"] = drawing.Number,
                ["title"] = drawing.Name,
                ["kind"] = drawing.Kind.ToString(),
                ["row"] = drawing.Row,
                ["from"] = RectJson(drawing.From),
                ["to"] = RectJson(drawing.To),
                ["move"] = drawing.Moves ? new JsonObject { ["dx"] = units.Length(drawing.Dx), ["dy"] = units.Length(drawing.Dy) } : "none",
                ["scale"] = outcome.Scales.TryGetValue(drawing.Number, out var scale) ? scale.Text : null,
            };
            if (titles.TryGetValue(drawing.Number, out var title))
            {
                item["titles"] = title.What;
            }
            else if (drawing.Kind == DrawingKind.Other && found.Clusters.First(c => c.Number == drawing.Number).Title is null)
            {
                item["titles"] = "untitled: give a label to add its title";
            }

            drawings.Add(item);
        }

        var moving = outcome.Plan.Drawings.Count(d => d.Moves);
        var data = new JsonObject
        {
            ["drawings"] = drawings,
            ["gap"] = units.Length(outcome.Plan.Gap),
            ["clusterGap"] = units.Length(found.ClusterGap),
            ["sketch"] = outcome.Plan.Sketch,
            ["applied"] = !preview,
        };
        if (outcome.Warnings.Count > 0)
        {
            data["warnings"] = new JsonArray([.. outcome.Warnings.Select(w => (JsonNode)w)]);
        }

        var names = string.Join(", ", outcome.Plan.Drawings.Select(d => d.Number.ToString(CultureInfo.InvariantCulture) + " " + d.Name));
        var message = preview
            ? $"Preview (nothing changed): {found.Clusters.Count} drawings ({names}); {moving} would move, {outcome.Titles.Count} title changes. Call again without preview_only to apply."
            : $"{found.Clusters.Count} drawings ({names}): {moving} moved as one undo step, {outcome.Titles.Count} title/scale changes. Check with get_drawing_info / capture_view (ZOOM E).";
        if (outcome.Warnings.Count > 0)
        {
            message += " Warnings: " + string.Join(" ", outcome.Warnings);
        }

        context.Log(message);
        return ToolResult.Ok(message, data) with
        {
            CreatedIds = [.. outcome.Created.Select(ToolContext.HandleValueOf)],
            ModifiedIds = [.. outcome.Moved.Select(ToolContext.HandleValueOf)],
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // create_sheets
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> CreateSheetsAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var standard = (args.GetStringOrNull("standard") ?? "iso") == "arch" ? PaperStandard.Arch : PaperStandard.Iso;
        var paper = PaperSizes.For(args.GetStringOrNull("paper") ?? "A3", standard);
        var drawingName = await context.InvokeAsync(() => Path.GetFileNameWithoutExtension(context.Document.Name), cancellationToken).ConfigureAwait(false);
        var values = new TitleBlockValues
        {
            Project = (args.GetStringOrNull("project") ?? drawingName ?? "PROJECT").ToUpperInvariant(),
            Client = args.GetStringOrNull("client") ?? "-",
            Date = TitleBlockValues.DateText(DateTimeOffset.Now),
            DrawnBy = TitleBlockValues.DrawnByFor(Environment.UserName),
        };
        var numbers = args.Has("drawings") ? args.GetLongList("drawings").Select(n => (int)n).ToList() : null;
        var request = new SheetsRequest(
            paper,
            values,
            numbers,
            Labels(args),
            args.GetBool("combine", fallback: true),
            ScaleArgument(args),
            args.GetDoubleOrNull("cluster_gap"),
            args.GetBool("replace_existing"));
        var outcome = await context.WriteAsync((document, transaction) => SheetBuilder.Build(document.Database, transaction, request), cancellationToken).ConfigureAwait(false);

        var sheets = new JsonArray([.. outcome.Sheets.Select(s => (JsonNode)new JsonObject
        {
            ["number"] = s.Spec.Number,
            ["layout"] = s.LayoutName,
            ["title"] = s.Spec.Title,
            ["scale"] = s.Spec.ScaleText,
            ["media"] = s.Media,
            ["replaced"] = s.Replaced,
            ["viewports"] = new JsonArray([.. s.Spec.Viewports.Select(v => (JsonNode)new JsonObject
            {
                ["drawing"] = v.DrawingNumber,
                ["title"] = v.Title,
                ["scale"] = v.Scale.Text,
                ["paperMm"] = RectJson(v.Paper),
                ["model"] = RectJson(v.Model),
                ["cropped"] = v.Cropped,
            })]),
        })]);
        var data = new JsonObject
        {
            ["paper"] = paper.Name,
            ["device"] = PaperSizes.PdfDevice,
            ["titleBlock"] = new JsonObject
            {
                ["block"] = outcome.BlockName,
                ["created"] = outcome.BlockCreated,
                ["layer"] = TitleBlockDesign.Layer,
                ["attributes"] = new JsonObject([.. values.ByTag.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))]),
                ["note"] = TitleBlockDesign.Note,
            },
            ["sheets"] = sheets,
        };
        if (outcome.Skipped.Count > 0)
        {
            data["skipped"] = new JsonArray([.. outcome.Skipped.Select(s => (JsonNode)s)]);
        }

        if (outcome.Warnings.Count > 0)
        {
            data["warnings"] = new JsonArray([.. outcome.Warnings.Select(w => (JsonNode)w)]);
        }

        if (args.GetBool("show_sheet") && outcome.Sheets.Count > 0)
        {
            var first = outcome.Sheets[0].LayoutName;
            await context.InvokeAsync(
                () =>
                {
                    using var documentLock = Acad.AcadDispatcher.LockDocument(context.Document);
                    LayoutManager.Current.CurrentLayout = first;
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            data["shown"] = first;
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Created {outcome.Sheets.Count} sheets on {paper.Name}: {string.Join(", ", outcome.Sheets.Select(s => s.LayoutName))}. Title block {outcome.BlockName}{(outcome.BlockCreated ? " (new)" : string.Empty)}, viewports locked at their scales.") +
            (outcome.Skipped.Count > 0 ? " Left out: " + string.Join("; ", outcome.Skipped) + "." : string.Empty) +
            (outcome.Warnings.Count > 0 ? " Warnings: " + string.Join(" ", outcome.Warnings) : string.Empty) +
            " Next: plot_to_pdf when the user wants the PDF.";
        context.Log(message);
        return ToolResult.Ok(message, data) with
        {
            CreatedIds = [.. outcome.Created.Select(ToolContext.HandleValueOf)],
            ModifiedIds = [.. outcome.Modified.Select(ToolContext.HandleValueOf)],
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // plot_to_pdf
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> PlotToPdfAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var requested = args.Has("sheets") ? args.GetStringList("sheets") : null;
        var name = args.GetStringOrNull("name");
        var drawingName = await context.InvokeAsync(() => Path.GetFileName(context.Document.Name), cancellationToken).ConfigureAwait(false);
        string path;
        try
        {
            var folder = Directory.CreateDirectory(ExportFiles.Folder).FullName;
            path = SheetNames.PdfPath(folder, name, drawingName, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolException("Could not prepare the export folder " + ExportFiles.Folder + ": " + ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var outcome = await context.InvokeAsync(() => PdfPlotter.Plot(context.Document, requested, path), cancellationToken).ConfigureAwait(false);
        var file = new FileInfo(outcome.Path);
        if (!file.Exists)
        {
            return ToolResult.Fail($"AutoCAD finished the plot of {outcome.Sheets.Count} sheets but no PDF appeared at {outcome.Path}. Check the plot settings of the sheets (PAGESETUP) and try again.");
        }

        var data = new JsonObject
        {
            ["file"] = outcome.Path,
            ["folder"] = file.DirectoryName,
            ["pages"] = outcome.Sheets.Count,
            ["sheets"] = new JsonArray([.. outcome.Sheets.Select(s => (JsonNode)s)]),
            ["bytes"] = file.Length,
            ["device"] = PaperSizes.PdfDevice,
        };
        if (outcome.Notes.Count > 0)
        {
            data["notes"] = new JsonArray([.. outcome.Notes.Select(n => (JsonNode)n)]);
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"Saved a {outcome.Sheets.Count}-page PDF: {outcome.Path}. Tell the user where it is.");
        context.Log(message);
        return ToolResult.Ok(message, data);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // plan_full_set
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> PlanFullSetAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var point = args.Has("plan_point") ? Numbers(args, "plan_point", 2) : null;
        var read = await context.ReadAsync(
            (document, transaction) =>
            {
                var found = DrawingFinder.Find(document.Database, transaction, null, []);
                var plan = PickGroundPlan(found.Clusters, point);
                var drawn = plan is null ? null : PlanReading.FromDrawing(found.Scan.Items, found.Scan.Layers, plan, found.InchesPerUnit);
                var key = CheckpointStore.DrawingKey(document.IsNamedDrawing
                    ? document.Name
                    : $"unsaved-{ProcessIdentity.Current.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Path.GetFileName(document.Name)}");
                return (Found: found, Plan: plan, Drawn: drawn, Key: key);
            },
            cancellationToken).ConfigureAwait(false);

        var ipu = read.Found.InchesPerUnit;
        var validated = ValidatedPlan(read.Key, ipu, context);
        var notes = new List<string>();
        FullSetInput? input = read.Drawn;
        if (validated is not null)
        {
            if (read.Drawn is null || Overlaps(validated.Outline, read.Drawn.Outline))
            {
                // The validated plan's numbers, with what only the drawing knows (its extents with dimensions).
                input = validated with { PlanBox = read.Drawn?.PlanBox, Openings = validated.Openings.Count > 0 ? validated.Openings : read.Drawn?.Openings ?? [] };
            }
            else
            {
                notes.Add("A validated plan exists but its outline is not where the ground floor is drawn; the drawing was used.");
            }
        }

        if (args.Has("outline"))
        {
            var o = Numbers(args, "outline", 4);
            var outline = SheetRect.FromCorners(o[0], o[1], o[2], o[3]);
            input = (input ?? new FullSetInput { Outline = outline, InchesPerUnit = ipu, Source = "arguments" }) with { Outline = outline };
        }

        if (input is null)
        {
            throw new ToolException(read.Found.Clusters.Count == 0
                ? "Model space is empty: draw (or open) the ground floor plan first."
                : "No ground floor plan found: give plan_point (a point inside it) or outline [x0, y0, x1, y1] of its outer walls.");
        }

        input = input with
        {
            InchesPerUnit = ipu,
            Stair = args.Has("stair") ? Rect(args, "stair") : input.Stair,
            Plot = args.Has("plot") ? Rect(args, "plot") : input.Plot,
            WetAreas = args.Has("wet_areas")
                ? [.. args.GetObjectList("wet_areas").Select(w => new NamedRect(w.GetString("name").ToUpperInvariant(), Rect(w, "box")))]
                : input.WetAreas,
            UpperFloors = (int)args.GetLong("upper_floors", input.UpperFloors),
            Terrace = args.GetBool("terrace", fallback: input.Terrace),
            FrontSide = args.GetStringOrNull("front") ?? input.FrontSide,
            FloorHeightInches = args.GetDouble("floor_height", input.FloorHeightInches),
            PlinthInches = args.GetDouble("plinth", input.PlinthInches),
            SillInches = args.GetDouble("sill", input.SillInches),
            LintelInches = args.GetDouble("lintel", input.LintelInches),
            ParapetInches = args.GetDouble("parapet", input.ParapetInches),
        };

        var plan = FullSetPlanner.Plan(input);
        var data = FullSetPlanner.ToJson(plan, ipu);
        data["source"] = input.Source;
        data["groundFloor"] = new JsonObject
        {
            ["drawing"] = read.Plan is { } p ? p.Number.ToString(CultureInfo.InvariantCulture) + " " + p.Name : null,
            ["outline"] = RectJson(input.Outline),
            ["stair"] = input.Stair is { } s ? RectJson(s) : null,
            ["plot"] = input.Plot is { } pl ? RectJson(pl) : null,
            ["wetAreas"] = new JsonArray([.. input.WetAreas.Select(w => (JsonNode)new JsonObject { ["name"] = w.Name, ["box"] = RectJson(w.Box) })]),
        };
        if (notes.Count > 0)
        {
            data["notes"] = new JsonArray([.. notes.Select(n => (JsonNode)n)]);
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Planned {plan.Stages.Count} stages from the ground floor plan ({input.Source}): {string.Join(", ", plan.Stages.Select(s => s.Title))}. ") +
            "Put them in update_plan, state the assumptions in one line, then draft stage by stage with run_commands (recall_knowledge with each stage's guide first); ask the user before each new drawing as usual.";
        return ToolResult.Ok(message, data);
    }

    private static DrawingCluster? PickGroundPlan(IReadOnlyList<DrawingCluster> clusters, double[]? point)
    {
        if (point is not null)
        {
            var target = new SheetRect(point[0], point[1], point[0], point[1]);
            return clusters.OrderBy(c => c.Box.DistanceTo(target)).FirstOrDefault();
        }

        return clusters.Where(c => c.Kind == DrawingKind.FloorPlan).OrderBy(c => Math.Abs(c.Order)).ThenBy(c => c.Number).FirstOrDefault()
            ?? clusters.OrderByDescending(c => c.Anchor.Width * c.Anchor.Height).FirstOrDefault();
    }

    // The plan validate_plan saved for this drawing (accuracy pack), as plan facts; null when there is none or it cannot be read.
    private static FullSetInput? ValidatedPlan(string key, double inchesPerUnit, ToolContext context)
    {
        try
        {
            var store = PlanStore.CreateDefault();
            var stored = store.Load(key) ?? store.LatestOfProcess(ProcessIdentity.Current.ProcessId);
            if (stored is null || PlanParser.Parse(stored.Plan).Plan is not { } plan)
            {
                return null;
            }

            return PlanReading.FromHousePlan(plan, inchesPerUnit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException)
        {
            context.Warn("plan_full_set: the validated plan could not be read; using the drawing.", ex);
            return null;
        }
    }

    private static bool Overlaps(SheetRect a, SheetRect b) => a.Overlaps(b);

    // ---------------------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static List<DrawingLabel> Labels(ToolArguments args)
    {
        if (!args.Has("labels"))
        {
            return [];
        }

        var labels = new List<DrawingLabel>();
        foreach (var label in args.GetObjectList("labels"))
        {
            var p = Numbers(label, "point", 2);
            labels.Add(new DrawingLabel(p[0], p[1], label.GetString("title")));
        }

        return labels;
    }

    private static DrawingScale? ScaleArgument(ToolArguments args)
    {
        var text = args.GetStringOrNull("scale");
        if (text is null)
        {
            return null;
        }

        return DrawingScales.Parse(text) ?? throw new ToolException($"'{text}' is not a standard scale; use one of {string.Join(", ", SheetToolDefinitions.ScaleNames)}.");
    }

    private static SheetRect Rect(ToolArguments args, string name)
    {
        var v = Numbers(args, name, 4);
        var rect = SheetRect.FromCorners(v[0], v[1], v[2], v[3]);
        return rect.Width > 0 && rect.Height > 0 ? rect : throw new ToolException($"{name} needs a width and a height: [x0, y0, x1, y1].");
    }

    private static JsonArray RectJson(SheetRect rect) =>
        new(DrawingUnits.Round(rect.MinX), DrawingUnits.Round(rect.MinY), DrawingUnits.Round(rect.MaxX), DrawingUnits.Round(rect.MaxY));
}
