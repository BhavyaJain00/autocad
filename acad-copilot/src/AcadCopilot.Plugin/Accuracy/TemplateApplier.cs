using System.Globalization;
using System.IO;
using AcadCopilot.Core.Accuracy;
using AcadCopilot.Core.Tools;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadCopilot.Plugin.Accuracy;

/// <summary>What apply_drawing_template did.</summary>
internal sealed class TemplateReport
{
    public List<string> Created { get; } = [];

    public List<string> Updated { get; } = [];

    public List<string> Kept { get; } = [];

    public List<string> Warnings { get; } = [];

    public string? CurrentDimStyle { get; set; }
}

/// <summary>Options of apply_drawing_template.</summary>
/// <param name="Scale">"1/4" or "1/8".</param>
/// <param name="Overwrite">Update existing layers and styles.</param>
/// <param name="TitleBlock">Define the title block.</param>
internal sealed record TemplateOptions(string Scale, bool Overwrite, bool TitleBlock);

/// <summary>
/// Applies a <see cref="DrawingTemplate"/> (or the user's own DWT/DWG) to a drawing through the AutoCAD database API:
/// header variables, linetypes, layers, text styles, dimension styles and the title block. Main thread, inside the
/// caller's write transaction (document locked), so everything is one undo step and no dialog can open.
/// </summary>
internal static class TemplateApplier
{
    /// <summary>The built-in template: the bundle's Templates\AcadCopilot-Architectural.json when it is valid, else the data in code.</summary>
    public static (DrawingTemplate Template, string Source) BuiltIn(string pluginDirectory)
    {
        var file = Path.Combine(pluginDirectory, "Templates", DrawingTemplate.FileName);
        try
        {
            if (File.Exists(file) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) is System.Text.Json.Nodes.JsonObject json)
            {
                return (DrawingTemplate.FromJson(json), file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            Core.Diagnostics.CopilotLog.Warn($"Template {file} could not be read; the built-in data is used.", ex);
        }

        return (DrawingTemplate.Default, "built-in");
    }

    /// <summary>Applies <paramref name="template"/>.</summary>
    public static TemplateReport Apply(Database database, Transaction transaction, DrawingTemplate template, TemplateOptions options)
    {
        var report = new TemplateReport();
        ApplyUnits(database, template, options, report);
        ApplyLinetypes(database, transaction, template, report);
        ApplyLayers(database, transaction, template, options, report);
        var textStyles = ApplyTextStyles(database, transaction, template, options, report);
        ApplyDimStyles(database, transaction, template, options, textStyles, report);
        if (options.TitleBlock && template.TitleBlock is { } titleBlock)
        {
            ApplyTitleBlock(database, transaction, titleBlock, textStyles.GetValueOrDefault("ARCH"), options, report);
        }

        return report;
    }

    private static void ApplyUnits(Database database, DrawingTemplate template, TemplateOptions options, TemplateReport report)
    {
        foreach (var (name, value) in template.Units)
        {
            var v = (int)Math.Round(value);
            switch (name.ToUpperInvariant())
            {
                case "LUNITS":
                    database.Lunits = v;
                    break;
                case "LUPREC":
                    database.Luprec = v;
                    break;
                case "INSUNITS":
                    database.Insunits = (UnitsValue)v;
                    break;
                case "MEASUREMENT":
                    database.Measurement = v == 1 ? MeasurementValue.Metric : MeasurementValue.English;
                    break;
                case "AUNITS":
                    database.Aunits = v;
                    break;
                case "AUPREC":
                    database.Auprec = v;
                    break;
                case "LTSCALE":
                    database.Ltscale = DrawingTemplate.FactorFor(options.Scale) / 2;
                    break;
                case "PSLTSCALE":
                    database.Psltscale = v != 0;
                    break;
                case "MSLTSCALE":
                    database.MsLtScale = v != 0;
                    break;
                default:
                    report.Warnings.Add($"Unit variable {name} is not supported and was skipped.");
                    break;
            }
        }

        report.Updated.Add(string.Create(CultureInfo.InvariantCulture, $"units: LUNITS {database.Lunits}, LUPREC {database.Luprec}, INSUNITS {(int)database.Insunits}, LTSCALE {database.Ltscale}"));
    }

    private static void ApplyLinetypes(Database database, Transaction transaction, DrawingTemplate template, TemplateReport report)
    {
        var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        foreach (var name in template.Linetypes.Concat(template.Layers.Select(l => l.Linetype)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(name, "Continuous", StringComparison.OrdinalIgnoreCase) || table.Has(name))
            {
                continue;
            }

            try
            {
                database.LoadLineTypeFile(name, "acad.lin");
                report.Created.Add("linetype " + name);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                report.Warnings.Add($"Linetype {name} could not be loaded from acad.lin ({ex.ErrorStatus}).");
            }
        }
    }

    private static void ApplyLayers(Database database, Transaction transaction, DrawingTemplate template, TemplateOptions options, TemplateReport report)
    {
        var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var linetypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        foreach (var layer in template.Layers)
        {
            LayerTableRecord record;
            if (table.Has(layer.Name))
            {
                if (!options.Overwrite)
                {
                    report.Kept.Add("layer " + layer.Name);
                    continue;
                }

                record = (LayerTableRecord)transaction.GetObject(table[layer.Name], OpenMode.ForWrite);
                report.Updated.Add("layer " + layer.Name);
            }
            else
            {
                record = new LayerTableRecord { Name = layer.Name };
                if (!table.IsWriteEnabled)
                {
                    table.UpgradeOpen();
                }

                table.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
                report.Created.Add("layer " + layer.Name);
            }

            record.Color = Color.FromColorIndex(ColorMethod.ByAci, layer.Color);
            record.LineWeight = (LineWeight)(int)Math.Round(layer.Lineweight * 100);
            record.Description = layer.Description;
            record.IsPlottable = true;
            if (linetypes.Has(layer.Linetype))
            {
                record.LinetypeObjectId = linetypes[layer.Linetype];
            }
        }
    }

    private static Dictionary<string, ObjectId> ApplyTextStyles(Database database, Transaction transaction, DrawingTemplate template, TemplateOptions options, TemplateReport report)
    {
        var ids = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        foreach (var style in template.TextStyles)
        {
            TextStyleTableRecord record;
            if (table.Has(style.Name))
            {
                ids[style.Name] = table[style.Name];
                if (!options.Overwrite)
                {
                    report.Kept.Add("text style " + style.Name);
                    continue;
                }

                record = (TextStyleTableRecord)transaction.GetObject(table[style.Name], OpenMode.ForWrite);
                report.Updated.Add("text style " + style.Name);
            }
            else
            {
                record = new TextStyleTableRecord { Name = style.Name };
                if (!table.IsWriteEnabled)
                {
                    table.UpgradeOpen();
                }

                ids[style.Name] = table.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
                report.Created.Add("text style " + style.Name);
            }

            record.FileName = style.Font;
            record.TextSize = style.Height;
            record.XScale = style.WidthFactor <= 0 ? 1 : style.WidthFactor;
        }

        if (ids.TryGetValue("ARCH", out var arch))
        {
            database.Textstyle = arch;
        }

        return ids;
    }

    private static void ApplyDimStyles(Database database, Transaction transaction, DrawingTemplate template, TemplateOptions options, Dictionary<string, ObjectId> textStyles, TemplateReport report)
    {
        var table = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
        var wanted = DrawingTemplate.DimStyleFor(options.Scale);
        foreach (var style in template.DimStyles)
        {
            DimStyleTableRecord record;
            if (table.Has(style.Name))
            {
                if (!options.Overwrite)
                {
                    report.Kept.Add("dimension style " + style.Name);
                    continue;
                }

                record = (DimStyleTableRecord)transaction.GetObject(table[style.Name], OpenMode.ForWrite);
                report.Updated.Add("dimension style " + style.Name);
            }
            else
            {
                record = new DimStyleTableRecord();
                using (var current = database.GetDimstyleData())
                {
                    record.CopyFrom(current);
                }

                record.Name = style.Name;
                if (!table.IsWriteEnabled)
                {
                    table.UpgradeOpen();
                }

                table.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
                report.Created.Add("dimension style " + style.Name);
            }

            foreach (var (name, value) in style.Variables)
            {
                if (!SetDimVariable(record, name, value))
                {
                    report.Warnings.Add($"{style.Name}: {name} is not supported and was skipped.");
                }
            }

            if (textStyles.TryGetValue(style.TextStyle, out var textStyle) || TryGet(transaction, database.TextStyleTableId, style.TextStyle, out textStyle))
            {
                record.Dimtxsty = textStyle;
            }
        }

        if (table.Has(wanted))
        {
            var id = table[wanted];
            database.Dimstyle = id;
            database.SetDimstyleData((DimStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead));
            report.CurrentDimStyle = wanted;
        }
    }

    private static bool TryGet(Transaction transaction, ObjectId tableId, string name, out ObjectId id)
    {
        var table = (SymbolTable)transaction.GetObject(tableId, OpenMode.ForRead);
        id = table.Has(name) ? table[name] : ObjectId.Null;
        return !id.IsNull;
    }

    private static bool SetDimVariable(DimStyleTableRecord record, string name, double value)
    {
        var i = (int)Math.Round(value);
        switch (name.ToUpperInvariant())
        {
            case "DIMSCALE": record.Dimscale = value; return true;
            case "DIMLUNIT": record.Dimlunit = i; return true;
            case "DIMDEC": record.Dimdec = i; return true;
            case "DIMFRAC": record.Dimfrac = i; return true;
            case "DIMZIN": record.Dimzin = i; return true;
            case "DIMTSZ": record.Dimtsz = value; return true;
            case "DIMASZ": record.Dimasz = value; return true;
            case "DIMTXT": record.Dimtxt = value; return true;
            case "DIMTAD": record.Dimtad = i; return true;
            case "DIMTIH": record.Dimtih = i != 0; return true;
            case "DIMTOH": record.Dimtoh = i != 0; return true;
            case "DIMEXO": record.Dimexo = value; return true;
            case "DIMEXE": record.Dimexe = value; return true;
            case "DIMDLE": record.Dimdle = value; return true;
            case "DIMGAP": record.Dimgap = value; return true;
            case "DIMATFIT": record.Dimatfit = i; return true;
            case "DIMTMOVE": record.Dimtmove = i; return true;
            case "DIMJUST": record.Dimjust = i; return true;
            case "DIMADEC": record.Dimadec = i; return true;
            case "DIMRND": record.Dimrnd = value; return true;
            case "DIMTOFL": record.Dimtofl = i != 0; return true;
            case "DIMSAH": record.Dimsah = i != 0; return true;
            default: return false;
        }
    }

    private static void ApplyTitleBlock(Database database, Transaction transaction, TemplateTitleBlock titleBlock, ObjectId textStyle, TemplateOptions options, TemplateReport report)
    {
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        BlockTableRecord record;
        if (blocks.Has(titleBlock.Name))
        {
            if (!options.Overwrite)
            {
                report.Kept.Add("block " + titleBlock.Name);
                return;
            }

            record = (BlockTableRecord)transaction.GetObject(blocks[titleBlock.Name], OpenMode.ForWrite);
            foreach (ObjectId id in record)
            {
                transaction.GetObject(id, OpenMode.ForWrite).Erase();
            }

            report.Updated.Add("block " + titleBlock.Name);
        }
        else
        {
            record = new BlockTableRecord { Name = titleBlock.Name, Origin = Point3d.Origin, Units = UnitsValue.Inches, Comments = "AI Copilot title block (paper inches; insert at 48 for 1/4\" = 1'-0\", 96 for 1/8\")" };
            blocks.UpgradeOpen();
            blocks.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            report.Created.Add("block " + titleBlock.Name);
        }

        void Append(Entity entity)
        {
            entity.Layer = "0";
            record.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        foreach (var line in titleBlock.Lines)
        {
            Append(new Line(new Point3d(line.X1, line.Y1, 0), new Point3d(line.X2, line.Y2, 0)));
        }

        foreach (var text in titleBlock.Texts)
        {
            var dbText = new DBText { Position = new Point3d(text.X, text.Y, 0), Height = text.Height, TextString = text.Text };
            if (!textStyle.IsNull)
            {
                dbText.TextStyleId = textStyle;
            }

            Append(dbText);
        }

        foreach (var attribute in titleBlock.Attributes)
        {
            var definition = new AttributeDefinition
            {
                Position = new Point3d(attribute.X, attribute.Y, 0),
                Height = attribute.Height,
                Tag = attribute.Tag,
                Prompt = attribute.Prompt,
                TextString = attribute.Default,
                LockPositionInBlock = true,
            };
            if (!textStyle.IsNull)
            {
                definition.TextStyleId = textStyle;
            }

            Append(definition);
        }
    }

    /// <summary>
    /// Imports the user's own template drawing (.dwt / .dwg): its units, linetypes, layers, text styles, dimension styles
    /// and named blocks, keeping (or with overwrite replacing) what the drawing already has.
    /// </summary>
    public static TemplateReport Import(Database database, Transaction transaction, string path, TemplateOptions options)
    {
        var report = new TemplateReport();
        using var source = new Database(false, true);
        try
        {
            source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
            source.CloseInput(true);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new ToolException($"The template {path} could not be read ({ex.ErrorStatus}). Check the file, or use the built-in template (use_built_in).");
        }

        database.Lunits = source.Lunits;
        database.Luprec = source.Luprec;
        database.Insunits = source.Insunits;
        database.Measurement = source.Measurement;
        database.Aunits = source.Aunits;
        database.Auprec = source.Auprec;
        database.Ltscale = source.Ltscale;
        report.Updated.Add(string.Create(CultureInfo.InvariantCulture, $"units from the template: LUNITS {source.Lunits}, INSUNITS {(int)source.Insunits}, LTSCALE {source.Ltscale}"));

        var cloning = options.Overwrite ? DuplicateRecordCloning.Replace : DuplicateRecordCloning.Ignore;
        string? currentDim = null;
        using (var read = source.TransactionManager.StartOpenCloseTransaction())
        {
            Clone(read, source, source.LinetypeTableId, database.LinetypeTableId, "linetype", cloning, report, transaction);
            Clone(read, source, source.TextStyleTableId, database.TextStyleTableId, "text style", cloning, report, transaction);
            Clone(read, source, source.LayerTableId, database.LayerTableId, "layer", cloning, report, transaction);
            Clone(read, source, source.DimStyleTableId, database.DimStyleTableId, "dimension style", cloning, report, transaction);
            Clone(read, source, source.BlockTableId, database.BlockTableId, "block", cloning, report, transaction);
            if (read.GetObject(source.Dimstyle, OpenMode.ForRead) is DimStyleTableRecord dim)
            {
                currentDim = dim.Name;
            }

            read.Commit();
        }

        if (currentDim is not null && TryGet(transaction, database.DimStyleTableId, currentDim, out var dimId))
        {
            database.Dimstyle = dimId;
            database.SetDimstyleData((DimStyleTableRecord)transaction.GetObject(dimId, OpenMode.ForRead));
            report.CurrentDimStyle = currentDim;
        }

        return report;
    }

    private static void Clone(Transaction read, Database source, ObjectId sourceTable, ObjectId targetTable, string kind, DuplicateRecordCloning cloning, TemplateReport report, Transaction write)
    {
        var existing = (SymbolTable)write.GetObject(targetTable, OpenMode.ForRead);
        var ids = new ObjectIdCollection();
        var names = new List<(string Name, bool Exists)>();
        foreach (ObjectId id in (SymbolTable)read.GetObject(sourceTable, OpenMode.ForRead))
        {
            if (read.GetObject(id, OpenMode.ForRead) is not SymbolTableRecord record || record.IsDependent)
            {
                continue;
            }

            if (record is BlockTableRecord block && (block.IsLayout || block.IsAnonymous || block.IsFromExternalReference))
            {
                continue;
            }

            if (record is LayerTableRecord && (record.Name == "0" || string.Equals(record.Name, "Defpoints", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (record is LinetypeTableRecord && record.Name is "ByLayer" or "ByBlock" or "Continuous")
            {
                continue;
            }

            ids.Add(id);
            names.Add((record.Name, existing.Has(record.Name)));
        }

        if (ids.Count == 0)
        {
            return;
        }

        using var mapping = new IdMapping();
        source.WblockCloneObjects(ids, targetTable, mapping, cloning, false);
        foreach (var (name, exists) in names)
        {
            (exists ? (cloning == DuplicateRecordCloning.Replace ? report.Updated : report.Kept) : report.Created).Add(kind + " " + name);
        }
    }
}
