using AcadCopilot.Core.Accuracy;
using AcadCopilot.Core.Sheets;
using AcadCopilot.Core.Tools;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadCopilot.Plugin.Sheets;

/// <summary>What create_sheets was asked.</summary>
internal sealed record SheetsRequest(
    PaperSize Paper,
    TitleBlockValues Values,
    IReadOnlyList<int>? Drawings,
    IReadOnlyList<DrawingLabel> Labels,
    bool Combine,
    DrawingScale? Scale,
    double? ClusterGap,
    bool ReplaceExisting);

/// <summary>One sheet made.</summary>
/// <param name="Spec">The planned sheet.</param>
/// <param name="LayoutName">The layout's actual name (" (2)" added when the name was taken).</param>
/// <param name="Viewports">Its viewports.</param>
/// <param name="Media">The plotter paper chosen.</param>
/// <param name="Replaced">True when a layout of that name was deleted first.</param>
internal sealed record BuiltSheet(SheetSpec Spec, string LayoutName, IReadOnlyList<ObjectId> Viewports, string Media, bool Replaced);

/// <summary>The result of create_sheets.</summary>
internal sealed record SheetsOutcome(
    SheetSetPlan Plan,
    IReadOnlyList<BuiltSheet> Sheets,
    string BlockName,
    bool BlockCreated,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ObjectId> Created,
    IReadOnlyList<ObjectId> Modified);

/// <summary>
/// create_sheets in AutoCAD: plans the sheets (Core <see cref="SheetPlanner"/>), then for each one creates a layout
/// (LayoutManager.CreateLayout), sets DWG To PDF.pc3 with the matching paper at 1:1 in millimetres
/// (PlotSettingsValidator), inserts the title block with its attributes and adds locked viewports at the planned scale.
/// Main thread, inside the caller's write transaction (document locked).
/// </summary>
internal static class SheetBuilder
{
    /// <summary>Builds the sheets.</summary>
    public static SheetsOutcome Build(Database database, Transaction transaction, SheetsRequest request)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        var found = DrawingFinder.Find(database, transaction, request.ClusterGap, request.Labels);
        var warnings = new List<string>(found.Warnings);
        warnings.AddRange(found.UnmatchedLabels.Select(l => $"Label \"{l.Title}\" is not inside or near any drawing."));
        var skipped = new List<string>();
        List<DrawingCluster> chosen;
        if (request.Drawings is { Count: > 0 } numbers)
        {
            chosen = [.. found.Clusters.Where(c => numbers.Contains(c.Number))];
            skipped.AddRange(numbers.Where(n => found.Clusters.All(c => c.Number != n)).Select(n => $"drawing {n} does not exist"));
        }
        else
        {
            chosen = [.. found.Clusters.Where(c => c.Title is not null)];
            skipped.AddRange(found.Clusters.Where(c => c.Title is null).Select(c => $"DRAWING {c.Number} at {c.Box} (untitled: name it with labels or list it in drawings)"));
        }

        if (chosen.Count == 0)
        {
            throw new ToolException(found.Clusters.Count == 0
                ? "Model space is empty: there is nothing to put on sheets."
                : "No titled drawing found. Run arrange_drawings with labels to name the drawings, or pass drawings (numbers) or labels here.");
        }

        // On ISO A3 the title block of apply_drawing_template (accuracy pack) is used when the drawing already has it.
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var template = DrawingTemplate.Default.TitleBlock;
        var external = request.Paper.Code == "ISO-A3" && template is not null && blockTable.Has(template.Name)
            ? TitleBlockDesign.FromTemplate(template)
            : null;
        var options = new SheetOptions
        {
            Paper = request.Paper,
            System = found.System,
            InchesPerUnit = found.InchesPerUnit,
            Combine = request.Combine,
            Scale = request.Scale,
            TitleBlock = external,
        };
        var drawings = chosen.Select(c => new SheetDrawing(c.Number, c.Kind, c.Order, c.Name, c.Box)).ToList();
        var plan = SheetPlanner.Plan(drawings, options);
        warnings.AddRange(plan.Warnings);

        var validator = PlotSettingsValidator.Current;
        var devices = validator.GetPlotDeviceList().Cast<string>().ToList();
        if (!devices.Contains(PaperSizes.PdfDevice, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolException($"The plotter \"{PaperSizes.PdfDevice}\" is not installed in this AutoCAD, so no PDF sheets can be set up. Ask the user to repair the plotter configurations (PLOTTERMANAGER).");
        }

        var created = new List<ObjectId>();
        var modified = new List<ObjectId>();
        EnsureLayer(database, transaction, TitleBlockDesign.Layer, 7, plottable: true);
        EnsureLayer(database, transaction, TitleBlockDesign.ViewportLayer, 8, plottable: false);
        var (blockId, blockCreated) = external is null
            ? EnsureTitleBlock(database, transaction, plan.TitleBlock)
            : (blockTable[external.BlockName], false);
        var baseValues = external is not null && request.Values.Client is { Length: > 0 } client && client != "-"
            ? request.Values with { Project = request.Values.Project + " / " + client }
            : request.Values;

        var manager = LayoutManager.Current;
        var existing = LayoutNames(database, transaction);
        var built = new List<BuiltSheet>();
        var byNumber = found.Clusters.ToDictionary(c => c.Number);
        using var unlocked = new LayerUnlocker(database, transaction);
        foreach (var sheet in plan.Sheets)
        {
            var name = sheet.LayoutName;
            var replaced = false;
            if (existing.Contains(name))
            {
                if (request.ReplaceExisting && !string.Equals(manager.CurrentLayout, name, StringComparison.OrdinalIgnoreCase))
                {
                    manager.DeleteLayout(name);
                    existing.Remove(name);
                    replaced = true;
                }
                else
                {
                    if (request.ReplaceExisting)
                    {
                        warnings.Add($"\"{name}\" is the current layout, so it was kept; switch to Model (SETVAR CTAB Model) to replace it.");
                    }

                    name = SheetNames.Unique(name, existing);
                }
            }

            var layoutId = manager.CreateLayout(name);
            existing.Add(name);
            var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForWrite);
            InitializeWithoutViewports(layout, transaction);
            var (media, rotated) = ConfigurePlot(layout, request.Paper, warnings);
            var origin = PaperOrigin(layout, rotated);
            var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

            var values = baseValues with { Title = sheet.Title, SheetNumber = sheet.Number, Scale = sheet.ScaleText };
            created.Add(InsertTitleBlock(database, transaction, space, blockId, plan.TitleBlock, values, origin));

            var viewports = new List<ObjectId>();
            foreach (var view in sheet.Viewports)
            {
                var id = AddViewport(database, transaction, space, view, origin);
                viewports.Add(id);
                created.Add(id);

                // Keep the model-space scale line in step with the sheet.
                if (byNumber.TryGetValue(view.DrawingNumber, out var cluster) && cluster.ScaleItem is { } item &&
                    DrawingTitles.Clean(found.Scan.Items[item].Text) != DrawingTitles.Clean(view.Scale.ScaleLine))
                {
                    unlocked.Unlock(found.Scan.Layers[item]);
                    if (DrawingFinder.SetText(transaction, found.Scan.Ids[item], view.Scale.ScaleLine))
                    {
                        modified.Add(found.Scan.Ids[item]);
                    }
                }

                if (view.Cropped)
                {
                    warnings.Add($"{view.Title} on {name} is cropped: it does not fit at {view.Scale.Text}.");
                }
            }

            built.Add(new BuiltSheet(sheet, name, viewports, media, replaced));
        }

        return new SheetsOutcome(plan, built, plan.TitleBlock.BlockName, blockCreated, skipped, warnings, created, modified);
    }

    /// <summary>The names of the paper-space layouts.</summary>
    public static HashSet<string> LayoutNames(Database database, Transaction transaction)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary)
        {
            names.Add(entry.Key);
        }

        return names;
    }

    // A new layout gets its overall paper-space viewport; any default floating viewport (LAYOUTCREATEVIEWPORT) is erased.
    private static void InitializeWithoutViewports(Layout layout, Transaction transaction)
    {
        try
        {
            layout.Initialize();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Already initialized, or AutoCAD initializes it when the tab is first opened.
        }

        var ids = layout.GetViewports();
        for (var i = 1; i < ids.Count; i++)
        {
            if (transaction.GetObject(ids[i], OpenMode.ForWrite) is Viewport viewport)
            {
                viewport.Erase();
            }
        }
    }

    // DWG To PDF.pc3, the paper of the request (full bleed preferred), millimetres, 1:1, plot the layout from 0,0.
    private static (string Media, bool Rotated) ConfigurePlot(Layout layout, PaperSize paper, List<string> warnings)
    {
        var validator = PlotSettingsValidator.Current;
        validator.SetPlotConfigurationName(layout, PaperSizes.PdfDevice, null);
        validator.RefreshLists(layout);
        var names = validator.GetCanonicalMediaNameList(layout).Cast<string>().ToList();
        var pick = PaperSizes.PickMedia(names, paper);
        bool rotated;
        if (pick is { } p)
        {
            validator.SetCanonicalMediaName(layout, p.Name);
            rotated = p.Rotated;
        }
        else
        {
            validator.SetClosestMediaName(layout, paper.WidthMm, paper.HeightMm, PlotPaperUnit.Millimeters, true);
            rotated = layout.PlotPaperSize.X < layout.PlotPaperSize.Y;
            warnings.Add($"No exact {paper.Name} paper on {PaperSizes.PdfDevice}; using {layout.CanonicalMediaName}.");
        }

        validator.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters);
        validator.SetPlotRotation(layout, rotated ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
        validator.SetPlotType(layout, PlotType.Layout);
        validator.SetUseStandardScale(layout, true);
        validator.SetStdScaleType(layout, StdScaleType.StdScale1To1);
        validator.SetPlotCentered(layout, false);
        validator.SetPlotOrigin(layout, new Point2d(0, 0));
        try
        {
            var styles = validator.GetPlotStyleSheetList().Cast<string>().ToList();
            var wanted = layout.Database.PlotStyleMode ? "monochrome.ctb" : "monochrome.stb";
            if (styles.Contains(wanted, StringComparer.OrdinalIgnoreCase))
            {
                validator.SetCurrentStyleSheet(layout, wanted);
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Keep the default plot style table.
        }

        return (layout.CanonicalMediaName, rotated);
    }

    // Where the paper's lower-left corner is in layout coordinates: (0,0) is the corner of the printable area, so the
    // paper starts at minus the margins (zero for full-bleed paper).
    private static Vector3d PaperOrigin(Layout layout, bool rotated)
    {
        var margins = layout.PlotPaperMargins;
        return rotated
            ? new Vector3d(-margins.MinPoint.Y, -margins.MinPoint.X, 0)
            : new Vector3d(-margins.MinPoint.X, -margins.MinPoint.Y, 0);
    }

    private static ObjectId InsertTitleBlock(Database database, Transaction transaction, BlockTableRecord space, ObjectId blockId, TitleBlockDesign design, TitleBlockValues values, Vector3d origin)
    {
        var reference = new BlockReference(Point3d.Origin + origin, blockId);
        reference.SetDatabaseDefaults(database);
        reference.ScaleFactors = new Scale3d(design.InsertScale);
        reference.Layer = TitleBlockDesign.Layer;
        var id = space.AppendEntity(reference);
        transaction.AddNewlyCreatedDBObject(reference, true);

        var byTag = values.ByTag;
        var definition = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
        foreach (ObjectId childId in definition)
        {
            if (transaction.GetObject(childId, OpenMode.ForRead) is not AttributeDefinition attribute || attribute.Constant)
            {
                continue;
            }

            var value = byTag.TryGetValue(attribute.Tag, out var v) ? v : attribute.TextString;
            var attributeReference = new AttributeReference();
            attributeReference.SetAttributeFromBlock(attribute, reference.BlockTransform);
            attributeReference.TextString = value;
            if (design.Attributes.FirstOrDefault(a => a.Tag == attribute.Tag) is { } field)
            {
                attributeReference.Height = TitleBlockDesign.FitHeight(field, value);
            }

            reference.AttributeCollection.AppendAttribute(attributeReference);
            transaction.AddNewlyCreatedDBObject(attributeReference, true);
        }

        return id;
    }

    private static ObjectId AddViewport(Database database, Transaction transaction, BlockTableRecord space, SheetViewport view, Vector3d origin)
    {
        var viewport = new Viewport();
        viewport.SetDatabaseDefaults(database);
        viewport.Layer = TitleBlockDesign.ViewportLayer;
        viewport.CenterPoint = new Point3d(view.Paper.CenterX, view.Paper.CenterY, 0) + origin;
        viewport.Width = view.Paper.Width;
        viewport.Height = view.Paper.Height;
        var id = space.AppendEntity(viewport);
        transaction.AddNewlyCreatedDBObject(viewport, true);

        // Plan view of model space, centred on the drawing, at the sheet scale (paper mm per drawing unit).
        viewport.ViewDirection = Vector3d.ZAxis;
        viewport.ViewTarget = Point3d.Origin;
        viewport.TwistAngle = 0;
        viewport.ViewCenter = new Point2d(view.Model.CenterX, view.Model.CenterY);
        viewport.ViewHeight = view.Model.Height;
        viewport.CustomScale = view.Paper.Height / view.Model.Height;
        try
        {
            viewport.On = true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Turned on when the layout is first opened.
        }

        viewport.Locked = true;
        return id;
    }

    private static void EnsureLayer(Database database, Transaction transaction, string name, short color, bool plottable)
    {
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (layers.Has(name))
        {
            return;
        }

        var record = new LayerTableRecord
        {
            Name = name,
            Color = Color.FromColorIndex(ColorMethod.ByAci, color),
            IsPlottable = plottable,
            Description = plottable ? "Sheet frames and title blocks (AI Copilot)" : "Sheet viewports, not plotted (AI Copilot)",
        };
        layers.UpgradeOpen();
        layers.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    // The title block block definition A-TTLB-<paper>, created once per paper size (an existing one is reused).
    private static (ObjectId Id, bool Created) EnsureTitleBlock(Database database, Transaction transaction, TitleBlockDesign design)
    {
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        if (blocks.Has(design.BlockName))
        {
            return (blocks[design.BlockName], false);
        }

        var style = DrawingFinder.StyleOrCurrent(database, transaction, "ARCH");
        var block = new BlockTableRecord { Name = design.BlockName, Origin = Point3d.Origin, Comments = "Sheet frame and title block (AI Copilot create_sheets)" };
        blocks.UpgradeOpen();
        var id = blocks.Add(block);
        transaction.AddNewlyCreatedDBObject(block, true);

        void Add(Entity entity)
        {
            entity.SetDatabaseDefaults(database);
            entity.Layer = "0";
            block.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        foreach (var line in design.Lines)
        {
            var entity = new Line(new Point3d(line.X0, line.Y0, 0), new Point3d(line.X1, line.Y1, 0));
            Add(entity);
            entity.LineWeight = (LineWeight)line.Weight;
        }

        foreach (var text in design.Texts)
        {
            var entity = new DBText { Position = new Point3d(text.X, text.Y, 0), Height = text.Height, TextString = text.Text };
            Add(entity);
            entity.TextStyleId = style;
            entity.Height = text.Height;
        }

        foreach (var field in design.Attributes)
        {
            var attribute = new AttributeDefinition
            {
                Position = new Point3d(field.X, field.Y, 0),
                Height = field.Height,
                Tag = field.Tag,
                Prompt = field.Prompt,
                TextString = field.Default,
                LockPositionInBlock = true,
            };
            Add(attribute);
            attribute.TextStyleId = style;
            attribute.Height = field.Height;
        }

        return (id, true);
    }
}
