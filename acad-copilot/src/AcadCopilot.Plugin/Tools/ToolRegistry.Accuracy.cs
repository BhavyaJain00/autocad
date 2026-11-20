using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core;
using AcadCopilot.Core.Accuracy;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Accuracy;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.ApplicationServices;

namespace AcadCopilot.Plugin.Tools;

/// <summary>
/// Accuracy tools: validate_plan (pure checks of a plan in numbers, saved per drawing), check_drawing (the drawn geometry
/// against that plan) and apply_drawing_template (units, layers, styles, title block in one undo step). Definitions and
/// all checks live in AcadCopilot.Core.Accuracy; this file only reads or changes the drawing on AutoCAD's main thread.
/// </summary>
public sealed partial class ToolRegistry
{
    /// <summary>Adds the accuracy tools (called from CreateDefault).</summary>
    static partial void AddAccuracyTools(List<ToolSpec> tools);

    static partial void AddAccuracyTools(List<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Add(new ToolSpec(AccuracyToolDefinitions.ValidatePlanDefinition(), ValidatePlanAsync));
        tools.Add(new ToolSpec(AccuracyToolDefinitions.CheckDrawingDefinition(), CheckDrawingAsync));
        tools.Add(new ToolSpec(AccuracyToolDefinitions.ApplyDrawingTemplateDefinition(), ApplyDrawingTemplateAsync));
    }

    /// <summary>The key of a drawing's plan file (as for checkpoints: unsaved drawings are keyed by process and name).</summary>
    private static string PlanDrawingKey(Document document) =>
        CheckpointStore.DrawingKey(document.IsNamedDrawing
            ? document.Name
            : $"unsaved-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Path.GetFileName(document.Name)}");

    // ---------------------------------------------------------------------------------------------------------------
    // validate_plan
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> ValidatePlanAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var (validation, result, rules) = AccuracyTools.ValidatePlan(arguments, AccuracyTools.RuleLines(AppPaths.Knowledge));
        if (!result.Success || !validation.IsReady || !new ToolArguments(arguments).GetBool("save", fallback: true))
        {
            return result;
        }

        var (key, title) = await context.ReadAsync((document, _) => (PlanDrawingKey(document), Path.GetFileName(document.Name)), cancellationToken).ConfigureAwait(false);
        string file;
        try
        {
            file = PlanStore.CreateDefault().Save(key, title, (JsonObject)arguments["plan"]!, Environment.ProcessId, rules.Vastu, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Warn("The plan could not be saved.", ex);
            return result with { Message = result.Message + " (The plan could not be saved for check_drawing: " + ex.Message + ")" };
        }

        context.Log($"Plan '{validation.Plan!.Name}' saved for {title}: {file}");
        var data = result.Data is JsonObject d ? (JsonObject)d.DeepClone() : new JsonObject();
        data["savedFor"] = title;
        return result with { Message = result.Message + $" Saved for check_drawing on {title}.", Data = data };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // check_drawing
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> CheckDrawingAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var (key, title) = await context.ReadAsync((document, _) => (PlanDrawingKey(document), Path.GetFileName(document.Name)), cancellationToken).ConfigureAwait(false);
        var store = PlanStore.CreateDefault();
        var stored = store.Load(key);
        var note = string.Empty;
        if (stored is null)
        {
            stored = store.LatestOfProcess(Environment.ProcessId)
                ?? throw new ToolException($"No validated plan is saved for {title}. Call validate_plan with the plan (it must have no errors) first; check_drawing compares the drawing with it.");
            note = $" (Using the plan last validated in this AutoCAD session, for {stored.Drawing}.)";
        }

        var (plan, floor, origin) = AccuracyTools.ResolveFloor(stored.Plan, args.GetStringOrNull("floor"), args.GetStringOrNull("origin"));
        var layers = new CheckLayers
        {
            Wall = args.GetStringOrNull("wall_layer") ?? "A-WALL",
            Door = args.GetStringOrNull("door_layer") ?? "A-DOOR",
            Glazing = args.GetStringOrNull("glazing_layer") ?? "A-GLAZ",
            Text = args.GetStringOrNull("text_layer") ?? "A-ANNO-TEXT",
            Dims = args.GetStringOrNull("dims_layer") ?? "A-ANNO-DIMS",
        };
        var stage = args.GetStringOrNull("stage") ?? "all";
        var max = (int)args.GetLong("max_findings", AccuracyTools.DefaultMaxFindings);
        var region = DrawingChecker.Region(floor, origin);
        var (snapshot, unitsKnown) = await context.ReadAsync((document, transaction) => AccuracyDrawingReader.Read(document, transaction, region), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var report = DrawingChecker.Check(plan, floor, origin, snapshot, layers, stage);
        var result = AccuracyTools.CheckResult(report, floor, origin, stored, max);
        if (!unitsKnown)
        {
            note += " The drawing has no unit scale (INSUNITS 0), so 1 unit was read as 1 inch.";
        }

        context.Log($"check_drawing {floor.Name} ({stage}) on {title}: {report.Errors} errors, {report.Warnings} warnings.");
        return note.Length == 0 ? result : result with { Message = result.Message + note };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // apply_drawing_template
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> ApplyDrawingTemplateAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var scale = args.GetStringOrNull("scale") ?? "1/4";
        var options = new TemplateOptions(scale, args.GetBool("overwrite"), args.GetBool("title_block", fallback: true));
        var settings = AccuracySettings.CreateDefault();
        var given = args.GetStringOrNull("template_path")?.Trim().Trim('"');
        var useBuiltIn = args.GetBool("use_built_in");
        var path = given ?? (useBuiltIn ? null : settings.TemplatePath);
        if (path is not null && AccuracySettings.CheckTemplatePath(path) is { } problem)
        {
            throw new ToolException(problem + (given is null ? " (This is the remembered template; pass use_built_in true with remember_template true to forget it.)" : string.Empty));
        }

        var remembered = string.Empty;
        if (args.GetBool("remember_template"))
        {
            try
            {
                settings.SetTemplatePath(useBuiltIn && given is null ? null : path);
                remembered = path is null || (useBuiltIn && given is null) ? " The remembered template was cleared: the built-in template is the default again." : $" {path} is remembered as the default template.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remembered = " (The template choice could not be saved: " + ex.Message + ")";
            }
        }

        var (template, source) = path is null ? TemplateApplier.BuiltIn(BridgeLocator.PluginDirectory) : (DrawingTemplate.Default, path);
        var report = await context.WriteAsync(
            (document, transaction) => path is null
                ? TemplateApplier.Apply(document.Database, transaction, template, options)
                : TemplateApplier.Import(document.Database, transaction, path, options),
            cancellationToken).ConfigureAwait(false);

        var factor = DrawingTemplate.FactorFor(scale);
        var data = new JsonObject
        {
            ["source"] = source,
            ["created"] = AccuracyStrings(report.Created),
            ["updated"] = AccuracyStrings(report.Updated),
            ["kept"] = AccuracyStrings(report.Kept),
            ["warnings"] = AccuracyStrings(report.Warnings),
            ["currentDimStyle"] = report.CurrentDimStyle,
        };
        if (path is null)
        {
            data["textHeights"] = new JsonArray([.. template.TextHeights.Select(h => (JsonNode)new JsonObject
            {
                ["use"] = h.Use,
                ["height"] = Inch.Text(scale == "1/8" ? h.EighthInch : h.QuarterInch),
            })]);
            data["textStyle"] = "ARCH (height 0: TEXT asks for the height)";
            if (template.TitleBlock is { } tb)
            {
                data["titleBlock"] = new JsonObject
                {
                    ["block"] = tb.Name,
                    ["sheet"] = string.Create(CultureInfo.InvariantCulture, $"{Inch.Size(tb.Width * factor, tb.Height * factor)} in model space at {scale}\" = 1'-0\""),
                    ["insert"] = string.Create(CultureInfo.InvariantCulture, $"SETVAR CLAYER A-TTLB, then -INSERT [\"{tb.Name}\",<lower-left corner>,\"{factor}\",\"{factor}\",\"0\", then one answer per attribute prompt ({string.Join(", ", tb.Attributes.Select(a => a.Tag))})]"),
                };
            }
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Template applied as one undo step ({(path is null ? "built-in" : Path.GetFileName(path))}): {report.Created.Count} created, {report.Updated.Count} updated, {report.Kept.Count} kept{(report.Warnings.Count > 0 ? ", " + report.Warnings.Count + " warnings" : string.Empty)}. Current dimension style {report.CurrentDimStyle ?? "unchanged"}.{remembered}");
        context.Log(message);
        return ToolResult.Ok(message, data);
    }

    private static JsonArray AccuracyStrings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);
}
