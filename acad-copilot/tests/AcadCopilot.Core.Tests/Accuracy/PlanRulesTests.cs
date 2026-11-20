using AcadCopilot.Core.Accuracy;

namespace AcadCopilot.Core.Tests.Accuracy;

public sealed class PlanRulesTests
{
    [Fact]
    public void Defaults_AreTheIndianResidentialSizes()
    {
        var rules = PlanRules.Default;
        Assert.Equal(new RoomMinimum(120, 120), rules.RoomMinimums["bedroom"]);
        Assert.Equal(new RoomMinimum(132, 144), rules.RoomMinimums["master_bedroom"]);
        Assert.Equal(new RoomMinimum(132, 144), rules.RoomMinimums["living"]);
        Assert.Equal(new RoomMinimum(84, 120), rules.RoomMinimums["kitchen"]);
        Assert.Equal(new RoomMinimum(54, 84), rules.RoomMinimums["toilet"]);
        Assert.Equal(new RoomMinimum(60, 84), rules.RoomMinimums["bath_wc"]);
        Assert.Equal(new RoomMinimum(96, 120), rules.RoomMinimums["dining"]);
        Assert.Equal(new RoomMinimum(108, 204), rules.RoomMinimums["parking"]);
        Assert.Equal(39, rules.StairWidth);
        Assert.Equal(6.5, rules.RiserTypical);
        Assert.Equal(10, rules.TreadMin);
        Assert.Equal(42, rules.PassageWidth);
        Assert.Equal((42, 36, 30), (rules.MainDoor, rules.RoomDoor, rules.ToiletDoor));
        Assert.Equal((0.10, 0.125), (rules.WindowRatioMin, rules.WindowRatioGood));
        Assert.Equal((9, 4.5), (rules.OuterWall, rules.InnerWall));
        Assert.Equal((120, 126), (rules.FloorHeightMin, rules.FloorHeightMax));
        Assert.False(rules.Vastu);
    }

    [Theory]
    [InlineData("Toilets at least 5'x7' clear", "toilet", 60, 84)]
    [InlineData("Toilets at least 5'x7' clear", "bath_wc", 60, 84)]
    [InlineData("bedrooms 10x11 minimum", "bedroom", 120, 132)]
    [InlineData("Master bedroom at least 12'x14'", "master_bedroom", 144, 168)]
    [InlineData("Kitchen minimum 8 ft by 10 ft", "kitchen", 96, 120)]
    [InlineData("Living room at least 12'6\" x 14'", "living", 150, 168)]
    [InlineData("Bathrooms 4'9\"x7'", "bath_wc", 57, 84)]
    [InlineData("Parking 10' x 18' per car", "parking", 120, 216)]
    public void RoomRules_ChangeTheMinimums(string rule, string type, double shortSide, double longSide)
    {
        var rules = PlanRules.FromRules([rule]);
        Assert.Equal(new RoomMinimum(shortSide, longSide), rules.RoomMinimums[type]);
        Assert.Equal([rule], rules.Applied);
    }

    [Fact]
    public void DoorWallStairAndHeightRules_AreRead()
    {
        var rules = PlanRules.FromRules(
        [
            "Main door 4' wide",
            "Toilet door 2'4\"",
            "Bedroom doors 2'9\"",
            "Riser at most 7.5\"",
            "Tread 11\" minimum",
            "Stair width 3'6\"",
            "Passages at least 4'",
            "Outer walls 9 inch",
            "Inner walls 6\"",
            "Floor height 11'",
        ]);
        Assert.Equal(48, rules.MainDoor);
        Assert.Equal(28, rules.ToiletDoor);
        Assert.Equal(33, rules.RoomDoor);
        Assert.Equal(7.5, rules.RiserMax);
        Assert.Equal(11, rules.TreadMin);
        Assert.Equal(42, rules.StairWidth);
        Assert.Equal(48, rules.PassageWidth);
        Assert.Equal(9, rules.OuterWall);
        Assert.Equal(6, rules.InnerWall);
        Assert.Equal(132, rules.FloorHeightMax);
        Assert.Equal(10, rules.Applied.Count);
    }

    [Theory]
    [InlineData("Follow Vastu", true)]
    [InlineData("Plans must be Vastu compliant", true)]
    [InlineData("No Vastu please", false)]
    [InlineData("I don't follow vastu", false)]
    [InlineData("Ignore vastu", false)]
    public void Vastu_IsSwitchedByRules(string rule, bool expected)
    {
        Assert.Equal(expected, PlanRules.FromRules([rule]).Vastu);
    }

    [Fact]
    public void UnrelatedRules_AreIgnored()
    {
        var rules = PlanRules.FromRules(["Dimensions outside the walls", "Kitchen on the south-east", "Text height 9 for room names"]);
        Assert.Empty(rules.Applied);
        Assert.Equal(PlanRules.DefaultRoomMinimums["kitchen"], rules.RoomMinimums["kitchen"]);
    }

    [Theory]
    [InlineData("10'", true, 120)]
    [InlineData("10'6", true, 126)]
    [InlineData("10'-6\"", true, 126)]
    [InlineData("4.5\"", false, 4.5)]
    [InlineData("10 ft", true, 120)]
    [InlineData("9 inch", true, 9)]
    [InlineData("12", true, 144)]
    [InlineData("12", false, 12)]
    public void ParseLength_ReadsFeetAndInches(string text, bool feetDefault, double inches)
    {
        Assert.Equal(inches, PlanRules.ParseLength(text, feetDefault));
    }
}
