using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests;

public sealed class ToolKeywordsTests
{
    private static readonly IReadOnlyList<string> Kinds = ["walls", "doors", "windows", "spaces", "furniture"];

    [Theory]
    [InlineData("Wall Styles", "wall_styles")]
    [InlineData("  Door-Style ", "door_style")]
    [InlineData("3D", "3d")]
    [InlineData("AEC_WALL", "aec_wall")]
    [InlineData("a  --  b", "a_b")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeKey_LowercasesAndUnderscores(string? input, string expected) =>
        Assert.Equal(expected, ToolKeywords.NormalizeKey(input));

    [Theory]
    [InlineData("door", "doors")]
    [InlineData("Doors", "doors")]
    [InlineData("WINDOW", "windows")]
    [InlineData("furniture", "furniture")]
    [InlineData("space", "spaces")]
    public void MatchKeyword_AcceptsSingularAndPlural(string input, string expected) =>
        Assert.Equal(expected, ToolKeywords.MatchKeyword(input, Kinds));

    [Theory]
    [InlineData("toilets")]
    [InlineData("")]
    [InlineData("   ")]
    public void MatchKeyword_ReturnsNullForUnknown(string input) =>
        Assert.Null(ToolKeywords.MatchKeyword(input, Kinds));

    [Theory]
    [InlineData(null, null, 0, 50)]
    [InlineData(10L, 20L, 10, 20)]
    [InlineData(-5L, 0L, 0, 1)]
    [InlineData(0L, 9999L, 0, 200)]
    public void Page_ClampsOffsetAndLimit(long? offset, long? limit, int expectedOffset, int expectedLimit) =>
        Assert.Equal((expectedOffset, expectedLimit), ToolKeywords.Page(offset, limit, ToolKeywords.DefaultPageSize, 200));

    [Theory]
    [InlineData(100, 0, 50, 50)]
    [InlineData(100, 50, 50, null)]
    [InlineData(0, 0, 0, null)]
    [InlineData(10, 5, 3, 8)]
    public void NextOffset_PointsToTheNextPageOrNull(int total, int offset, int returned, int? expected) =>
        Assert.Equal(expected, ToolKeywords.NextOffset(total, offset, returned));

    [Theory]
    [InlineData("Standard Wall", "wall", true)]
    [InlineData("Standard Wall", "", true)]
    [InlineData("Standard Wall", null, true)]
    [InlineData(null, "x", false)]
    [InlineData("Door", "window", false)]
    public void ContainsText_IsCaseInsensitive(string? text, string? filter, bool expected) =>
        Assert.Equal(expected, ToolKeywords.ContainsText(text, filter));
}
