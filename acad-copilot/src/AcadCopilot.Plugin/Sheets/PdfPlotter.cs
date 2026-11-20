using AcadCopilot.Core.Sheets;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Sheets;

/// <summary>The result of plot_to_pdf.</summary>
/// <param name="Path">The PDF written.</param>
/// <param name="Sheets">The layouts plotted, in page order.</param>
/// <param name="Notes">What was adjusted (layouts switched to DWG To PDF for this plot).</param>
internal sealed record PlotOutcome(string Path, IReadOnlyList<string> Sheets, IReadOnlyList<string> Notes);

/// <summary>
/// plot_to_pdf in AutoCAD: plots layouts into one multi-page PDF with the publish engine
/// (PlotFactory.CreatePublishEngine, one BeginDocument, a page per layout) on DWG To PDF.pc3, with BACKGROUNDPLOT 0
/// during the plot (restored after) and the current layout restored. Main thread, application context.
/// </summary>
internal static class PdfPlotter
{
    /// <summary>The layouts plot_to_pdf would plot: <paramref name="requested"/>, else the create_sheets sheets, else all, in tab order.</summary>
    public static List<(string Name, ObjectId Id)> ChooseLayouts(Database database, Transaction transaction, IReadOnlyList<string>? requested)
    {
        var layouts = new List<(string Name, ObjectId Id, int Tab)>();
        var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary)
        {
            var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
            if (!layout.ModelType)
            {
                layouts.Add((layout.LayoutName, entry.Value, layout.TabOrder));
            }
        }

        layouts.Sort((a, b) => a.Tab.CompareTo(b.Tab));
        if (requested is { Count: > 0 })
        {
            var result = new List<(string, ObjectId)>();
            foreach (var name in requested)
            {
                var match = layouts.FirstOrDefault(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                {
                    throw new ToolException($"There is no layout \"{name}\". Layouts: {string.Join(", ", layouts.Select(l => l.Name))}.");
                }

                result.Add((match.Name, match.Id));
            }

            return result;
        }

        var sheets = layouts.Where(l => SheetNames.IsSheetName(l.Name)).ToList();
        return [.. (sheets.Count > 0 ? sheets : layouts).Select(l => (l.Name, l.Id))];
    }

    /// <summary>Plots the <paramref name="requested"/> layouts (null = the sheets) of <paramref name="document"/> into <paramref name="path"/>.</summary>
    public static PlotOutcome Plot(Document document, IReadOnlyList<string>? requested, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            throw new ToolException("AutoCAD is already plotting. Wait until that plot has finished, then try again.");
        }

        var database = document.Database;
        var notes = new List<string>();
        var plotted = new List<string>();
        using var documentLock = AcadDispatcher.LockDocument(document);
        var manager = LayoutManager.Current;
        var originalLayout = manager.CurrentLayout;
        var background = AcadApp.GetSystemVariable("BACKGROUNDPLOT");
        AcadApp.SetSystemVariable("BACKGROUNDPLOT", Convert.ChangeType(0, background?.GetType() ?? typeof(short), System.Globalization.CultureInfo.InvariantCulture));
        var settingsToDispose = new List<PlotSettings>();
        PlotProgressDialog? progress = null;
        try
        {
            using var transaction = database.TransactionManager.StartTransaction();
            var layouts = ChooseLayouts(database, transaction, requested);
            if (layouts.Count == 0)
            {
                throw new ToolException("This drawing has no layouts to plot. Make sheets with create_sheets first.");
            }

            using var engine = PlotFactory.CreatePublishEngine();
            try
            {
                engine.BeginPlot(null, null);
            }
            catch (Exception ex) when (ex is NullReferenceException or ArgumentNullException)
            {
                // This AutoCAD wants a progress object: use its own progress dialog.
                progress = new PlotProgressDialog(false, layouts.Count, false);
                progress.OnBeginPlot();
                engine.BeginPlot(progress, null);
            }

            var validator = PlotSettingsValidator.Current;
            using var info = new PlotInfo();
            using var infoValidator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
            for (var i = 0; i < layouts.Count; i++)
            {
                var (name, id) = layouts[i];
                manager.CurrentLayout = name; // the publish engine plots the current layout's view of paper space
                var layout = (Layout)transaction.GetObject(id, OpenMode.ForRead);
                var settings = new PlotSettings(layout.ModelType);
                settingsToDispose.Add(settings);
                settings.CopyFrom(layout);
                if (!string.Equals(layout.PlotConfigurationName, PaperSizes.PdfDevice, StringComparison.OrdinalIgnoreCase))
                {
                    var size = layout.PlotPaperSize;
                    var units = layout.PlotPaperUnits == PlotPaperUnit.Inches ? PlotPaperUnit.Inches : PlotPaperUnit.Millimeters;
                    validator.SetPlotConfigurationName(settings, PaperSizes.PdfDevice, null);
                    validator.RefreshLists(settings);
                    try
                    {
                        validator.SetClosestMediaName(settings, size.X, size.Y, units, true);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Keep the device's default paper.
                    }

                    notes.Add($"\"{name}\" is set up for {layout.PlotConfigurationName}; plotted on {PaperSizes.PdfDevice} with the closest paper (the layout itself is unchanged).");
                }

                info.Layout = id;
                info.OverrideSettings = settings;
                infoValidator.Validate(info);
                if (i == 0)
                {
                    engine.BeginDocument(info, document.Name, null, 1, true, path);
                }

                progress?.OnBeginSheet();
                using var page = new PlotPageInfo();
                engine.BeginPage(page, info, i == layouts.Count - 1, null);
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                engine.EndPage(null);
                progress?.OnEndSheet();
                plotted.Add(name);
            }

            engine.EndDocument(null);
            engine.EndPlot(null);
            progress?.OnEndPlot();
            transaction.Commit();
        }
        finally
        {
            progress?.Dispose();
            foreach (var settings in settingsToDispose)
            {
                settings.Dispose();
            }

            try
            {
                AcadApp.SetSystemVariable("BACKGROUNDPLOT", background);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Leave it at 0 rather than fail the plot.
            }

            try
            {
                manager.CurrentLayout = originalLayout;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // The layout tab stays on the last sheet plotted.
            }
        }

        return new PlotOutcome(path, plotted, notes);
    }
}
