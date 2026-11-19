namespace AcadCopilot.Core.Presentation;

/// <summary>
/// Pixel maths of export_view_png (pure, no GDI or AutoCAD types). AutoCAD's off-screen renderer draws on black, where
/// colour 7 (white/black) is white. A presentation picture needs paper white with dark linework but the fills in their
/// exact true colours, so the drawing is rendered twice on black: once only the colour fills (hatches, images), once
/// only the linework, and <see cref="Composite"/> lays the linework, darkened like ink, over the fills on white paper.
/// Buffers hold <c>height</c> rows of <c>stride</c> bytes, pixels of 3 (B, G, R) or 4 (B, G, R, A) bytes.
/// </summary>
public static class PresentationPixels
{
    /// <summary>A fill pixel at most this bright is "nothing drawn" and becomes paper white.</summary>
    public const int EmptyThreshold = 6;

    /// <summary>
    /// Composites into <paramref name="output"/>: paper white, then the fill pixels as they are, then the line pixels
    /// as ink (their brightness on black is their coverage; white and grey lines become black, coloured lines keep
    /// their hue, darkened to read on white). Anti-aliased line edges blend smoothly. Alpha, if any, is set opaque.
    /// </summary>
    public static void Composite(ReadOnlySpan<byte> fills, ReadOnlySpan<byte> lines, Span<byte> output, int width, int height, int stride, int bytesPerPixel)
    {
        Validate(fills.Length, width, height, stride, bytesPerPixel);
        Validate(lines.Length, width, height, stride, bytesPerPixel);
        Validate(output.Length, width, height, stride, bytesPerPixel);
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + (x * bytesPerPixel);
                var (b, g, r) = CompositePixel(fills[i], fills[i + 1], fills[i + 2], lines[i], lines[i + 1], lines[i + 2]);
                output[i] = b;
                output[i + 1] = g;
                output[i + 2] = r;
                if (bytesPerPixel == 4)
                {
                    output[i + 3] = 255;
                }
            }
        }
    }

    /// <summary>One pixel of <see cref="Composite"/> (channels B, G, R).</summary>
    public static (byte B, byte G, byte R) CompositePixel(byte fillB, byte fillG, byte fillR, byte lineB, byte lineG, byte lineR)
    {
        // Paper or fill.
        double b = 255, g = 255, r = 255;
        if (Math.Max(fillR, Math.Max(fillG, fillB)) > EmptyThreshold)
        {
            (b, g, r) = (fillB, fillG, fillR);
        }

        // Ink: coverage = brightness of the line pixel on black; its full colour is the pixel scaled to full brightness.
        var max = Math.Max(lineR, Math.Max(lineG, lineB));
        if (max > 3)
        {
            var coverage = max / 255.0;
            var (inkB, inkG, inkR) = Ink(lineB * 255.0 / max, lineG * 255.0 / max, lineR * 255.0 / max);
            b += (inkB - b) * coverage;
            g += (inkG - g) * coverage;
            r += (inkR - r) * coverage;
        }

        return (Channel(b), Channel(g), Channel(r));
    }

    /// <summary>
    /// A single render on black turned into a paper picture when the two-pass render is not available: near-neutral
    /// pixels (black background, white lines, greys) are inverted, coloured pixels keep their colour. Light neutral
    /// fills (white bands) come out dark this way; the two-pass <see cref="Composite"/> has no such limit.
    /// </summary>
    public static void PaperFromBlackRender(Span<byte> pixels, int width, int height, int stride, int bytesPerPixel)
    {
        Validate(pixels.Length, width, height, stride, bytesPerPixel);
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + (x * bytesPerPixel);
                var max = Math.Max(pixels[i + 2], Math.Max(pixels[i + 1], pixels[i]));
                var min = Math.Min(pixels[i + 2], Math.Min(pixels[i + 1], pixels[i]));
                var saturation = max == 0 ? 0 : (max - min) / (double)max;
                if (saturation < 0.12)
                {
                    pixels[i] = (byte)(255 - pixels[i]);
                    pixels[i + 1] = (byte)(255 - pixels[i + 1]);
                    pixels[i + 2] = (byte)(255 - pixels[i + 2]);
                }
            }
        }
    }

    /// <summary>The ink colour of a full-brightness line colour: hue and saturation kept, value 0.4 x saturation (white gives black).</summary>
    public static (double B, double G, double R) Ink(double b, double g, double r)
    {
        var max = Math.Max(r, Math.Max(g, b));
        if (max <= 0)
        {
            return (0, 0, 0);
        }

        var min = Math.Min(r, Math.Min(g, b));
        var saturation = (max - min) / max;
        var value = 0.4 * saturation;
        var scale = value * 255.0 / max;
        return (b * scale, g * scale, r * scale);
    }

    private static byte Channel(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static void Validate(int length, int width, int height, int stride, int bytesPerPixel)
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
