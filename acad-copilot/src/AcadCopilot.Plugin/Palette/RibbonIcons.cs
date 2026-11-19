using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AcadCopilot.Plugin.Palette;

/// <summary>Draws the ribbon and palette icons in code (no image files to ship): a rounded tile with a spark and "AI".</summary>
internal static class RibbonIcons
{
    /// <summary>Renders the icon at <paramref name="size"/> x <paramref name="size"/> pixels (16 small, 32 large ribbon button).</summary>
    public static BitmapSource Create(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var tile = new RectangleGeometry(new Rect(0.5, 0.5, size - 1, size - 1), size * 0.2, size * 0.2);
            var fill = new LinearGradientBrush(Color.FromRgb(0x5B, 0x5B, 0xF0), Color.FromRgb(0x2E, 0x8B, 0xE6), 45);
            fill.Freeze();
            dc.DrawGeometry(fill, null, tile);
            var text = new FormattedText(
                "AI",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.5,
                Brushes.White,
                1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2 + size * 0.04));
            var spark = size * 0.14;
            var center = new Point(size * 0.8, size * 0.2);
            var star = new StreamGeometry();
            using (var g = star.Open())
            {
                g.BeginFigure(new Point(center.X, center.Y - spark), true, true);
                g.LineTo(new Point(center.X + spark * 0.3, center.Y - spark * 0.3), true, false);
                g.LineTo(new Point(center.X + spark, center.Y), true, false);
                g.LineTo(new Point(center.X + spark * 0.3, center.Y + spark * 0.3), true, false);
                g.LineTo(new Point(center.X, center.Y + spark), true, false);
                g.LineTo(new Point(center.X - spark * 0.3, center.Y + spark * 0.3), true, false);
                g.LineTo(new Point(center.X - spark, center.Y), true, false);
                g.LineTo(new Point(center.X - spark * 0.3, center.Y - spark * 0.3), true, false);
            }

            star.Freeze();
            dc.DrawGeometry(Brushes.White, null, star);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
