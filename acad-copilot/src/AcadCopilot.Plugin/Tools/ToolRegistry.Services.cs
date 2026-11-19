using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Presentation;
using AcadCopilot.Core.Services;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Inspect;
using AcadCopilot.Plugin.Services;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Tools;

/// <summary>
/// Services and presentation tools: insert_symbol_library and place_services_legend (electrical / plumbing first-draft
/// layouts), fill_presentation_regions and add_presentation_backdrop (colour presentation elevation) and
/// export_view_png (a shareable PNG). Definitions live in AcadCopilot.Core (Services/, Presentation/), the AutoCAD work
/// in Plugin/Services/. Everything touching AutoCAD runs through <see cref="ToolContext"/> on the main thread.
/// </summary>
public sealed partial class ToolRegistry
{
    /// <summary>Adds the services and presentation tools (called from CreateDefault).</summary>
    static partial void AddServicesTools(List<ToolSpec> tools);

    static partial void AddServicesTools(List<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Add(new ToolSpec(ServiceToolDefinitions.InsertSymbolLibraryDefinition(), InsertSymbolLibraryAsync));
        tools.Add(new ToolSpec(ServiceToolDefinitions.PlaceServicesLegendDefinition(), PlaceServicesLegendAsync));
        tools.Add(new ToolSpec(PresentationToolDefinitions.FillRegionsDefinition(), FillPresentationRegionsAsync));
        tools.Add(new ToolSpec(PresentationToolDefinitions.AddBackdropDefinition(), AddPresentationBackdropAsync));
        tools.Add(new ToolSpec(PresentationToolDefinitions.ExportViewPngDefinition(), ExportViewPngAsync));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // insert_symbol_library
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> InsertSymbolLibraryAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var discipline = args.GetStringOrNull("discipline") ?? "both";
        var replace = args.GetBool("replace_existing");
        var file = args.GetStringOrNull("file");
        var builtIn = file is null;
        string path;
        if (builtIn)
        {
            path = LibraryPathOrFail();
        }
        else
        {
            path = file!.Trim().Trim('"');
            var extension = Path.GetExtension(path);
            if (!Path.IsPathFullyQualified(path) || !(extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ToolException("file must be the full path of a .dwg or .dxf drawing, for example C:\\Blocks\\MyServices.dwg.");
            }

            if (!File.Exists(path))
            {
                throw new ToolException($"{path} does not exist. Ask the user for the right path, or leave file out to use the built-in library.");
            }
        }

        var symbols = SymbolsOf(discipline);
        var (import, layers) = await context.WriteAsync(
            (document, transaction) =>
            {
                var result = SymbolLibraryImporter.Import(document.Database, transaction, path, builtIn ? [.. symbols.Select(s => s.BlockName)] : null, replace, inches: builtIn);
                var created = builtIn ? SymbolLibraryImporter.EnsureLayers(document.Database, transaction, LayersOf(discipline)) : [];
                return (result, created);
            },
            cancellationToken).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["file"] = import.File,
            ["imported"] = Strings(import.Imported),
            ["replaced"] = Strings(import.Replaced),
            ["alreadyInDrawing"] = Strings(import.Existing),
            ["layersCreated"] = Strings(layers),
        };
        if (builtIn)
        {
            data["blocks"] = new JsonArray([.. symbols.Select(s => (JsonNode)SymbolJson(s))]);
            data["insertExample"] = "run_commands step {\"command\":\"-INSERT\",\"inputs\":[\"E-SWITCHBOARD\",\"7'1.5,4'0\",\"\",\"\",\"0\",\"SB1\"]} after SETVAR CLAYER E-SWCH";
        }

        var total = import.Imported.Count + import.Replaced.Count + import.Existing.Count;
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"{total} blocks ready ({import.Imported.Count} imported, {import.Replaced.Count} redefined, {import.Existing.Count} already in the drawing) from {Path.GetFileName(import.File)}") +
            (layers.Count > 0 ? "; layers created: " + string.Join(", ", layers) + "." : ".") +
            (builtIn ? " Place them with -INSERT on their layers (blocks lists layer, base point and the label prompt); finish with place_services_legend." : string.Empty);
        context.Log(message);
        return ToolResult.Ok(message, data);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // place_services_legend
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> PlaceServicesLegendAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var discipline = args.GetString("discipline") == "plumbing" ? ServiceDiscipline.Plumbing : ServiceDiscipline.Electrical;
        var at = Numbers(args, "at", 2);
        var size = args.GetDouble("size", 1);
        var title = args.GetStringOrNull("title");
        List<SymbolDefinition>? requested = null;
        if (args.Has("symbols"))
        {
            requested = [];
            foreach (var name in args.GetStringList("symbols"))
            {
                requested.Add(SymbolLibrary.Find(name) ?? throw new ToolException(
                    $"'{name}' is not a library symbol. Use: {string.Join(", ", SymbolLibrary.All.Select(s => s.BlockName))}."));
            }
        }

        var path = LibraryPathOrFail();
        var (layout, created, rowsFrom) = await context.WriteAsync(
            (document, transaction) =>
            {
                var database = document.Database;
                string source;
                List<SymbolDefinition> rows;
                if (requested is not null)
                {
                    rows = requested;
                    source = "as requested";
                }
                else
                {
                    rows = UsedSymbols(database, transaction, discipline);
                    source = rows.Count > 0 ? "the symbols used in the drawing" : "all symbols (none placed yet)";
                    if (rows.Count == 0)
                    {
                        rows = [.. SymbolLibrary.Of(discipline)];
                    }
                }

                var missing = SymbolLibraryImporter.MissingBlocks(database, transaction, rows);
                if (missing.Count > 0)
                {
                    SymbolLibraryImporter.Import(database, transaction, path, missing, replace: false, inches: true);
                }

                SymbolLibraryImporter.EnsureLayers(database, transaction, ServiceLayers.All.Where(l => rows.Any(r => r.Layer == l.Name) || l.Name == ServiceLayers.NotesLayer(discipline)));
                var unit = ToolContext.GetUnits(database).InchesPerUnit is { } inches && inches > 0 ? 1 / inches : 1;
                var legend = ServicesLegend.Build(discipline, rows, at[0], at[1], unit * size, title);
                var ids = ServicesLegendWriter.Draw(database, transaction, legend);
                return (legend, ids, source);
            },
            cancellationToken).ConfigureAwait(false);

        var units = await context.ReadAsync((document, _) => ToolContext.GetUnits(document.Database), cancellationToken).ConfigureAwait(false);
        var data = new JsonObject
        {
            ["layer"] = layout.Layer,
            ["rows"] = Strings(layout.Symbols.Select(s => s.Symbol.BlockName)),
            ["rowsFrom"] = rowsFrom,
            ["extents"] = new JsonObject
            {
                ["min"] = PointJson(layout.MinX, layout.MinY, units),
                ["max"] = PointJson(layout.MaxX, layout.MaxY, units),
            },
            ["draftNote"] = SymbolLibrary.DraftNote(discipline),
        };
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"{(discipline == ServiceDiscipline.Electrical ? "Electrical" : "Plumbing")} legend with {layout.Symbols.Count} rows ({rowsFrom}) and the draft note drawn on {layout.Layer}, {created.Count} objects. Check it with capture_view.");
        return ToolResult.Ok(message, data) with { CreatedIds = [.. created.Select(ToolContext.HandleValueOf)] };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // fill_presentation_regions
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record FillRequest(int Index, PresentationFinish Finish, double[]? Point, string? Handle);

    private static async Task<ToolResult> FillPresentationRegionsAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var islands = args.GetBool("islands", fallback: true);
        var requests = new List<FillRequest>();
        var index = 0;
        foreach (var fill in args.GetObjectList("fills"))
        {
            var name = fill.GetString("finish");
            var finish = PresentationFinishes.Find(name) ?? throw new ToolException($"fills[{index}]: unknown finish '{name}'. Use: {string.Join(", ", PresentationFinishes.Names)}.");
            var point = fill.Has("point") ? Numbers(fill, "point", 2) : null;
            var handle = fill.GetStringOrNull("handle");
            if ((point is null) == (handle is null))
            {
                throw new ToolException($"fills[{index}]: give either point [x, y] (inside a closed area) or handle (a closed boundary object), not both and not neither.");
            }

            var resolved = PresentationFinishes.Resolve(
                finish,
                Color(fill, "color", index),
                Color(fill, "color2", index),
                fill.Has("gradient") ? fill.GetBool("gradient") : null,
                fill.GetDoubleOrNull("angle"));
            requests.Add(new FillRequest(index, resolved, point, handle));
            index++;
        }

        var (results, layersCreated) = await context.WriteAsync(
            (document, transaction) =>
            {
                var created = PresentationPainter.EnsureLayers(document.Database, transaction);
                var outcomes = new List<(FillRequest Request, PaintedFill? Fill, string? Error)>();
                foreach (var request in requests)
                {
                    try
                    {
                        var painted = request.Point is { } p
                            ? PresentationPainter.FillAtPoint(document, transaction, request.Finish, p[0], p[1], islands)
                            : PresentationPainter.FillHandle(document.Database, transaction, request.Finish, request.Handle!);
                        outcomes.Add((request, painted, null));
                    }
                    catch (Exception ex) when (ex is ToolException or Autodesk.AutoCAD.Runtime.Exception)
                    {
                        outcomes.Add((request, null, ex is Autodesk.AutoCAD.Runtime.Exception acad ? $"AutoCAD error {acad.ErrorStatus}: no fill could be made there" : ex.Message));
                    }
                }

                if (outcomes.Any(o => o.Fill is not null))
                {
                    PresentationPainter.Reorder(document.Database, transaction);
                }

                return (outcomes, created);
            },
            cancellationToken).ConfigureAwait(false);

        var units = await context.ReadAsync((document, _) => ToolContext.GetUnits(document.Database), cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        foreach (var (request, fill, error) in results)
        {
            var item = new JsonObject { ["index"] = request.Index, ["finish"] = request.Finish.Name };
            if (fill is not null)
            {
                item["handle"] = ToolContext.HandleOf(fill.Id);
                item["layer"] = fill.Layer;
                item["area"] = AreaJson(fill.Area, units);
                item["color"] = request.Finish.Color.ToString();
                if (fill.Loops > 1)
                {
                    item["islandsLeftOpen"] = fill.Loops - 1;
                }
            }
            else
            {
                item["error"] = error;
            }

            items.Add(item);
        }

        var made = results.Where(r => r.Fill is not null).ToList();
        var failed = results.Count - made.Count;
        var data = new JsonObject { ["fills"] = items, ["layersCreated"] = Strings(layersCreated) };
        var message = string.Create(CultureInfo.InvariantCulture, $"{made.Count} of {results.Count} areas filled, behind the linework.") +
            (failed > 0 ? " Failed: " + string.Join("; ", results.Where(r => r.Fill is null).Select(r => $"[{r.Request.Index}] {r.Error}")) : string.Empty);
        var result = made.Count == 0
            ? ToolResult.Fail(message) with { Data = data }
            : ToolResult.Ok(message, data) with { CreatedIds = [.. made.Select(m => ToolContext.HandleValueOf(m.Fill!.Id))] };
        return result;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // add_presentation_backdrop
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> AddPresentationBackdropAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var window = Numbers(args, "window", 4);
        var elevation = PlanRect.FromCorners(window[0], window[1], window[2], window[3]);
        if (!(elevation.Width > 0) || !(elevation.Height > 0))
        {
            throw new ToolException("window needs a width and a height: two opposite corners of the elevation's extents.");
        }

        var groundY = args.GetDouble("ground_y");
        var sky = args.GetBool("sky", fallback: true);
        var groundName = args.GetStringOrNull("ground") ?? PresentationFinishes.Ground;
        var sunFromLeft = (args.GetStringOrNull("sun") ?? "left") == "left";
        var (skyRect, groundRect) = PresentationGeometry.Backdrop(elevation, groundY);

        var polygons = new List<(string What, PresentationFinish Finish, IReadOnlyList<PlanPoint> Polygon)>();
        if (sky)
        {
            polygons.Add(("sky", PresentationFinishes.Find(PresentationFinishes.Sky)!, skyRect.Corners()));
        }

        if (groundName != "none")
        {
            polygons.Add((groundName, PresentationFinishes.Find(groundName)!, groundRect.Corners()));
        }

        if (args.Has("shadows"))
        {
            var shadowFinish = PresentationFinishes.Find(PresentationFinishes.Shadow)!;
            var n = 0;
            foreach (var shadow in args.GetObjectList("shadows"))
            {
                var box = Numbers(shadow, "box", 4);
                var kind = (shadow.GetStringOrNull("kind") ?? "below") == "side" ? ShadowKind.Side : ShadowKind.Below;
                var polygon = PresentationGeometry.Shadow(PlanRect.FromCorners(box[0], box[1], box[2], box[3]), shadow.GetDouble("depth"), kind, sunFromLeft, groundY);
                polygons.Add(($"shadow {n++}", shadowFinish, polygon));
            }
        }

        if (polygons.Count == 0)
        {
            throw new ToolException("Nothing to add: sky is false, ground is none and there are no shadows.");
        }

        var results = await context.WriteAsync(
            (document, transaction) =>
            {
                PresentationPainter.EnsureLayers(document.Database, transaction);
                var outcomes = new List<(string What, PaintedFill? Fill, string? Error)>();
                foreach (var (what, finish, polygon) in polygons)
                {
                    try
                    {
                        outcomes.Add((what, PresentationPainter.FillPolygon(document.Database, transaction, finish, polygon), null));
                    }
                    catch (Exception ex) when (ex is ToolException or Autodesk.AutoCAD.Runtime.Exception)
                    {
                        outcomes.Add((what, null, ex.Message));
                    }
                }

                if (outcomes.Any(o => o.Fill is not null))
                {
                    PresentationPainter.Reorder(document.Database, transaction);
                }

                return outcomes;
            },
            cancellationToken).ConfigureAwait(false);

        var items = new JsonArray([.. results.Select(r => (JsonNode)(r.Fill is { } f
            ? new JsonObject { ["what"] = r.What, ["handle"] = ToolContext.HandleOf(f.Id), ["layer"] = f.Layer }
            : new JsonObject { ["what"] = r.What, ["error"] = r.Error }))]);
        var made = results.Where(r => r.Fill is not null).ToList();
        var message = string.Create(CultureInfo.InvariantCulture, $"Added {made.Count} of {results.Count} backdrop fills (sky, ground, shadows) behind the elevation. Check with capture_view.");
        var data = new JsonObject
        {
            ["fills"] = items,
            ["sky"] = RectJson(skyRect),
            ["ground"] = RectJson(groundRect),
        };
        return made.Count == 0
            ? ToolResult.Fail(message) with { Data = data }
            : ToolResult.Ok(message, data) with { CreatedIds = [.. made.Select(m => ToolContext.HandleValueOf(m.Fill!.Id))] };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // export_view_png
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ToolResult> ExportViewPngAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var window = args.Has("window") ? Numbers(args, "window", 4) : null;
        var area = args.GetStringOrNull("area") ?? (window is null ? "extents" : "window");
        if (area == "window" && window is null)
        {
            throw new ToolException("area 'window' needs window: [x0, y0, x1, y1] in drawing units.");
        }

        if (area != "window" && window is not null)
        {
            throw new ToolException($"window is only used with area 'window' (area is '{area}').");
        }

        var width = (int)Math.Clamp(args.GetLong("width", PresentationToolDefinitions.DefaultExportWidth), PresentationToolDefinitions.MinExportWidth, PresentationToolDefinitions.MaxExportWidth);
        var white = (args.GetStringOrNull("background") ?? "white") != "black";
        var name = args.GetStringOrNull("name");

        var (picture, drawingName) = await context.InvokeAsync(
            () =>
            {
                var document = context.Document;
                PlanBox box;
                if (area == "window")
                {
                    box = PlanBox.FromCorners(window![0], window[1], window[2], window[3]);
                    if (box.Width <= 0 || box.Height <= 0)
                    {
                        throw new ToolException("window needs a width and a height.");
                    }
                }
                else if (area == "view")
                {
                    box = ViewGeometry.Read(document).Visible;
                }
                else
                {
                    box = PresentationExporter.Extents(document) ?? throw new ToolException("Model space has nothing visible to export.");
                }

                return (PresentationExporter.Render(document, box, width, white), Path.GetFileName(document.Name));
            },
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        string path;
        try
        {
            var folder = Directory.CreateDirectory(ExportFiles.Folder).FullName;
            path = ExportFiles.NewPath(folder, ExportFiles.SafeBaseName(name, drawingName), DateTimeOffset.Now);
            await File.WriteAllBytesAsync(path, picture.Png, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolException("Could not write the PNG in " + ExportFiles.Folder + ": " + ex.Message);
        }

        context.Log($"Exported {picture.Width}x{picture.Height} to {path} ({picture.Method}).");
        var data = new JsonObject
        {
            ["file"] = path,
            ["folder"] = Path.GetDirectoryName(path),
            ["width"] = picture.Width,
            ["height"] = picture.Height,
            ["bytes"] = picture.Png.Length,
            ["method"] = picture.Method,
            ["fills"] = picture.Entities.Fills,
            ["lines"] = picture.Entities.Lines,
            ["shown"] = new JsonArray(Round(picture.Shown.MinX), Round(picture.Shown.MinY), Round(picture.Shown.MaxX), Round(picture.Shown.MaxY)),
        };
        var message = string.Create(CultureInfo.InvariantCulture, $"Saved {picture.Width} x {picture.Height} px PNG: {path}. A preview is attached; tell the user where the file is.");
        return ToolResult.Ok(message, data) with { Images = [picture.Preview] };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static string LibraryPathOrFail()
    {
        try
        {
            return SymbolLibraryImporter.BuiltInLibraryPath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolException("The symbol library file could not be prepared: " + ex.Message);
        }
    }

    private static IReadOnlyList<SymbolDefinition> SymbolsOf(string discipline) => discipline switch
    {
        "electrical" => SymbolLibrary.Of(ServiceDiscipline.Electrical),
        "plumbing" => SymbolLibrary.Of(ServiceDiscipline.Plumbing),
        _ => SymbolLibrary.All,
    };

    private static IEnumerable<ServiceLayer> LayersOf(string discipline) => discipline switch
    {
        "electrical" => ServiceLayers.Of(ServiceDiscipline.Electrical),
        "plumbing" => ServiceLayers.Of(ServiceDiscipline.Plumbing),
        _ => ServiceLayers.All,
    };

    // The library symbols of a discipline placed in model space, in library order.
    private static List<SymbolDefinition> UsedSymbols(Database database, Transaction transaction, ServiceDiscipline discipline)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
            if (id.IsErased || !id.ObjectClass.DxfName.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference reference &&
                transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord definition)
            {
                used.Add(definition.Name);
            }
        }

        return [.. SymbolLibrary.Of(discipline).Where(s => used.Contains(s.BlockName))];
    }

    private static JsonObject SymbolJson(SymbolDefinition symbol)
    {
        var node = new JsonObject
        {
            ["name"] = symbol.BlockName,
            ["title"] = symbol.Title,
            ["layer"] = symbol.Layer,
            ["basePoint"] = symbol.BasePoint,
            ["use"] = symbol.Description,
        };
        if (symbol.Attribute is { } attribute)
        {
            node["labelPrompt"] = attribute.Prompt;
            node["labelDefault"] = attribute.Default;
        }

        return node;
    }

    private static double[] Numbers(ToolArguments args, string name, int count)
    {
        var values = args.GetArray(name).Select(node => CopilotJson.TryGetDouble(node, out var value) ? value : double.NaN).ToArray();
        if (values.Length != count || values.Any(v => !double.IsFinite(v)))
        {
            throw new ToolException($"{name} must be {count} numbers in drawing units.");
        }

        return values;
    }

    private static Rgb? Color(ToolArguments fill, string name, int index)
    {
        var text = fill.GetStringOrNull(name);
        if (text is null)
        {
            return null;
        }

        return Rgb.TryParse(text, out var color)
            ? color
            : throw new ToolException($"fills[{index}].{name}: '{text}' is not a colour; use \"R,G,B\" with 0-255 each (e.g. \"243,233,212\") or \"#F3E9D4\".");
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);

    private static double Round(double value) => ToolContext.Round(value);

    private static JsonObject PointJson(double x, double y, AcadCopilot.Core.Drafting.DrawingUnits units) =>
        ToolContext.Point(new Autodesk.AutoCAD.Geometry.Point3d(x, y, 0), units);

    private static JsonObject AreaJson(double area, AcadCopilot.Core.Drafting.DrawingUnits units)
    {
        var node = new JsonObject { ["du2"] = Round(area) };
        if (units.InchesPerUnit is { } inches)
        {
            node["sqft"] = Math.Round(area * inches * inches / 144.0, 1);
        }

        return node;
    }

    private static JsonArray RectJson(PlanRect rect) => new(Round(rect.MinX), Round(rect.MinY), Round(rect.MaxX), Round(rect.MaxY));
}
