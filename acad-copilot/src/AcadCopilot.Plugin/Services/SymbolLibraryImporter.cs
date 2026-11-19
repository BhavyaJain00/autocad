using System.IO;
using System.Text;
using AcadCopilot.Core;
using AcadCopilot.Core.Services;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Services;

/// <summary>What an import of library blocks did.</summary>
/// <param name="File">The library file read.</param>
/// <param name="Imported">Blocks new in the drawing.</param>
/// <param name="Replaced">Blocks that existed and were redefined.</param>
/// <param name="Existing">Blocks that existed and were kept.</param>
internal sealed record LibraryImport(string File, IReadOnlyList<string> Imported, IReadOnlyList<string> Replaced, IReadOnlyList<string> Existing);

/// <summary>
/// Brings the services symbol blocks into the active drawing the reliable way: the library DXF (or the user's DWG) is
/// read into a side database (Database.DxfIn / ReadDwgFile) and its block definitions are copied with
/// Database.WblockCloneObjects (no command typed, nothing placed). The built-in library's blocks are marked as inches,
/// so -INSERT scales them 1:1 in an inch drawing and converts them in a metric one.
/// </summary>
internal static class SymbolLibraryImporter
{
    /// <summary>
    /// The built-in library file: the bundle's Contents\Blocks\AcadCopilot-Services.dxf when present, otherwise the same
    /// file generated into %LOCALAPPDATA%\AcadCopilot\blocks\ (any thread; touches no AutoCAD object).
    /// </summary>
    public static string BuiltInLibraryPath()
    {
        var bundled = Path.Combine(BridgeLocator.PluginDirectory, SymbolLibrary.BundleFolder, SymbolLibrary.FileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var folder = AppPaths.Ensure(Path.Combine(AppPaths.Root, "blocks"));
        var path = Path.Combine(folder, SymbolLibrary.FileName);
        var text = SymbolLibraryDxf.Write();
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path, Encoding.ASCII), text, StringComparison.Ordinal))
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, Encoding.ASCII);
            File.Move(temp, path, overwrite: true);
        }

        return path;
    }

    /// <summary>
    /// Copies the block definitions of <paramref name="path"/> (.dxf or .dwg) into <paramref name="target"/>.
    /// Main thread, inside the caller's write transaction (document locked).
    /// </summary>
    /// <param name="target">The drawing's database.</param>
    /// <param name="transaction">The open transaction on <paramref name="target"/>.</param>
    /// <param name="path">Library file.</param>
    /// <param name="only">Block names to import (null = all named blocks).</param>
    /// <param name="replace">Redefine blocks that already exist.</param>
    /// <param name="inches">Mark the blocks as drawn in inches (the built-in library).</param>
    public static LibraryImport Import(Database target, Transaction transaction, string path, IReadOnlyCollection<string>? only, bool replace, bool inches)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(transaction);
        var wanted = only is null ? null : new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
        var imported = new List<string>();
        var replaced = new List<string>();
        var existing = new List<string>();

        using var source = new Database(false, true);
        if (path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            source.DxfIn(path, Path.Combine(Path.GetTempPath(), "AcadCopilot-dxfin.log"));
        }
        else
        {
            source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
            source.CloseInput(true);
        }

        if (inches)
        {
            source.Insunits = UnitsValue.Inches;
        }

        var ids = new ObjectIdCollection();
        var names = new List<string>();
        using (var sourceTransaction = source.TransactionManager.StartTransaction())
        {
            var table = (BlockTable)sourceTransaction.GetObject(source.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                var block = (BlockTableRecord)sourceTransaction.GetObject(id, OpenMode.ForRead);
                if (block.IsLayout || block.IsAnonymous || block.IsFromExternalReference || block.IsDependent ||
                    (wanted is not null && !wanted.Contains(block.Name)))
                {
                    continue;
                }

                if (inches && block.Units != UnitsValue.Inches)
                {
                    block.UpgradeOpen();
                    block.Units = UnitsValue.Inches;
                }

                ids.Add(id);
                names.Add(block.Name);
            }

            sourceTransaction.Commit();
        }

        if (ids.Count == 0)
        {
            throw new ToolException(wanted is null
                ? $"{Path.GetFileName(path)} has no named block definitions to import."
                : $"{Path.GetFileName(path)} has none of the blocks {string.Join(", ", wanted)}.");
        }

        var targetTable = (BlockTable)transaction.GetObject(target.BlockTableId, OpenMode.ForRead);
        foreach (var name in names)
        {
            if (!targetTable.Has(name))
            {
                imported.Add(name);
            }
            else if (replace)
            {
                replaced.Add(name);
            }
            else
            {
                existing.Add(name);
            }
        }

        if (imported.Count > 0 || replaced.Count > 0)
        {
            using var mapping = new IdMapping();
            source.WblockCloneObjects(ids, target.BlockTableId, mapping, replace ? DuplicateRecordCloning.Replace : DuplicateRecordCloning.Ignore, false);
        }

        return new LibraryImport(path, imported, replaced, existing);
    }

    /// <summary>
    /// Creates the missing services layers (colour, linetype, lineweight, description); existing layers are left as
    /// the user set them. Main thread, inside the caller's write transaction. Returns the names created.
    /// </summary>
    public static IReadOnlyList<string> EnsureLayers(Database database, Transaction transaction, IEnumerable<ServiceLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        var created = new List<string>();
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        foreach (var layer in layers)
        {
            if (layerTable.Has(layer.Name))
            {
                continue;
            }

            var record = new LayerTableRecord
            {
                Name = layer.Name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, layer.ColorIndex),
                LineWeight = (LineWeight)layer.Lineweight,
                Description = layer.Description,
            };
            if (LinetypeId(database, transaction, layer.Linetype) is { } linetype)
            {
                record.LinetypeObjectId = linetype;
            }

            if (!layerTable.IsWriteEnabled)
            {
                layerTable.UpgradeOpen();
            }

            layerTable.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            created.Add(layer.Name);
        }

        return created;
    }

    /// <summary>The names of the <paramref name="symbols"/> blocks missing from the drawing. Main thread.</summary>
    public static IReadOnlyList<string> MissingBlocks(Database database, Transaction transaction, IEnumerable<SymbolDefinition> symbols)
    {
        var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        return [.. symbols.Where(s => !table.Has(s.BlockName)).Select(s => s.BlockName)];
    }

    private static ObjectId? LinetypeId(Database database, Transaction transaction, string name)
    {
        if (string.Equals(name, "CONTINUOUS", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (!table.Has(name))
        {
            try
            {
                database.LoadLineTypeFile(name, database.Measurement == MeasurementValue.Metric ? "acadiso.lin" : "acad.lin");
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null; // Linetype file not found: the layer stays Continuous.
            }
        }

        return table.Has(name) ? table[name] : null;
    }
}
