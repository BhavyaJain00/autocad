namespace AcadCopilot.Plugin.Inspect;

/// <summary>
/// Pure pixel and size maths for capture_view (no AutoCAD, GDI or WPF types), so it is unit-tested outside AutoCAD:
/// picture size from a view's proportions, blank-picture detection, and the conversion of a render on black into a
/// plot-like picture on white.
/// </summary>
public static class CaptureMath
{
    /// <summary>Default picture width in pixels.</summary>
    public const int DefaultWidth = 1200;

    /// <summary>Largest picture width in pixels.</summary>
    public const int MaxWidth = 1600;

    /// <summary>Largest picture height in pixels.</summary>
    public const int MaxHeight = 1200;

    /// <summary>Smallest picture side in pixels.</summary>
    public const int MinSide = 64;

    /// <summary>
    /// Pixel size of a picture of an area <paramref name="worldWidth"/> x <paramref name="worldHeight"/> (any unit):
    /// as wide as <paramref name="maxWidth"/> allows, proportions kept, height at most <paramref name="maxHeight"/>.
    /// Degenerate areas (zero, negative, not finite) give a 3:2 picture.
    /// </summary>
    public static (int Width, int Height) FitSize(double worldWidth, double worldHeight, int maxWidth, int maxHeight)
    {
        maxWidth = Math.Max(MinSide, maxWidth);
        maxHeight = Math.Max(MinSide, maxHeight);
        var aspect = worldWidth > 0 && worldHeight > 0 && double.IsFinite(worldWidth) && double.IsFinite(worldHeight)
            ? worldHeight / worldWidth
            : 2.0 / 3.0;

        double width = maxWidth;
        var height = width * aspect;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height / aspect;
        }

        return (Math.Clamp((int)Math.Round(width), MinSide, maxWidth), Math.Clamp((int)Math.Round(height), MinSide, maxHeight));
    }

    /// <summary>
    /// Grows a <paramref name="box"/> (plus a small margin) so its proportions match a picture of
    /// <paramref name="pixelWidth"/> x <paramref name="pixelHeight"/>, keeping its centre. Used to frame "extents"
    /// and "window" pictures without distortion.
    /// </summary>
    public static PlanBox FrameForPicture(PlanBox box, int pixelWidth, int pixelHeight, double marginFraction = 0.03)
    {
        var size = Math.Max(box.Width, box.Height);
        var framed = size > 0 ? box.Expand(marginFraction, 0) : box.Expand(0, 0.5); // a single point: show 1 unit around it
        var aspect = (double)pixelHeight / pixelWidth;
        var width = Math.Max(framed.Width, size * 1e-6);
        var height = Math.Max(framed.Height, size * 1e-6);
        if (height / width < aspect)
        {
            height = width * aspect;
        }
        else
        {
            width = height / aspect;
        }

        return new PlanBox(framed.CenterX - (width / 2), framed.CenterY - (height / 2), framed.CenterX + (width / 2), framed.CenterY + (height / 2));
    }

    /// <summary>
    /// True when every pixel has the same colour: the renderer produced an empty picture.
    /// <paramref name="pixels"/> holds <paramref name="height"/> rows of <paramref name="stride"/> bytes, each with
    /// <paramref name="width"/> pixels of <paramref name="bytesPerPixel"/> bytes (3 = BGR, 4 = BGRA).
    /// </summary>
    public static bool IsUniform(ReadOnlySpan<byte> pixels, int width, int height, int stride, int bytesPerPixel)
    {
        ValidateLayout(pixels.Length, width, height, stride, bytesPerPixel);
        var first = pixels[..bytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * bytesPerPixel);
            for (var x = 0; x < row.Length; x += bytesPerPixel)
            {
                if (!row.Slice(x, bytesPerPixel).SequenceEqual(first))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Turns a picture rendered on black into a plot-like picture on white, in place: black becomes white, white and
    /// grey lines become black and grey, coloured lines keep their hue but get darker so yellow, cyan and green stay
    /// readable on white. Smooth (antialiased) edges stay smooth. Alpha, when present, is left alone.
    /// Layout as in <see cref="IsUniform"/>; channel order B, G, R.
    /// </summary>
    public static void InkOnWhite(Span<byte> pixels, int width, int height, int stride, int bytesPerPixel)
    {
        ValidateLayout(pixels.Length, width, height, stride, bytesPerPixel);
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * bytesPerPixel);
            for (var x = 0; x < row.Length; x += bytesPerPixel)
            {
                var (b, g, r) = InkOnWhite(row[x], row[x + 1], row[x + 2]);
                row[x] = b;
                row[x + 1] = g;
                row[x + 2] = r;
            }
        }
    }

    /// <summary>
    /// One pixel of <see cref="InkOnWhite(Span{byte}, int, int, int, int)"/>: in HSV terms the hue and saturation
    /// are kept and the value becomes 1 - v * (1 - 0.4 * s). Black gives white, white gives black, pure red gives
    /// (102, 0, 0), 50 % grey stays about 50 % grey.
    /// </summary>
    public static (byte B, byte G, byte R) InkOnWhite(byte b, byte g, byte r)
    {
        var max = Math.Max(r, Math.Max(g, b));
        if (max == 0)
        {
            return (255, 255, 255);
        }

        var min = Math.Min(r, Math.Min(g, b));
        var value = max / 255.0;
        var saturation = (max - min) / (double)max;
        var newValue = 1.0 - (value * (1.0 - (0.4 * saturation)));

        // In HSV every channel is proportional to the value, so scaling each by newValue / value keeps hue and saturation.
        var scale = newValue * 255.0 / max;
        return (Channel(b * scale), Channel(g * scale), Channel(r * scale));
    }

    private static byte Channel(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static void ValidateLayout(int length, int width, int height, int stride, int bytesPerPixel)
    {
        if (bytesPerPixel is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel), "Use 3 (BGR) or 4 (BGRA) bytes per pixel.");
        }

        if (width < 1 || height < 1 || stride < width * bytesPerPixel || length < ((height - 1) * stride) + (width * bytesPerPixel))
        {
            throw new ArgumentException("The pixel buffer does not match the given size and stride.");
        }
    }
}
