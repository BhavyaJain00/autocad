using AcadCopilot.Core.Services;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadCopilot.Plugin.Services;

/// <summary>
/// Draws a <see cref="LegendLayout"/> (lines, texts, symbol block references with their attributes) into model space.
/// Main thread, inside the caller's write transaction; the symbol blocks must exist (import them first).
/// </summary>
internal static class ServicesLegendWriter
{
    /// <summary>Draws the legend; returns the ids of everything created.</summary>
    public static List<ObjectId> Draw(Database database, Transaction transaction, LegendLayout layout)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(layout);
        var created = new List<ObjectId>();
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);

        foreach (var line in layout.Lines)
        {
            var entity = new Line(new Point3d(line.X1, line.Y1, 0), new Point3d(line.X2, line.Y2, 0));
            entity.SetDatabaseDefaults(database);
            entity.Layer = layout.Layer;
            created.Add(space.AppendEntity(entity));
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        foreach (var text in layout.Texts)
        {
            var entity = new DBText();
            entity.SetDatabaseDefaults(database);
            entity.Layer = layout.Layer;
            entity.TextStyleId = database.Textstyle;
            entity.Height = text.Height;
            entity.TextString = text.Text;
            var point = new Point3d(text.X, text.Y, 0);
            entity.Position = point;
            entity.HorizontalMode = text.Centered ? TextHorizontalMode.TextCenter : TextHorizontalMode.TextLeft;
            entity.VerticalMode = TextVerticalMode.TextVerticalMid;
            entity.AlignmentPoint = point;
            created.Add(space.AppendEntity(entity));
            transaction.AddNewlyCreatedDBObject(entity, true);
            entity.AdjustAlignment(database);
        }

        foreach (var symbol in layout.Symbols)
        {
            if (!blocks.Has(symbol.Symbol.BlockName))
            {
                continue;
            }

            var definitionId = blocks[symbol.Symbol.BlockName];
            var reference = new BlockReference(new Point3d(symbol.X, symbol.Y, 0), definitionId);
            reference.SetDatabaseDefaults(database);
            reference.Layer = symbol.Symbol.Layer;
            reference.ScaleFactors = new Scale3d(symbol.Scale);
            created.Add(space.AppendEntity(reference));
            transaction.AddNewlyCreatedDBObject(reference, true);

            var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
            if (!definition.HasAttributeDefinitions)
            {
                continue;
            }

            foreach (ObjectId id in definition)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is not AttributeDefinition attribute || attribute.Constant)
                {
                    continue;
                }

                var value = new AttributeReference();
                value.SetAttributeFromBlock(attribute, reference.BlockTransform);
                value.TextString = attribute.TextString;
                reference.AttributeCollection.AppendAttribute(value);
                transaction.AddNewlyCreatedDBObject(value, true);
                if (!attribute.IsMTextAttributeDefinition && (attribute.HorizontalMode != TextHorizontalMode.TextLeft || attribute.VerticalMode != TextVerticalMode.TextBase))
                {
                    value.AdjustAlignment(database);
                }
            }
        }

        return created;
    }
}
