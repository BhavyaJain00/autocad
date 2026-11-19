using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Inspect;

/// <summary>Parsed filters of list_entities.</summary>
/// <param name="Layer">Layer name pattern, or null for all layers.</param>
/// <param name="Type">DXF type pattern, or null for all types.</param>
/// <param name="Window">Window in WCS, or null.</param>
/// <param name="Inside">Only entities completely inside <paramref name="Window"/>.</param>
/// <param name="CurrentSpace">Read the current layout instead of model space.</param>
/// <param name="Limit">Page size.</param>
/// <param name="Offset">Matches to skip.</param>
public sealed record EntityFilter(NamePattern? Layer, NamePattern? Type, PlanBox? Window, bool Inside, bool CurrentSpace, int Limit, int Offset);

/// <summary>Entity count and extents of a drawing space, optionally with how many entities touch an area.</summary>
/// <param name="Total">Entities in the space.</param>
/// <param name="InArea">Entities whose bounding box touches the area (null when not counted).</param>
/// <param name="Extents">XY extents of the visible entities (null when there are none).</param>
/// <param name="Approximate">True when the drawing was too large to measure and EXTMIN/EXTMAX were used.</param>
public sealed record SpaceScan(int Total, int? InArea, PlanBox? Extents, bool Approximate);

/// <summary>The read-only queries behind the inspection tools. Main thread only, inside a read transaction.</summary>
public static class InspectQueries
{
    /// <summary>Above this many entities a space is not measured entity by entity.</summary>
    public const int MaxMeasuredEntities = 50_000;

    /// <summary>
    /// Counts the entities of <paramref name="spaceId"/>, measures their XY extents (visible entities with bounds) and
    /// counts those touching <paramref name="area"/>.
    /// </summary>
    public static SpaceScan ScanSpace(Database database, Transaction transaction, ObjectId spaceId, PlanBox? area)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
        var ids = new List<ObjectId>();
        foreach (ObjectId id in space)
        {
            if (!id.IsErased)
            {
                ids.Add(id);
            }
        }

        if (ids.Count > MaxMeasuredEntities)
        {
            var header = new PlanBox(database.Extmin.X, database.Extmin.Y, database.Extmax.X, database.Extmax.Y);
            return new SpaceScan(ids.Count, null, header.IsValid ? header : null, Approximate: true);
        }

        PlanBox? extents = null;
        var inArea = 0;
        foreach (var id in ids)
        {
            if (transaction.GetObject(id, OpenMode.ForRead, false) is not Entity { Visible: true } entity || EntitySummary.PlanBounds(entity) is not { } box)
            {
                continue;
            }

            extents = extents is { } current ? current.Union(box) : box;
            if (area is { } a && a.Intersects(box))
            {
                inArea++;
            }
        }

        return new SpaceScan(ids.Count, area is null ? null : inArea, extents, Approximate: false);
    }

    /// <summary>list_layers: every layer (or those matching <paramref name="name"/>) with state, style and entity count.</summary>
    public static JsonObject ListLayers(Document document, Transaction transaction, NamePattern? name, bool counts)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(transaction);
        var database = document.Database;
        var perLayer = counts ? CountEntitiesByLayer(database, transaction) : null;
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var layers = new List<LayerTableRecord>();
        var total = 0;
        foreach (ObjectId id in layerTable)
        {
            if (id.IsErased || transaction.GetObject(id, OpenMode.ForRead) is not LayerTableRecord layer)
            {
                continue;
            }

            total++;
            if (name is null || name.IsMatch(layer.Name))
            {
                layers.Add(layer);
            }
        }

        var items = new JsonArray();
        foreach (var layer in layers
                     .OrderBy(l => l.Name == "0" ? 0 : 1)
                     .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new JsonObject
            {
                ["name"] = layer.Name,
                ["on"] = !layer.IsOff,
                ["frozen"] = layer.IsFrozen,
                ["locked"] = layer.IsLocked,
                ["color"] = EntitySummary.ColorText(layer.Color),
                ["linetype"] = LinetypeName(layer.LinetypeObjectId, transaction),
                ["lineweight"] = EntitySummary.LineweightText(layer.LineWeight),
                ["plot"] = layer.IsPlottable,
            };
            if (perLayer is not null)
            {
                item["entities"] = perLayer.TryGetValue(layer.ObjectId, out var count) ? count : 0;
            }

            if (layer.ObjectId == database.Clayer)
            {
                item["current"] = true;
            }

            if (layer.IsDependent)
            {
                item["xref"] = true;
            }

            if (!string.IsNullOrWhiteSpace(layer.Description))
            {
                item["description"] = layer.Description;
            }

            items.Add(item);
        }

        var current = (LayerTableRecord)transaction.GetObject(database.Clayer, OpenMode.ForRead);
        var result = new JsonObject
        {
            ["current"] = current.Name,
            ["total"] = total,
            ["returned"] = items.Count,
            ["layers"] = items,
        };
        if (perLayer is not null)
        {
            result["countsCover"] = "model space and all layouts";
        }

        return result;
    }

    /// <summary>list_entities: filtered, paged compact summaries plus total and counts by type of all matches.</summary>
    public static JsonObject ListEntities(Document document, Transaction transaction, EntityFilter filter)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(filter);
        var database = document.Database;
        var units = ToolContext.GetUnits(database);
        var spaceId = filter.CurrentSpace ? database.CurrentSpaceId : SymbolUtilityServices.GetBlockModelSpaceId(database);
        var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
        var page = new JsonArray();
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (ObjectId id in space)
        {
            if (id.IsErased)
            {
                continue;
            }

            var rxClass = id.ObjectClass;
            var type = string.IsNullOrEmpty(rxClass.DxfName) ? rxClass.Name : rxClass.DxfName;
            if (filter.Type is not null && !filter.Type.IsMatch(type))
            {
                continue;
            }

            if (transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (filter.Layer is not null && !filter.Layer.IsMatch(entity.Layer))
            {
                continue;
            }

            if (filter.Window is { } window)
            {
                if (EntitySummary.PlanBounds(entity) is not { } box || !(filter.Inside ? window.Contains(box) : window.Intersects(box)))
                {
                    continue;
                }
            }

            total++;
            byType[type] = byType.TryGetValue(type, out var n) ? n + 1 : 1;
            if (total > filter.Offset && page.Count < filter.Limit)
            {
                page.Add(EntitySummary.Summarize(entity, units));
            }
        }

        var counts = new JsonObject();
        foreach (var (type, count) in byType.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            counts[type] = count;
        }

        var result = new JsonObject
        {
            ["space"] = filter.CurrentSpace ? SpaceLabel(database, transaction, spaceId) : "model",
            ["total"] = total,
            ["offset"] = filter.Offset,
            ["returned"] = page.Count,
            ["countsByType"] = counts,
            ["entities"] = page,
        };
        if (filter.Offset + page.Count < total)
        {
            result["nextOffset"] = filter.Offset + page.Count;
        }

        return result;
    }

    /// <summary>get_entity: full descriptions of the entities with these handles; unknown handles are listed separately.</summary>
    public static (JsonArray Found, List<string> Problems) GetEntities(Document document, Transaction transaction, IReadOnlyList<string> handles)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(handles);
        var database = document.Database;
        var units = ToolContext.GetUnits(database);
        var found = new JsonArray();
        var problems = new List<string>();
        foreach (var handle in handles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ObjectId id;
            try
            {
                id = ToolContext.GetObjectId(database, handle);
            }
            catch (AcadCopilot.Core.Tools.ToolException ex)
            {
                problems.Add(ex.Message);
                continue;
            }

            if (transaction.GetObject(id, OpenMode.ForRead, false) is Entity entity)
            {
                found.Add(EntitySummary.Describe(entity, transaction, units));
            }
            else
            {
                problems.Add($"Handle {handle} is not a drawing entity (it is a {id.ObjectClass.Name}).");
            }
        }

        return (found, problems);
    }

    /// <summary>get_sysvars: values of the named system variables; unknown names are listed under "unknown".</summary>
    public static JsonObject GetSysvars(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var values = new JsonObject();
        var unknown = new JsonArray();
        foreach (var raw in names)
        {
            var name = raw.Trim().ToUpperInvariant();
            if (values.ContainsKey(name))
            {
                continue;
            }

            try
            {
                values[name] = SysvarValue(AcadApp.GetSystemVariable(name));
            }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException)
            {
                unknown.Add(name);
            }
        }

        var result = new JsonObject { ["values"] = values };
        if (unknown.Count > 0)
        {
            result["unknown"] = unknown;
        }

        return result;
    }

    /// <summary>Converts a system variable value to JSON (numbers, text, points as arrays).</summary>
    public static JsonNode? SysvarValue(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag,
        double number => ToolContext.Round(number),
        float number => ToolContext.Round(number),
        short or int or long or byte or ushort or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        Point3d point => new JsonArray(ToolContext.Round(point.X), ToolContext.Round(point.Y), ToolContext.Round(point.Z)),
        Point2d point => new JsonArray(ToolContext.Round(point.X), ToolContext.Round(point.Y)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>JSON for a <see cref="SpaceScan"/>: entity count and extents (box with feet-inches) or empty.</summary>
    public static JsonObject ScanJson(SpaceScan scan, DrawingUnits units)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var json = new JsonObject { ["entities"] = scan.Total };
        if (scan.InArea is { } inArea)
        {
            json["entitiesInPicture"] = inArea;
        }

        if (scan.Extents is { } extents)
        {
            json["extents"] = ViewFacts.BoxJson(extents, units);
            if (scan.Approximate)
            {
                json["extentsApproximate"] = true;
            }
        }
        else
        {
            json["extents"] = new JsonObject { ["empty"] = true };
        }

        return json;
    }

    private static Dictionary<ObjectId, int> CountEntitiesByLayer(Database database, Transaction transaction)
    {
        var counts = new Dictionary<ObjectId, int>();
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in blockTable)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (!block.IsLayout)
            {
                continue;
            }

            foreach (ObjectId id in block)
            {
                if (!id.IsErased && transaction.GetObject(id, OpenMode.ForRead, false) is Entity entity)
                {
                    counts[entity.LayerId] = counts.TryGetValue(entity.LayerId, out var n) ? n + 1 : 1;
                }
            }
        }

        return counts;
    }

    private static string LinetypeName(ObjectId id, Transaction transaction)
    {
        if (id.IsNull || id.IsErased)
        {
            return "?";
        }

        return transaction.GetObject(id, OpenMode.ForRead) is LinetypeTableRecord linetype ? linetype.Name : "?";
    }

    private static string SpaceLabel(Database database, Transaction transaction, ObjectId spaceId)
    {
        if (spaceId == SymbolUtilityServices.GetBlockModelSpaceId(database))
        {
            return "model";
        }

        var block = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
        return block.IsLayout && transaction.GetObject(block.LayoutId, OpenMode.ForRead) is Layout layout
            ? "paper:" + layout.LayoutName
            : block.Name;
    }
}
