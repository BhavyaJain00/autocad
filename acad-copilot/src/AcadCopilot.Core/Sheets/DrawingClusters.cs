using System.Globalization;

namespace AcadCopilot.Core.Sheets;

/// <summary>One model-space object as the cluster finder sees it.</summary>
/// <param name="Box">Its extents (drawing units).</param>
/// <param name="Text">Its text for TEXT / MTEXT / attributes (null for other objects).</param>
/// <param name="TextHeight">Text height (0 for other objects).</param>
/// <param name="IsAnnotation">Text, dimension, leader, or an object on an annotation layer (A-ANNO-*, *DIM*, *TEXT*).</param>
/// <param name="IsWall">On a wall layer (A-WALL, *WALL*): the walls anchor a plan when plans are lined up.</param>
public sealed record ClusterItem(SheetRect Box, string? Text = null, double TextHeight = 0, bool IsAnnotation = false, bool IsWall = false);

/// <summary>One separate drawing found in model space.</summary>
public sealed record DrawingCluster
{
    /// <summary>1-based number in reading order (rows top to bottom, left to right in a row).</summary>
    public int Number { get; init; }

    /// <summary>Indexes of its objects in the item list given to <see cref="DrawingClusters.Find"/>.</summary>
    public required IReadOnlyList<int> Items { get; init; }

    /// <summary>Extents of all its objects.</summary>
    public SheetRect Box { get; init; }

    /// <summary>
    /// The part used to line drawings up: the walls (wall layers) when there are any, else the non-annotation objects,
    /// else <see cref="Box"/>. Plans placed with the same anchor spacing keep walls and stairs aligned between floors.
    /// </summary>
    public SheetRect Anchor { get; init; }

    /// <summary>The recognised title (from a title text in the drawing or a label given by the AI), or null.</summary>
    public DrawingTitleInfo? Title { get; init; }

    /// <summary>Index (in the item list) of the title text, or null (no title text, or the title came from a label).</summary>
    public int? TitleItem { get; init; }

    /// <summary>Index (in the item list) of the scale line under the title, or null.</summary>
    public int? ScaleItem { get; init; }

    /// <summary>The kind (Other when untitled).</summary>
    public DrawingKind Kind => Title?.Kind ?? DrawingKind.Other;

    /// <summary>Sort order within the kind (floor level for plans).</summary>
    public double Order => Title?.Order ?? 0;

    /// <summary>The title, or "DRAWING n" when untitled.</summary>
    public string Name => Title?.Title ?? string.Create(CultureInfo.InvariantCulture, $"DRAWING {Number}");
}

/// <summary>The drawings found in model space, plus notes about what could not be separated.</summary>
/// <param name="Clusters">The drawings in reading order.</param>
/// <param name="Warnings">Problems (two titles in one group that could not be split ...).</param>
public sealed record ClusterResult(IReadOnlyList<DrawingCluster> Clusters, IReadOnlyList<string> Warnings);

/// <summary>A label the AI gives to a drawing: a point inside it (or near it) and its title.</summary>
/// <param name="X">X of a point inside the drawing.</param>
/// <param name="Y">Y of a point inside the drawing.</param>
/// <param name="Title">The title, e.g. "FIRST FLOOR PLAN".</param>
public sealed record DrawingLabel(double X, double Y, string Title);

/// <summary>
/// Finds the separate drawings in model space: groups of objects separated by empty space wider than a gap
/// (default <see cref="DefaultGapInches"/>), split further where one group holds two different titles, with small
/// untitled groups next to a titled drawing (north arrow, notes) joined to it. Pure.
/// </summary>
public static class DrawingClusters
{
    /// <summary>Default empty space that separates two drawings: 10'-0" (drawings are drawn at least 20' apart).</summary>
    public const double DefaultGapInches = 120;

    /// <summary>A text counts as the title when it is at least this fraction of the drawing's largest text height.</summary>
    public const double TitleHeightFraction = 0.75;

    /// <summary>An untitled group smaller than this fraction of the nearest titled drawing is joined to it.</summary>
    public const double OrphanSizeFraction = 0.35;

    /// <summary>... when it is closer than this many gaps.</summary>
    public const double OrphanDistanceGaps = 3;

    /// <summary>Finds the drawings among <paramref name="items"/>; <paramref name="gap"/> in drawing units (positive).</summary>
    public static ClusterResult Find(IReadOnlyList<ClusterItem> items, double gap)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!(gap > 0) || !double.IsFinite(gap))
        {
            throw new ArgumentOutOfRangeException(nameof(gap), "The gap must be a positive number of drawing units.");
        }

        var warnings = new List<string>();
        var valid = Enumerable.Range(0, items.Count).Where(i => items[i].Box.IsValid).ToList();
        var groups = Group(items, valid, gap);

        // Split groups that hold two different titles (drawings placed too close together).
        var split = new List<List<int>>();
        foreach (var group in groups)
        {
            if (DistinctTitles(items, group).Count <= 1)
            {
                split.Add(group);
                continue;
            }

            List<List<int>>? parts = null;
            for (var smaller = gap / 2; smaller >= gap / 16; smaller /= 2)
            {
                var candidate = Group(items, group, smaller);
                if (candidate.All(g => DistinctTitles(items, g).Count <= 1))
                {
                    parts = candidate;
                    break;
                }
            }

            if (parts is null)
            {
                warnings.Add($"The drawings {string.Join(", ", DistinctTitles(items, group))} touch or overlap, so they are treated as one; move them apart (or give labels) and try again.");
                split.Add(group);
            }
            else
            {
                split.AddRange(parts);
            }
        }

        var built = split.Select(g => Build(items, g)).ToList();
        built = MergeOrphans(items, built, gap);
        return new ClusterResult(Number(built), warnings);
    }

    /// <summary>
    /// Applies the AI's labels: each label names the drawing that contains its point (or the nearest one within
    /// <paramref name="reach"/>). Unrecognised titles make the drawing kind Other with that title. Returns the new list
    /// and the labels that matched no drawing.
    /// </summary>
    public static (IReadOnlyList<DrawingCluster> Clusters, IReadOnlyList<DrawingLabel> Unmatched) ApplyLabels(
        IReadOnlyList<DrawingCluster> clusters, IEnumerable<DrawingLabel> labels, double reach)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(labels);
        var result = clusters.ToList();
        var unmatched = new List<DrawingLabel>();
        foreach (var label in labels)
        {
            var point = new SheetRect(label.X, label.Y, label.X, label.Y);
            var best = -1;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < result.Count; i++)
            {
                var distance = result[i].Box.DistanceTo(point);
                if (distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }

            if (best < 0 || bestDistance > reach)
            {
                unmatched.Add(label);
                continue;
            }

            var title = DrawingTitles.Classify(label.Title) ?? new DrawingTitleInfo(DrawingKind.Other, 0, DrawingTitles.Clean(label.Title));
            result[best] = result[best] with { Title = title };
        }

        return (result, unmatched);
    }

    // Union-find over a sweep in X: two objects join when both the X gap and the Y gap between them are at most gap.
    private static List<List<int>> Group(IReadOnlyList<ClusterItem> items, IReadOnlyList<int> indices, double gap)
    {
        var order = indices.OrderBy(i => items[i].Box.MinX).ToArray();
        var parent = new int[order.Length];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Root(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        for (var a = 0; a < order.Length; a++)
        {
            var box = items[order[a]].Box;
            for (var b = a + 1; b < order.Length; b++)
            {
                var other = items[order[b]].Box;
                if (other.MinX > box.MaxX + gap)
                {
                    break;
                }

                if (other.MinY <= box.MaxY + gap && other.MaxY >= box.MinY - gap)
                {
                    var ra = Root(a);
                    var rb = Root(b);
                    if (ra != rb)
                    {
                        parent[rb] = ra;
                    }
                }
            }
        }

        return [.. Enumerable.Range(0, order.Length)
            .GroupBy(Root)
            .Select(g => g.Select(i => order[i]).Order().ToList())];
    }

    private static List<string> DistinctTitles(IReadOnlyList<ClusterItem> items, IReadOnlyList<int> group) =>
        [.. TitleCandidates(items, group).Select(c => c.Info.Title).Distinct(StringComparer.Ordinal)];

    private static List<(int Index, DrawingTitleInfo Info)> TitleCandidates(IReadOnlyList<ClusterItem> items, IReadOnlyList<int> group)
    {
        var maxHeight = group.Where(i => items[i].Text is not null).Select(i => items[i].TextHeight).DefaultIfEmpty(0).Max();
        var list = new List<(int, DrawingTitleInfo)>();
        foreach (var i in group)
        {
            var item = items[i];
            if (item.Text is null || item.TextHeight < maxHeight * TitleHeightFraction)
            {
                continue;
            }

            if (DrawingTitles.Classify(item.Text) is { } info)
            {
                list.Add((i, info));
            }
        }

        return list;
    }

    private static DrawingCluster Build(IReadOnlyList<ClusterItem> items, List<int> group)
    {
        var box = group.Select(i => items[i].Box).Aggregate((a, b) => a.Union(b));
        var walls = group.Where(i => items[i].IsWall).ToList();
        var bodies = group.Where(i => !items[i].IsAnnotation && items[i].Text is null).ToList();
        var anchor = walls.Count > 0
            ? walls.Select(i => items[i].Box).Aggregate((a, b) => a.Union(b))
            : bodies.Count > 0 ? bodies.Select(i => items[i].Box).Aggregate((a, b) => a.Union(b)) : box;

        var candidates = TitleCandidates(items, group);
        int? titleItem = null;
        DrawingTitleInfo? title = null;
        if (candidates.Count > 0)
        {
            var best = candidates
                .OrderByDescending(c => items[c.Index].TextHeight)
                .ThenBy(c => items[c.Index].Box.MinY)
                .First();
            titleItem = best.Index;
            title = best.Info;
        }

        int? scaleItem = null;
        var scales = group.Where(i => DrawingTitles.IsScaleText(items[i].Text)).ToList();
        if (scales.Count > 0)
        {
            var reference = titleItem is { } t ? items[t].Box : box;
            scaleItem = scales.OrderBy(i => items[i].Box.DistanceTo(reference)).First();
        }

        return new DrawingCluster { Items = group, Box = box, Anchor = anchor, Title = title, TitleItem = titleItem, ScaleItem = scaleItem };
    }

    private static List<DrawingCluster> MergeOrphans(IReadOnlyList<ClusterItem> items, List<DrawingCluster> clusters, double gap)
    {
        var titled = clusters.Where(c => c.Title is not null).ToList();
        if (titled.Count == 0)
        {
            return clusters;
        }

        var additions = new Dictionary<DrawingCluster, List<int>>(ReferenceEqualityComparer.Instance);
        var result = new List<DrawingCluster>();
        foreach (var cluster in clusters)
        {
            if (cluster.Title is not null)
            {
                continue;
            }

            var nearest = titled.OrderBy(t => t.Box.DistanceTo(cluster.Box)).First();
            var size = Math.Max(cluster.Box.Width, cluster.Box.Height);
            var nearestSize = Math.Max(nearest.Box.Width, nearest.Box.Height);
            if (size < nearestSize * OrphanSizeFraction && nearest.Box.DistanceTo(cluster.Box) <= gap * OrphanDistanceGaps)
            {
                if (!additions.TryGetValue(nearest, out var list))
                {
                    additions[nearest] = list = [];
                }

                list.AddRange(cluster.Items);
            }
            else
            {
                result.Add(cluster);
            }
        }

        foreach (var cluster in titled)
        {
            if (additions.TryGetValue(cluster, out var extra))
            {
                var merged = Build(items, [.. cluster.Items.Concat(extra).Order()]);

                // Keep the anchor of the titled drawing itself (a north arrow beside a plan must not shift the walls).
                result.Add(merged with { Anchor = cluster.Anchor, Title = cluster.Title, TitleItem = cluster.TitleItem });
            }
            else
            {
                result.Add(cluster);
            }
        }

        return result;
    }

    // Reading order: rows from the top (a drawing joins a row when its centre lies within the row's height), then X.
    private static List<DrawingCluster> Number(List<DrawingCluster> clusters)
    {
        var rows = new List<List<DrawingCluster>>();
        foreach (var cluster in clusters.OrderByDescending(c => c.Box.MaxY))
        {
            var row = rows.FirstOrDefault(r => r.Any(c => cluster.Box.CenterY >= c.Box.MinY && cluster.Box.CenterY <= c.Box.MaxY));
            if (row is null)
            {
                rows.Add([cluster]);
            }
            else
            {
                row.Add(cluster);
            }
        }

        var number = 1;
        return [.. rows.SelectMany(r => r.OrderBy(c => c.Box.MinX)).Select(c => c with { Number = number++ })];
    }
}
