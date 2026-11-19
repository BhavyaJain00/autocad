using System.IO;
using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Inspect;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Tools;

/// <summary>
/// Inspection tools, all read-only (Risk Read, read transactions, no document lock): get_drawing_info, capture_view,
/// list_layers, list_entities, get_entity and get_sysvars. The last five are defined in <see cref="InspectToolDefinitions"/>;
/// the work is done by <see cref="InspectQueries"/>, <see cref="EntitySummary"/>, <see cref="ViewGeometry"/> and
/// <see cref="ViewCapture"/>.
/// </summary>
public sealed partial class ToolRegistry
{
    /// <summary>Above this many model-space entities get_drawing_info reports the header extents instead of measuring.</summary>
    private const int MaxEntitiesForMeasuredExtents = 50_000;

    static partial void AddInspectTools(List<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Add(new ToolSpec(GetDrawingInfoDefinition(), GetDrawingInfoAsync));
        tools.Add(new ToolSpec(InspectToolDefinitions.CaptureViewDefinition(), CaptureViewAsync));
        tools.Add(new ToolSpec(InspectToolDefinitions.ListLayersDefinition(), ListLayersAsync));
        tools.Add(new ToolSpec(InspectToolDefinitions.ListEntitiesDefinition(), ListEntitiesAsync));
        tools.Add(new ToolSpec(InspectToolDefinitions.GetEntityDefinition(), GetEntityAsync));
        tools.Add(new ToolSpec(InspectToolDefinitions.GetSysvarsDefinition(), GetSysvarsAsync));
    }

    private static ToolDefinition GetDrawingInfoDefinition() => new()
    {
        Name = "get_drawing_info",
        Title = "Drawing info",
        Risk = ToolRisk.Read,
        Description =
            "Read the state of the active AutoCAD drawing. Call it before you start drafting and whenever you need to " +
            "re-sync. Returns: product (\"AutoCAD 2027\" = plain AutoCAD; a vertical such as AutoCAD Architecture is named) " +
            "and architectureObjects (true only in AutoCAD Architecture / MEP, which have wall, door and window objects; in plain AutoCAD draw them with lines); file (name, path, saved, modified, readOnly); units (LUNITS display format, " +
            "INSUNITS real-world unit, LUPREC precision, inchesPerUnit = what 1 drawing unit is in inches, and a summary); " +
            "space (model or paper layout); model-space extents (min, max, width, height; empty when nothing is drawn); " +
            "currentLayer and layerCount; entity counts by DXF type in model space (LINE, LWPOLYLINE, ARC, TEXT, " +
            "DIMENSION, HATCH, INSERT ...) plus the paper-space total; the current view (centre, width, height and visible area in world coordinates); styles " +
            "(current text and dimension style, DIMSCALE, LTSCALE, annotation scale); whether a command is active in " +
            "the command line (and its name); and the drawing revision, which increases on every change, including " +
            "changes the user makes. Lengths are {du: drawing units, ftIn: feet-inches text} and points {x, y, ftIn}; " +
            "ftIn is given when the real-world scale is known (architectural or engineering units: 1 unit = 1 inch).",
        InputSchema = Schema.Object(),
    };

    private static async Task<ToolResult> GetDrawingInfoAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var data = await context.ReadAsync((document, transaction) => ReadDrawingInfo(document, transaction), cancellationToken).ConfigureAwait(false);
        data["revision"] = context.Revision;
        var file = data["file"]?["name"]?.GetValue<string>() ?? "drawing";
        var total = data["entities"]?["total"]?.GetValue<int>() ?? 0;
        var units = data["units"]?["summary"]?.GetValue<string>() ?? string.Empty;
        return ToolResult.Ok(
            string.Create(CultureInfo.InvariantCulture, $"{file}: {total} model-space entities; units {units}."),
            data);
    }

    private static JsonObject ReadDrawingInfo(Document document, Transaction transaction)
    {
        var database = document.Database;
        var units = ToolContext.GetUnits(database);
        var result = new JsonObject
        {
            ["product"] = AcadHost.ProductName,
            ["architectureObjects"] = AcadHost.HasArchitectureObjects,
            ["file"] = new JsonObject
            {
                ["name"] = Path.GetFileName(document.Name),
                ["path"] = document.IsNamedDrawing ? document.Name : null,
                ["saved"] = document.IsNamedDrawing,
                ["modified"] = SystemVariableInt("DBMOD") != 0,
                ["readOnly"] = document.IsReadOnly,
            },
            ["units"] = new JsonObject
            {
                ["lunits"] = units.LUnits,
                ["format"] = units.FormatName,
                ["insunits"] = units.InsUnits,
                ["luprec"] = units.LuPrec,
                ["unit"] = units.UnitName,
                ["inchesPerUnit"] = units.InchesPerUnit,
                ["acceptsFeetInches"] = units.AcceptsFeetInches,
                ["summary"] = units.Describe(),
            },
            ["space"] = database.TileMode ? "model" : "paper:" + CurrentLayoutName(),
        };

        var clayer = (LayerTableRecord)transaction.GetObject(database.Clayer, OpenMode.ForRead);
        result["currentLayer"] = clayer.Name;
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var layerCount = 0;
        foreach (var _ in layerTable)
        {
            layerCount++;
        }

        result["layerCount"] = layerCount;

        var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var ids = new List<ObjectId>();
        foreach (ObjectId id in modelSpace)
        {
            ids.Add(id);
            var cls = id.ObjectClass;
            var type = string.IsNullOrEmpty(cls.DxfName) ? cls.Name : cls.DxfName;
            counts[type] = counts.TryGetValue(type, out var n) ? n + 1 : 1;
        }

        var byType = new JsonObject();
        foreach (var (type, count) in counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            byType[type] = count;
        }

        result["entities"] = new JsonObject { ["total"] = ids.Count, ["byType"] = byType };
        result["paperSpaceEntities"] = CountPaperSpaceEntities(database, transaction);
        result["extents"] = Extents(database, transaction, ids, units);
        result["view"] = CurrentView(document, units);
        result["styles"] = new JsonObject
        {
            ["textStyle"] = SystemVariableString("TEXTSTYLE"),
            ["dimStyle"] = SystemVariableString("DIMSTYLE"),
            ["dimscale"] = ToolContext.Round(database.Dimscale),
            ["ltscale"] = ToolContext.Round(database.Ltscale),
            ["annotationScale"] = SafeAnnotationScale(database),
        };
        var command = document.CommandInProgress;
        result["command"] = new JsonObject
        {
            ["active"] = !string.IsNullOrEmpty(command),
            ["name"] = string.IsNullOrEmpty(command) ? null : command,
        };
        return result;
    }

    private static JsonObject Extents(Database database, Transaction transaction, List<ObjectId> ids, DrawingUnits units)
    {
        if (ids.Count == 0)
        {
            return new JsonObject { ["empty"] = true };
        }

        Extents3d? extents = null;
        var approximate = false;
        if (ids.Count > MaxEntitiesForMeasuredExtents)
        {
            extents = new Extents3d(database.Extmin, database.Extmax);
            approximate = true;
        }
        else
        {
            foreach (var id in ids)
            {
                if (id.IsErased)
                {
                    continue;
                }

                try
                {
                    if (transaction.GetObject(id, OpenMode.ForRead, false) is Entity { Visible: true } entity && entity.Bounds is { } bounds)
                    {
                        if (extents is { } current)
                        {
                            current.AddExtents(bounds);
                            extents = current;
                        }
                        else
                        {
                            extents = bounds;
                        }
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // Entities without measurable bounds (rays, xlines, empty text) do not count.
                }
            }
        }

        if (extents is not { } box || box.MinPoint.X > box.MaxPoint.X)
        {
            return new JsonObject { ["empty"] = true, ["note"] = "Model space has entities but none with measurable bounds." };
        }

        var min = box.MinPoint;
        var max = box.MaxPoint;
        var result = new JsonObject
        {
            ["empty"] = false,
            ["min"] = ToolContext.Point(new Point3d(min.X, min.Y, 0), units),
            ["max"] = ToolContext.Point(new Point3d(max.X, max.Y, 0), units),
            ["width"] = ToolContext.Length(max.X - min.X, units),
            ["height"] = ToolContext.Length(max.Y - min.Y, units),
        };
        if (approximate)
        {
            result["approximate"] = true;
        }

        return result;
    }

    private static JsonObject? CurrentView(Document document, DrawingUnits units)
    {
        try
        {
            // World coordinates: the view centre in DCS equals WCS only for an untwisted plan view targeting 0,0,0.
            return ViewGeometry.Read(document).ToJson(units);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static int CountPaperSpaceEntities(Database database, Transaction transaction)
    {
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var total = 0;
        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (!block.IsLayout || SymbolUtilityServices.IsBlockModelSpaceName(block.Name))
            {
                continue;
            }

            foreach (ObjectId _ in block)
            {
                total++;
            }
        }

        return total;
    }

    private static string CurrentLayoutName()
    {
        try
        {
            return LayoutManager.Current.CurrentLayout;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return "?";
        }
    }

    private static string? SafeAnnotationScale(Database database)
    {
        try
        {
            return database.Cannoscale?.Name;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    private static int SystemVariableInt(string name)
    {
        try
        {
            return Convert.ToInt32(AcadApp.GetSystemVariable(name), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidCastException or FormatException or OverflowException)
        {
            return 0;
        }
    }

    private static string? SystemVariableString(string name)
    {
        try
        {
            return AcadApp.GetSystemVariable(name)?.ToString();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // capture_view
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> CaptureViewAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var window = ReadWindow(args, requireSize: true);
        var area = args.GetStringOrNull("area") ?? (window is null ? "view" : "window");
        if (area == "window" && window is null)
        {
            throw new ToolException("area 'window' needs window: [x0, y0, x1, y1], two opposite corners in drawing units.");
        }

        if (area != "window" && window is not null)
        {
            throw new ToolException($"window is only used with area 'window' (area is '{area}'). Leave out area, or set it to 'window'.");
        }

        var maxWidth = (int)Math.Clamp(args.GetLong("width", CaptureMath.DefaultWidth), 400, CaptureMath.MaxWidth);
        var white = (args.GetStringOrNull("background") ?? "white") != "dark";
        var method = args.GetStringOrNull("method") ?? "auto";

        return await context.InvokeAsync(
            () =>
            {
                var document = context.Document;
                var database = document.Database;
                var units = ToolContext.GetUnits(database);
                var view = ViewGeometry.Read(document);
                var showsSheet = area == "view" && view.IsPaperSpace;
                SpaceScan scan;
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    var spaceId = showsSheet ? database.CurrentSpaceId : SymbolUtilityServices.GetBlockModelSpaceId(database);
                    scan = InspectQueries.ScanSpace(database, transaction, spaceId, area == "view" ? view.Visible : window);
                    transaction.Commit();
                }

                PlanBox? target = window;
                if (area == "extents")
                {
                    target = scan.Extents ?? throw new ToolException("Model space is empty, so there are no extents to show. Use area 'view' to see the current view.");
                }

                var request = new CaptureRequest
                {
                    MaxWidth = maxWidth,
                    Area = target,
                    WhiteBackground = white,
                    Methods = CaptureMethods(method, showsSheet),
                    ExpectBlank = area == "extents" ? scan.Total == 0 : scan.InArea == 0 && (view.IsPlan || area != "view"),
                };
                var outcome = ViewCapture.Capture(document, request);
                return CaptureResult(outcome, area, view, scan, units, white);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<CaptureMethod> CaptureMethods(string method, bool showsSheet) => method switch
    {
        "offscreen" => [CaptureMethod.Offscreen],
        "screen" => [CaptureMethod.Screen],
        "preview" => [CaptureMethod.Preview],

        // A layout sheet off-screen shows the paper-space entities but not what its viewports show: prefer the screen.
        _ when showsSheet => [CaptureMethod.Screen, CaptureMethod.Offscreen, CaptureMethod.Preview],
        _ => [CaptureMethod.Offscreen, CaptureMethod.Screen, CaptureMethod.Preview],
    };

    private static ToolResult CaptureResult(CaptureOutcome outcome, string area, ViewFacts view, SpaceScan scan, DrawingUnits units, bool white)
    {
        var image = outcome.Image;
        var shown = outcome.Shown ?? view.Visible;
        var data = new JsonObject
        {
            ["picture"] = new JsonObject
            {
                ["width"] = image.Width,
                ["height"] = image.Height,
                ["method"] = ViewCapture.WireName(outcome.Method),
                ["background"] = outcome.Method == CaptureMethod.Offscreen ? (white ? "white (plot-like)" : "black") : "as on the screen",
            },
            ["area"] = area,
            ["shown"] = ViewFacts.BoxJson(shown, units),
            ["view"] = view.ToJson(units),
            ["drawing"] = InspectQueries.ScanJson(scan, units),
            ["units"] = units.Describe(),
        };
        if (outcome.Blank)
        {
            data["blank"] = true;
        }

        if (outcome.Notes.Count > 0)
        {
            data["notes"] = new JsonArray([.. outcome.Notes.Select(note => (JsonNode)note)]);
        }

        var what = area switch
        {
            "extents" => "the whole drawing",
            "window" => "the requested window",
            _ => view.IsPaperSpace ? "the current layout sheet" : "the current view",
        };
        var content = outcome.Blank
            ? "it is blank (nothing is drawn there)"
            : scan.InArea is { } inArea && area != "extents"
                ? string.Create(CultureInfo.InvariantCulture, $"{inArea} of {scan.Total} entities are in it")
                : string.Create(CultureInfo.InvariantCulture, $"{scan.Total} entities");
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Picture of {what} ({image.Width} x {image.Height} px, {ViewCapture.Describe(outcome.Method)}): {content}.");
        return ToolResult.Ok(message, data) with { Images = [image] };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // list_layers, list_entities, get_entity, get_sysvars
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> ListLayersAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var name = NamePattern.Parse(args.GetStringOrNull("name"));
        var counts = args.GetBool("counts", fallback: true);
        var data = await context.ReadAsync((document, transaction) => InspectQueries.ListLayers(document, transaction, name, counts), cancellationToken).ConfigureAwait(false);
        var returned = data["returned"]?.GetValue<int>() ?? 0;
        var total = data["total"]?.GetValue<int>() ?? 0;
        var current = data["current"]?.GetValue<string>() ?? "?";
        return ToolResult.Ok(
            string.Create(CultureInfo.InvariantCulture, $"{returned} of {total} layers; current layer '{current}'."),
            data);
    }

    private static async Task<ToolResult> ListEntitiesAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var window = ReadWindow(args, requireSize: false);
        var inside = args.GetBool("inside");
        if (inside && window is null)
        {
            throw new ToolException("inside=true needs a window: [x0, y0, x1, y1] in drawing units.");
        }

        var filter = new EntityFilter(
            NamePattern.Parse(args.GetStringOrNull("layer")),
            NamePattern.Parse(args.GetStringOrNull("type")),
            window,
            inside,
            CurrentSpace: args.GetStringOrNull("space") == "current",
            Limit: (int)Math.Clamp(args.GetLong("limit", InspectToolDefinitions.DefaultListLimit), 1, InspectToolDefinitions.MaxListLimit),
            Offset: (int)Math.Max(0, args.GetLong("offset", 0)));
        var data = await context.ReadAsync((document, transaction) => InspectQueries.ListEntities(document, transaction, filter), cancellationToken).ConfigureAwait(false);
        var total = data["total"]?.GetValue<int>() ?? 0;
        var returned = data["returned"]?.GetValue<int>() ?? 0;
        var message = total == 0
            ? "No entities match."
            : string.Create(CultureInfo.InvariantCulture, $"{returned} of {total} matching entities (from {filter.Offset + 1}).");
        if (data["nextOffset"] is { } next)
        {
            message += string.Create(CultureInfo.InvariantCulture, $" More: offset {next.GetValue<int>()}.");
        }

        return ToolResult.Ok(message, data);
    }

    private static async Task<ToolResult> GetEntityAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var single = args.GetStringOrNull("handle");
        var many = args.Has("handles") ? args.GetStringList("handles") : null;
        if ((single is null) == (many is null))
        {
            throw new ToolException("Give either handle (one entity) or handles (several), not both and not neither.");
        }

        IReadOnlyList<string> handles = many ?? [single!];
        var (found, problems) = await context.ReadAsync((document, transaction) => InspectQueries.GetEntities(document, transaction, handles), cancellationToken).ConfigureAwait(false);
        if (found.Count == 0)
        {
            return ToolResult.Fail("No entity found.", problems);
        }

        var data = new JsonObject { ["entities"] = found };
        var result = ToolResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Read {found.Count} entit{(found.Count == 1 ? "y" : "ies")}."), data);
        return problems.Count == 0 ? result : result with { Warnings = problems };
    }

    private static async Task<ToolResult> GetSysvarsAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var names = new ToolArguments(arguments).GetStringList("names");
        var data = await context.InvokeAsync(() => InspectQueries.GetSysvars(names), cancellationToken).ConfigureAwait(false);
        var read = data["values"]?.AsObject().Count ?? 0;
        if (read == 0)
        {
            return ToolResult.Fail("None of these names is an AutoCAD system variable: " + string.Join(", ", names) + ".");
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"Read {read} system variable{(read == 1 ? string.Empty : "s")}.");
        if (data["unknown"] is JsonArray unknown)
        {
            message += " Unknown: " + string.Join(", ", unknown.Select(n => n?.GetValue<string>())) + ".";
        }

        return ToolResult.Ok(message, data);
    }

    /// <summary>Reads the optional window argument [x0, y0, x1, y1] as a WCS box.</summary>
    private static PlanBox? ReadWindow(ToolArguments args, bool requireSize)
    {
        if (!args.Has("window"))
        {
            return null;
        }

        var values = args.GetArray("window")
            .Select(node => CopilotJson.TryGetDouble(node, out var value) ? value : double.NaN)
            .ToList();
        if (values.Count != 4 || values.Any(v => !double.IsFinite(v)))
        {
            throw new ToolException("window must be 4 numbers: [x0, y0, x1, y1], two opposite corners in drawing units.");
        }

        var box = PlanBox.FromCorners(values[0], values[1], values[2], values[3]);
        if (requireSize && (box.Width <= 0 || box.Height <= 0))
        {
            throw new ToolException("window needs a width and a height: give two opposite corners of a rectangle.");
        }

        return box;
    }
}
