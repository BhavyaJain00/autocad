using AcadCopilot.Core.Sheets;

namespace AcadCopilot.Core.Tests.Sheets;

/// <summary>Model-space objects for the drawing-set tests (inches: 1 unit = 1").</summary>
internal static class SheetTestData
{
    public static ClusterItem Wall(double x0, double y0, double x1, double y1) => new(SheetRect.FromCorners(x0, y0, x1, y1), IsWall: true);

    public static ClusterItem Line(double x0, double y0, double x1, double y1) => new(SheetRect.FromCorners(x0, y0, x1, y1));

    public static ClusterItem Dim(double x0, double y0, double x1, double y1) => new(SheetRect.FromCorners(x0, y0, x1, y1), IsAnnotation: true);

    public static ClusterItem Text(double cx, double cy, double height, string text) =>
        new(SheetRect.FromCenter(cx, cy, Math.Max(1, text.Length) * height * 0.8, height), text, height, IsAnnotation: true);

    /// <summary>
    /// A 30' x 40' plan with its lower-left outer wall corner at (x, y): four outer wall lines, an inner wall, a room
    /// name, a dimension 40" below, the title 90" below and the scale line under it (all gaps under 50").
    /// </summary>
    public static List<ClusterItem> Plan(double x, double y, string? title, bool scaleLine = true)
    {
        var items = new List<ClusterItem>
        {
            Wall(x, y, x + 360, y),
            Wall(x + 360, y, x + 360, y + 480),
            Wall(x + 360, y + 480, x, y + 480),
            Wall(x, y + 480, x, y),
            Wall(x + 180, y, x + 180, y + 300),
            Text(x + 90, y + 150, 9, "KITCHEN"),
            Dim(x, y - 44, x + 360, y - 36),
        };
        if (title is not null)
        {
            items.Add(Text(x + 180, y - 90, 12, "%%u" + title));
            if (scaleLine)
            {
                items.Add(Text(x + 180, y - 108, 6, "SCALE 1/4\" = 1'-0\""));
            }
        }

        return items;
    }
}
