using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>Dictated text -> drafting prompt text (pure).</summary>
public sealed class SpokenTextNormalizerTests
{
    [Theory]
    [InlineData("30 by 40 feet", "30' x 40'")]
    [InlineData("Draw a 30 by 40 feet plot.", "Draw a 30' x 40' plot.")]
    [InlineData("9 inch", "9\"")]
    [InlineData("9 inches", "9\"")]
    [InlineData("9 inch walls", "9\" walls")]
    [InlineData("four and a half inch", "4 1/2\"")]
    [InlineData("4 and a half inch partition", "4 1/2\" partition")]
    [InlineData("thirty by forty feet", "30' x 40'")]
    [InlineData("Thirty by forty feet east facing plot", "30' x 40' east-facing plot")]
    [InlineData("30 feet by 40 feet", "30' x 40'")]
    [InlineData("30 feet by 40", "30' x 40'")]
    [InlineData("30 x 40 ft", "30' x 40'")]
    [InlineData("30x40 feet", "30' x 40'")]
    [InlineData("30 into 40 feet", "30' x 40'")]
    [InlineData("a 12 by 10 by 9 feet room", "a 12' x 10' x 9' room")]
    [InlineData("10 feet 6 inches", "10'-6\"")]
    [InlineData("10 feet and 6 inches", "10'-6\"")]
    [InlineData("ten feet six inches by twelve feet", "10'-6\" x 12'")]
    [InlineData("two and a half feet", "2'-6\"")]
    [InlineData("one and a half feet wide", "1'-6\" wide")]
    [InlineData("2.5 feet", "2'-6\"")]
    [InlineData("4.5 inch", "4 1/2\"")]
    [InlineData("1/2 inch", "1/2\"")]
    [InlineData("half inch gap", "1/2\" gap")]
    [InlineData("three quarter inch", "3/4\"")]
    [InlineData("forty-five feet", "45'")]
    [InlineData("forty five feet", "45'")]
    [InlineData("a 9-inch brick wall", "a 9\" brick wall")]
    [InlineData("a 6 foot wide road", "a 6' wide road")]
    [InlineData("twelve hundred square feet", "1200 sq ft")]
    [InlineData("1,200 sq ft", "1200 sq ft")]
    [InlineData("1500 square feet house", "1500 sq ft house")]
    [InlineData("G plus one house", "G+1 house")]
    [InlineData("ground plus two", "G+2")]
    [InlineData("stair case near the set back", "staircase near the setback")]
    [InlineData("Bed room 12 by 14 feet", "Bedroom 12' x 14'")]
    [InlineData("open it in auto cad", "open it in AutoCAD")]
    [InlineData("3 by 4", "3 x 4")]
    public void Spoken_measures_become_drafting_text(string spoken, string expected) =>
        Assert.Equal(expected, SpokenTextNormalizer.Normalize(spoken));

    [Theory]
    [InlineData("2 bedrooms and 1 toilet")]
    [InlineData("one bedroom, one kitchen")]
    [InlineData("the 90's houses")]
    [InlineData("Add dimensions to the plan")]
    [InlineData("G+1 house")]
    [InlineData("2nd floor")]
    [InlineData("30' x 40' plot with 9\" walls and 4 1/2\" partitions, door 3'-6\", 1200 sq ft")]
    public void Other_text_and_typed_measures_stay(string text) =>
        Assert.Equal(text, SpokenTextNormalizer.Normalize(text));

    [Fact]
    public void Normalizing_twice_changes_nothing()
    {
        const string spoken = "Draw the ground floor of a thirty by forty feet east facing plot with nine inch outer walls, four and a half inch partitions and a 3 feet 6 inches main door, G plus one";
        var once = SpokenTextNormalizer.Normalize(spoken);
        Assert.Equal("Draw the ground floor of a 30' x 40' east-facing plot with 9\" outer walls, 4 1/2\" partitions and a 3'-6\" main door, G+1", once);
        Assert.Equal(once, SpokenTextNormalizer.Normalize(once));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Empty_text_is_empty(string? text, string expected) => Assert.Equal(expected, SpokenTextNormalizer.Normalize(text));
}
