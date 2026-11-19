using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed class FeetInchesTests
{
    [Theory]
    [InlineData(150.5, "12'-6 1/2\"")]
    [InlineData(360, "30'-0\"")]
    [InlineData(9, "0'-9\"")]
    [InlineData(4.5, "0'-4 1/2\"")]
    [InlineData(0, "0'-0\"")]
    [InlineData(11.99, "1'-0\"")]
    [InlineData(-6, "-0'-6\"")]
    [InlineData(12.0625, "1'-0 1/16\"")]
    [InlineData(480.75, "40'-0 3/4\"")]
    [InlineData(0.001, "0'-0\"")]
    public void Format_writes_architectural_text(double inches, string expected)
    {
        Assert.Equal(expected, FeetInches.Format(inches));
    }

    [Fact]
    public void Format_respects_the_denominator()
    {
        Assert.Equal("0'-4 1/2\"", FeetInches.Format(4.4, denominator: 2));
        Assert.Equal("0'-4\"", FeetInches.Format(4.2, denominator: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeetInches.Format(1, denominator: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeetInches.Format(double.NaN));
    }

    [Theory]
    [InlineData(150.5, "12'6-1/2\"")]
    [InlineData(360, "30'")]
    [InlineData(9, "9\"")]
    [InlineData(4.5, "4-1/2\"")]
    [InlineData(0.5, "1/2\"")]
    [InlineData(0, "0\"")]
    [InlineData(-4.5, "-4-1/2\"")]
    [InlineData(366, "30'6\"")]
    public void FormatForInput_has_no_spaces(double inches, string expected)
    {
        var text = FeetInches.FormatForInput(inches);
        Assert.Equal(expected, text);
        Assert.DoesNotContain(' ', text);
    }

    [Theory]
    [InlineData("12'6\"", 150)]
    [InlineData("12'-6\"", 150)]
    [InlineData("12' 6\"", 150)]
    [InlineData("12'-6 1/2\"", 150.5)]
    [InlineData("12'6-1/2\"", 150.5)]
    [InlineData("12'6", 150)]
    [InlineData("12'", 144)]
    [InlineData("12.5'", 150)]
    [InlineData("6\"", 6)]
    [InlineData("6 1/2\"", 6.5)]
    [InlineData("6-1/2\"", 6.5)]
    [InlineData("1/2\"", 0.5)]
    [InlineData("6.25in", 6.25)]
    [InlineData("12ft 6in", 150)]
    [InlineData("12 ft 6 in", 150)]
    [InlineData("150", 150)]
    [InlineData("-3'-6\"", -42)]
    [InlineData("9''", 9)]
    [InlineData("30′", 360)]
    [InlineData("6″", 6)]
    public void TryParse_reads_common_forms(string text, double expected)
    {
        Assert.True(FeetInches.TryParse(text, out var inches), text);
        Assert.Equal(expected, inches, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12'6\"x")]
    [InlineData("1/0\"")]
    [InlineData("'6")]
    [InlineData("12'-")]
    public void TryParse_rejects_other_text(string? text)
    {
        Assert.False(FeetInches.TryParse(text, out _));
    }

    [Theory]
    [InlineData(150.5)]
    [InlineData(360)]
    [InlineData(9)]
    [InlineData(4.5)]
    [InlineData(0.0625)]
    public void Formatted_text_parses_back(double inches)
    {
        Assert.True(FeetInches.TryParse(FeetInches.Format(inches), out var fromDisplay));
        Assert.Equal(inches, fromDisplay, 6);
        Assert.True(FeetInches.TryParse(FeetInches.FormatForInput(inches), out var fromInput));
        Assert.Equal(inches, fromInput, 6);
    }

    [Theory]
    [InlineData("@30'<0", true)]
    [InlineData("12'6\",0", true)]
    [InlineData("4-1/2\"", true)]
    [InlineData("360,480", false)]
    [InlineData("MASTER'S BED", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsFeetInchNotation_spots_marks_after_digits(string? input, bool expected)
    {
        Assert.Equal(expected, FeetInches.ContainsFeetInchNotation(input));
    }
}
