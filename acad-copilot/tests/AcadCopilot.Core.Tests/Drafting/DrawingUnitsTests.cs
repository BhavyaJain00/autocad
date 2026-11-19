using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed class DrawingUnitsTests
{
    [Fact]
    public void Architectural_units_are_inches_and_accept_feet_inch_input()
    {
        var units = new DrawingUnits { LUnits = 4, InsUnits = 1 };
        Assert.True(units.AcceptsFeetInches);
        Assert.Equal(1.0, units.InchesPerUnit);
        Assert.Equal("30'-0\"", units.ToFeetInches(360));
        Assert.Equal("360 (30'-0\")", units.LengthText(360));
        Assert.Contains("architectural", units.Describe());
        Assert.Contains("feet-inch input", units.Describe());
    }

    [Fact]
    public void Engineering_units_are_inches_even_when_insunits_says_feet()
    {
        var units = new DrawingUnits { LUnits = 3, InsUnits = 2 };
        Assert.Equal(1.0, units.InchesPerUnit);
        Assert.True(units.AcceptsFeetInches);
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 12.0)]
    [InlineData(4, 1.0 / 25.4)]
    [InlineData(5, 1.0 / 2.54)]
    [InlineData(6, 1000.0 / 25.4)]
    public void Decimal_units_follow_insunits(int insUnits, double inchesPerUnit)
    {
        var units = new DrawingUnits { LUnits = 2, InsUnits = insUnits };
        Assert.False(units.AcceptsFeetInches);
        Assert.Equal(inchesPerUnit, units.InchesPerUnit!.Value, 9);
    }

    [Fact]
    public void Millimetre_drawing_reports_feet_inches()
    {
        var units = new DrawingUnits { LUnits = 2, InsUnits = 4 };
        Assert.Equal("10'-0\"", units.ToFeetInches(3048));
        Assert.Equal("millimetre", units.UnitName);
    }

    [Fact]
    public void Unitless_decimal_drawing_has_no_feet_inches()
    {
        var units = new DrawingUnits { LUnits = 2, InsUnits = 0 };
        Assert.Null(units.InchesPerUnit);
        Assert.Null(units.ToFeetInches(100));
        Assert.Null(units.ToSquareFeet(100));
        Assert.Equal("100", units.LengthText(100));
        var json = units.Length(100);
        Assert.Equal(100, json["du"]!.GetValue<double>());
        Assert.False(json.ContainsKey("ftIn"));
        Assert.Contains("not set", units.Describe());
    }

    [Fact]
    public void Length_json_has_drawing_units_and_feet_inches()
    {
        var units = new DrawingUnits { LUnits = 4, InsUnits = 1 };
        var json = units.Length(150.5);
        Assert.Equal(150.5, json["du"]!.GetValue<double>());
        Assert.Equal("12'-6 1/2\"", json["ftIn"]!.GetValue<string>());
    }

    [Fact]
    public void Square_feet_from_square_inches()
    {
        var units = new DrawingUnits { LUnits = 4, InsUnits = 1 };
        Assert.Equal(1200.0, units.ToSquareFeet(360 * 480));
    }

    [Fact]
    public void Round_drops_noise_and_negative_zero()
    {
        Assert.Equal(1.234568, DrawingUnits.Round(1.23456789));
        Assert.Equal(0.0, DrawingUnits.Round(-0.0000001));
        Assert.False(double.IsNegative(DrawingUnits.Round(-0.0000001)));
    }
}
