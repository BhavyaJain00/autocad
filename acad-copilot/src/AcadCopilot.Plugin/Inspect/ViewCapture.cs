using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using AcadGeometry = Autodesk.AutoCAD.Geometry;
using Gs = Autodesk.AutoCAD.GraphicsSystem;

namespace AcadCopilot.Plugin.Inspect;

/// <summary>How a picture of the drawing is taken.</summary>
public enum CaptureMethod
{
    /// <summary>
    /// AutoCAD's graphics system renders the drawing into an off-screen device (Manager.CreateAutoCADOffScreenDevice,
    /// View.GetSnapshot): clean picture, any area, chosen background; nothing on screen changes.
    /// </summary>
    Offscreen,

    /// <summary>A copy of the drawing window as the user sees it (Win32 PrintWindow with PW_RENDERFULLCONTENT).</summary>
    Screen,

    /// <summary>AutoCAD's preview image of the current view (DocumentExtension.CapturePreviewImage).</summary>
    Preview,
}

/// <summary>What capture_view asked for.</summary>
public sealed record CaptureRequest
{
    /// <summary>Largest picture width in pixels.</summary>
    public int MaxWidth { get; init; } = CaptureMath.DefaultWidth;

    /// <summary>Area to show in WCS (null = the current view of the active viewport).</summary>
    public PlanBox? Area { get; init; }

    /// <summary>Plot-like picture on white (true) or the screen's dark look (false). Off-screen renders only.</summary>
    public bool WhiteBackground { get; init; } = true;

    /// <summary>Methods to try, in order; the first that gives a picture with content wins.</summary>
    public IReadOnlyList<CaptureMethod> Methods { get; init; } = [CaptureMethod.Offscreen, CaptureMethod.Screen, CaptureMethod.Preview];

    /// <summary>True when the area is known to hold no entities, so a blank picture is the right answer.</summary>
    public bool ExpectBlank { get; init; }
}

/// <summary>The picture and how it was made.</summary>
/// <param name="Image">The PNG.</param>
/// <param name="Method">The method that produced it.</param>
/// <param name="Blank">True when every pixel has the same colour.</param>
/// <param name="Shown">For an area: the WCS rectangle the picture shows (the area plus margins, in picture proportions).</param>
/// <param name="Notes">What was tried and failed before, in plain words.</param>
public sealed record CaptureOutcome(ToolImage Image, CaptureMethod Method, bool Blank, PlanBox? Shown, IReadOnlyList<string> Notes);

/// <summary>
/// Takes a PNG picture of the drawing without visible side effects (no zoom, no regen, no window). Fallback chain:
/// off-screen render, then a copy of the drawing window, then AutoCAD's preview image. Main thread only.
/// </summary>
public static class ViewCapture
{
    private const uint PwClientOnly = 0x1;
    private const uint PwRenderFullContent = 0x2;

    /// <summary>Takes the picture, trying <see cref="CaptureRequest.Methods"/> in order.</summary>
    /// <exception cref="ToolException">No method produced a picture.</exception>
    public static CaptureOutcome Capture(Document document, CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        var notes = new List<string>();
        CaptureOutcome? blank = null;

        // An area is framed once: picture size from its proportions, then the area grown to exactly those proportions.
        (int Width, int Height, PlanBox Box)? frame = null;
        if (request.Area is { } area)
        {
            var (width, height) = CaptureMath.FitSize(area.Width, area.Height, request.MaxWidth, CaptureMath.MaxHeight);
            frame = (width, height, CaptureMath.FrameForPicture(area, width, height));
        }

        foreach (var method in request.Methods)
        {
            if (request.Area is not null && method != CaptureMethod.Offscreen)
            {
                notes.Add($"The {Describe(method)} shows only the current view, so it was not used for this area.");
                continue;
            }

            try
            {
                using var bitmap = method switch
                {
                    CaptureMethod.Offscreen => RenderOffscreen(document, request.MaxWidth, frame),
                    CaptureMethod.Screen => CopyDrawingWindow(document),
                    _ => CapturePreview(document, request.MaxWidth),
                };
                var invert = method == CaptureMethod.Offscreen && request.WhiteBackground;
                var image = Encode(bitmap, request.MaxWidth, invert, out var uniform);
                var outcome = new CaptureOutcome(image, method, uniform, frame?.Box, [.. notes]);
                if (!uniform || request.ExpectBlank)
                {
                    return outcome;
                }

                blank ??= outcome;
                notes.Add($"The {Describe(method)} came out blank although the area holds entities.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Any failure of one technique (AutoCAD graphics errors, GDI+/Win32 ExternalException, GDI+ "parameter is
                // not valid" ArgumentException, null wrappers) falls through to the next technique in the chain.
                CopilotLog.Warn($"capture_view: the {Describe(method)} failed", ex);
                notes.Add($"The {Describe(method)} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (blank is not null)
        {
            return blank with { Notes = notes };
        }

        throw new ToolException("Could not take a picture of the drawing. " + string.Join(" ", notes));
    }

    /// <summary>Plain-words name of a method for notes.</summary>
    public static string Describe(CaptureMethod method) => method switch
    {
        CaptureMethod.Offscreen => "off-screen render",
        CaptureMethod.Screen => "copy of the drawing window",
        _ => "AutoCAD preview image",
    };

    /// <summary>Wire name of a method ("offscreen", "screen", "preview").</summary>
    public static string WireName(CaptureMethod method) => method.ToString().ToLowerInvariant();

    private static Bitmap RenderOffscreen(Document document, int maxWidth, (int Width, int Height, PlanBox Box)? frame)
    {
        var database = document.Database;
        var manager = document.GraphicsManager ?? throw new InvalidOperationException("The drawing has no graphics manager.");
        var viewportNumber = ViewGeometry.CurrentViewport();
        using var descriptor = new Gs.KernelDescriptor();
        descriptor.addRequirement(Gs.KernelDescriptor.Drawing3D);
        var kernel = Gs.Manager.AcquireGraphicsKernel(descriptor) ?? throw new InvalidOperationException("AutoCAD has no graphics kernel for off-screen rendering.");
        try
        {
            using var view = new Gs.View();
            manager.SetViewFromViewport(view, viewportNumber);
            int width;
            int height;
            if (frame is { } framed)
            {
                // A plan view of the framed area, looking down the Z axis; the user's own view is untouched.
                (width, height) = (framed.Width, framed.Height);
                var target = new AcadGeometry.Point3d(framed.Box.CenterX, framed.Box.CenterY, 0);
                view.SetView(target + AcadGeometry.Vector3d.ZAxis, target, AcadGeometry.Vector3d.YAxis, framed.Box.Width, framed.Box.Height);
            }
            else
            {
                (width, height) = CaptureMath.FitSize(view.FieldWidth, view.FieldHeight, maxWidth, CaptureMath.MaxHeight);
            }

            using var device = manager.CreateAutoCADOffScreenDevice(kernel) ?? throw new InvalidOperationException("AutoCAD could not create an off-screen device.");
            device.OnSize(new Size(width, height));
            device.DeviceRenderType = Gs.RendererType.Default;

            // Always render on black: colour 7 then draws white, exactly as on a dark screen. A white picture is made
            // afterwards by CaptureMath.InkOnWhite, which does not depend on how the device maps colour 7.
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
                        // Paper space of a layout shows the sheet; everything else (model tab, a viewport, an area) model space.
                        var spaceId = frame is null && !database.TileMode && viewportNumber <= 1
                            ? database.CurrentSpaceId
                            : AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(database);
                        var space = (AcadDb.BlockTableRecord)transaction.GetObject(spaceId, AcadDb.OpenMode.ForRead);
                        view.Add(space, model);
                        transaction.Commit();
                    }

                    view.Update();
                    return view.GetSnapshot(new Rectangle(0, 0, width, height))
                           ?? throw new InvalidOperationException("The off-screen view returned no picture.");
                }
                finally
                {
                    // Detach the drawing before the model goes away (order as in Autodesk's samples).
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

    private static Bitmap CopyDrawingWindow(Document document)
    {
        var handle = document.Window?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The drawing window has no handle.");
        }

        if (IsIconic(handle))
        {
            throw new InvalidOperationException("The drawing window is minimized.");
        }

        if (!GetClientRect(handle, out var rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 16 || height < 16)
        {
            throw new InvalidOperationException($"The drawing window is too small ({width} x {height} px).");
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            bool copied;
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    copied = PrintWindow(handle, hdc, PwClientOnly | PwRenderFullContent);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            if (!copied)
            {
                throw new InvalidOperationException("Windows could not copy the drawing window (PrintWindow failed).");
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap CapturePreview(Document document, int maxWidth)
    {
        double viewWidth;
        double viewHeight;
        using (var view = document.Editor.GetCurrentView())
        {
            viewWidth = view.Width;
            viewHeight = view.Height;
        }

        var (width, height) = CaptureMath.FitSize(viewWidth, viewHeight, maxWidth, CaptureMath.MaxHeight);
        return document.CapturePreviewImage((uint)width, (uint)height)
               ?? throw new InvalidOperationException("AutoCAD returned no preview image.");
    }

    /// <summary>
    /// Scales <paramref name="source"/> to fit <paramref name="maxWidth"/> x <see cref="CaptureMath.MaxHeight"/>,
    /// optionally turns a render on black into a picture on white, checks for a blank result and encodes a 24-bit PNG.
    /// </summary>
    private static ToolImage Encode(Bitmap source, int maxWidth, bool inkOnWhite, out bool uniform)
    {
        var (width, height) = source.Width <= maxWidth && source.Height <= CaptureMath.MaxHeight
            ? (source.Width, source.Height)
            : CaptureMath.FitSize(source.Width, source.Height, maxWidth, CaptureMath.MaxHeight);
        using var picture = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(picture))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }

        var bounds = new Rectangle(0, 0, width, height);
        var data = picture.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            uniform = CaptureMath.IsUniform(pixels, width, height, stride, 3);
            if (inkOnWhite)
            {
                CaptureMath.InkOnWhite(pixels, width, height, stride, 3);
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
        }
        finally
        {
            picture.UnlockBits(data);
        }

        using var png = new MemoryStream();
        picture.Save(png, ImageFormat.Png);
        return ToolImage.FromPng(png.ToArray());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
