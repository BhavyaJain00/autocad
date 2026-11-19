using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Presentation;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Inspect;
using Autodesk.AutoCAD.ApplicationServices;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using AcadGeometry = Autodesk.AutoCAD.Geometry;
using Gs = Autodesk.AutoCAD.GraphicsSystem;

namespace AcadCopilot.Plugin.Services;

/// <summary>The rendered export picture.</summary>
/// <param name="Png">The full-size PNG.</param>
/// <param name="Preview">A small PNG for the AI to look at.</param>
/// <param name="Width">Pixels.</param>
/// <param name="Height">Pixels.</param>
/// <param name="Shown">The world rectangle in the picture.</param>
/// <param name="Method">How it was made.</param>
/// <param name="Entities">Entities drawn (fills, lines).</param>
internal sealed record ExportPicture(byte[] Png, ToolImage Preview, int Width, int Height, PlanBox Shown, string Method, (int Fills, int Lines) Entities);

/// <summary>
/// Renders the drawing for export_view_png off-screen (nothing on screen changes): one pass with the colour fills only
/// and one with the linework only, both on black, then <see cref="PresentationPixels.Composite"/> on paper white. Falls
/// back to capture_view's single render (<see cref="ViewCapture"/>) with <see cref="PresentationPixels.PaperFromBlackRender"/>.
/// Main thread only.
/// </summary>
internal static class PresentationExporter
{
    /// <summary>Most pixels of one export (memory).</summary>
    public const long MaxPixels = 12_000_000;

    /// <summary>Preview width.</summary>
    public const int PreviewWidth = 900;

    /// <summary>Renders <paramref name="area"/> at <paramref name="width"/> pixels wide.</summary>
    public static ExportPicture Render(Document document, PlanBox area, int width, bool white)
    {
        ArgumentNullException.ThrowIfNull(document);
        var (w, h) = CaptureMath.FitSize(area.Width, area.Height, width, PresentationToolDefinitions.MaxExportWidth);
        if ((long)w * h > MaxPixels)
        {
            var shrink = Math.Sqrt(MaxPixels / (double)((long)w * h));
            (w, h) = ((int)(w * shrink), (int)(h * shrink));
        }

        var frame = CaptureMath.FrameForPicture(area, w, h, marginFraction: 0.02);
        var (fills, lines) = Classify(document.Database);
        try
        {
            if (!white)
            {
                using var all = RenderPass(document, [.. fills, .. lines], w, h, frame);
                return Finish(EncodeAsIs(all), w, h, frame, "off-screen render on black", (fills.Count, lines.Count));
            }

            using var fillBitmap = RenderPass(document, fills, w, h, frame);
            using var lineBitmap = RenderPass(document, lines, w, h, frame);
            return Finish(CompositeToPng(fillBitmap, lineBitmap, w, h), w, h, frame, "off-screen render, fills and linework composited on white", (fills.Count, lines.Count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolException)
        {
            CopilotLog.Warn("export_view_png: the two-pass render failed; using capture_view's render", ex);
            var outcome = ViewCapture.Capture(document, new CaptureRequest
            {
                MaxWidth = Math.Min(w, CaptureMath.MaxWidth),
                Area = area,
                WhiteBackground = false,
                Methods = [CaptureMethod.Offscreen],
            });
            using var stream = new MemoryStream(outcome.Image.GetBytes());
            using var bitmap = new Bitmap(stream);
            var png = white ? PaperFromBlack(bitmap) : outcome.Image.GetBytes();
            return Finish(png, bitmap.Width, bitmap.Height, outcome.Shown ?? frame, "single off-screen render (fallback; light grey fills may look dark)", (fills.Count, lines.Count));
        }
    }

    /// <summary>The model-space extents of visible entities on visible layers, or null when nothing is drawn.</summary>
    public static PlanBox? Extents(Document document)
    {
        var database = document.Database;
        PlanBox? box = null;
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        foreach (var id in Visible(database, transaction))
        {
            try
            {
                if (transaction.GetObject(id, AcadDb.OpenMode.ForRead) is AcadDb.Entity entity && entity.Bounds is { } bounds)
                {
                    var next = new PlanBox(bounds.MinPoint.X, bounds.MinPoint.Y, bounds.MaxPoint.X, bounds.MaxPoint.Y);
                    box = box is { } current ? current.Union(next) : next;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // No measurable bounds (rays, empty text).
            }
        }

        transaction.Commit();
        return box;
    }

    // Fills = colour hatches (solid, gradient, with background) not in colour 7, raster images, and everything on the
    // presentation layers; lines = the rest. Both in draw order, without off / frozen layers and invisible entities.
    private static (List<AcadDb.ObjectId> Fills, List<AcadDb.ObjectId> Lines) Classify(AcadDb.Database database)
    {
        var fills = new List<AcadDb.ObjectId>();
        var lines = new List<AcadDb.ObjectId>();
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        foreach (var id in Visible(database, transaction))
        {
            var entity = (AcadDb.Entity)transaction.GetObject(id, AcadDb.OpenMode.ForRead);
            (IsFill(entity, transaction) ? fills : lines).Add(id);
        }

        transaction.Commit();
        return (fills, lines);
    }

    private static bool IsFill(AcadDb.Entity entity, AcadDb.Transaction transaction)
    {
        if (PresentationFinishes.IsPresentationLayer(entity.Layer) || entity is AcadDb.RasterImage)
        {
            return true;
        }

        if (entity is not AcadDb.Hatch hatch)
        {
            return false;
        }

        try
        {
            var filled = hatch.IsGradient ||
                         string.Equals(hatch.PatternName, "SOLID", StringComparison.OrdinalIgnoreCase) ||
                         hatch.BackgroundColor is { ColorMethod: not Autodesk.AutoCAD.Colors.ColorMethod.None };
            return filled && !IsColor7(hatch, transaction);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
    }

    // Colour 7 (white on black, black on paper) belongs with the linework so it prints dark.
    private static bool IsColor7(AcadDb.Entity entity, AcadDb.Transaction transaction)
    {
        if (entity is AcadDb.Hatch { IsGradient: true })
        {
            return false;
        }

        var color = entity.Color;
        if (color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByLayer &&
            transaction.GetObject(entity.LayerId, AcadDb.OpenMode.ForRead) is AcadDb.LayerTableRecord layer)
        {
            color = layer.Color;
        }

        return color.ColorMethod is Autodesk.AutoCAD.Colors.ColorMethod.ByBlock or Autodesk.AutoCAD.Colors.ColorMethod.Foreground ||
               (color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci && color.ColorIndex == 7);
    }

    private static IEnumerable<AcadDb.ObjectId> Visible(AcadDb.Database database, AcadDb.Transaction transaction)
    {
        var space = (AcadDb.BlockTableRecord)transaction.GetObject(AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(database), AcadDb.OpenMode.ForRead);
        IEnumerable<AcadDb.ObjectId> ordered;
        try
        {
            var order = (AcadDb.DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, AcadDb.OpenMode.ForRead);
            using var full = order.GetFullDrawOrder(0);
            ordered = [.. full.Cast<AcadDb.ObjectId>()];
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            ordered = [.. space.Cast<AcadDb.ObjectId>()];
        }

        var hiddenLayers = new HashSet<AcadDb.ObjectId>();
        var layers = (AcadDb.LayerTable)transaction.GetObject(database.LayerTableId, AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId layerId in layers)
        {
            var layer = (AcadDb.LayerTableRecord)transaction.GetObject(layerId, AcadDb.OpenMode.ForRead);
            if (layer.IsOff || layer.IsFrozen)
            {
                hiddenLayers.Add(layerId);
            }
        }

        foreach (var id in ordered)
        {
            if (id.IsErased || transaction.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity { Visible: true } entity ||
                hiddenLayers.Contains(entity.LayerId))
            {
                continue;
            }

            yield return id;
        }
    }

    private static Bitmap RenderPass(Document document, IReadOnlyList<AcadDb.ObjectId> ids, int width, int height, PlanBox frame)
    {
        var database = document.Database;
        var manager = document.GraphicsManager ?? throw new InvalidOperationException("The drawing has no graphics manager.");
        using var descriptor = new Gs.KernelDescriptor();
        descriptor.addRequirement(Gs.KernelDescriptor.Drawing3D);
        var kernel = Gs.Manager.AcquireGraphicsKernel(descriptor) ?? throw new InvalidOperationException("AutoCAD has no graphics kernel for off-screen rendering.");
        try
        {
            using var view = new Gs.View();
            manager.SetViewFromViewport(view, ViewGeometry.CurrentViewport());
            var target = new AcadGeometry.Point3d(frame.CenterX, frame.CenterY, 0);
            view.SetView(target + AcadGeometry.Vector3d.ZAxis, target, AcadGeometry.Vector3d.YAxis, frame.Width, frame.Height);
            using var device = manager.CreateAutoCADOffScreenDevice(kernel) ?? throw new InvalidOperationException("AutoCAD could not create an off-screen device.");
            device.OnSize(new Size(width, height));
            device.DeviceRenderType = Gs.RendererType.Default;
            device.BackgroundColor = Color.Black;
            device.Add(view);
            try
            {
                device.Update();
                using var model = manager.CreateAutoCADModel(kernel) ?? throw new InvalidOperationException("AutoCAD could not create a graphics model.");
                try
                {
                    using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        foreach (var id in ids)
                        {
                            if (transaction.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Entity entity)
                            {
                                view.Add(entity, model);
                            }
                        }

                        transaction.Commit();
                    }

                    view.Update();
                    return view.GetSnapshot(new Rectangle(0, 0, width, height))
                           ?? throw new InvalidOperationException("The off-screen view returned no picture.");
                }
                finally
                {
                    view.EraseAll();
                }
            }
            finally
            {
                device.Erase(view);
            }
        }
        finally
        {
            Gs.Manager.ReleaseGraphicsKernel(kernel);
        }
    }

    private static byte[] CompositeToPng(Bitmap fills, Bitmap lines, int width, int height)
    {
        var fillPixels = Pixels(fills, width, height, out var stride);
        var linePixels = Pixels(lines, width, height, out _);
        var output = new byte[fillPixels.Length];
        PresentationPixels.Composite(fillPixels, linePixels, output, width, height, stride, 3);
        return ToPng(output, width, height, stride);
    }

    private static byte[] PaperFromBlack(Bitmap source)
    {
        var pixels = Pixels(source, source.Width, source.Height, out var stride);
        PresentationPixels.PaperFromBlackRender(pixels, source.Width, source.Height, stride, 3);
        return ToPng(pixels, source.Width, source.Height, stride);
    }

    private static byte[] EncodeAsIs(Bitmap source)
    {
        var pixels = Pixels(source, source.Width, source.Height, out var stride);
        return ToPng(pixels, source.Width, source.Height, stride);
    }

    // The bitmap as 24-bit BGR rows of the requested size (scaled if the device returned another size).
    private static byte[] Pixels(Bitmap source, int width, int height, out int stride)
    {
        using var copy = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(copy))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = source.Width == width && source.Height == height ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }

        var data = copy.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            copy.UnlockBits(data);
        }
    }

    private static byte[] ToPng(byte[] pixels, int width, int height, int stride)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * stride, data.Scan0 + (y * data.Stride), Math.Min(stride, Math.Abs(data.Stride)));
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static ExportPicture Finish(byte[] png, int width, int height, PlanBox shown, string method, (int, int) entities)
    {
        using var stream = new MemoryStream(png);
        using var full = new Bitmap(stream);
        var (pw, ph) = CaptureMath.FitSize(full.Width, full.Height, Math.Min(PreviewWidth, full.Width), PreviewWidth);
        using var preview = new Bitmap(pw, ph, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(full, new Rectangle(0, 0, pw, ph));
        }

        using var previewStream = new MemoryStream();
        preview.Save(previewStream, ImageFormat.Png);
        return new ExportPicture(png, ToolImage.FromPng(previewStream.ToArray()), width, height, shown, method, entities);
    }
}
