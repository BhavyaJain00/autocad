using System.Text.Json.Nodes;
using AcadCopilot.Core.Accuracy;
using static AcadCopilot.Core.Tests.Accuracy.PlanBriefs;

namespace AcadCopilot.Core.Tests.Accuracy;

public sealed class PlanValidatorTests
{
    private static List<PlanFinding> Errors(PlanValidation v) => [.. v.Findings.Where(f => f.Severity == Severity.Error)];

    private static List<PlanFinding> Warnings(PlanValidation v) => [.. v.Findings.Where(f => f.Severity == Severity.Warning)];

    private static bool Has(PlanValidation v, string code, string? text = null) =>
        v.Findings.Any(f => f.Code == code && (text is null || f.Message.Contains(text, StringComparison.Ordinal)));

    [Fact]
    public void ThirtyForty_G1_IsReadyWithoutWarnings()
    {
        var v = Validate(ThirtyForty());
        Assert.True(v.IsReady, Describe(v));
        Assert.Empty(Warnings(v));
        Assert.Equal(2, v.Plan!.Floors.Count);
        Assert.Contains(v.Findings, f => f.Code == "headroom");
        Assert.Contains(v.Findings, f => f.Code == "half_inch" && f.Message.Contains("Kitchen", StringComparison.Ordinal));
    }

    [Fact]
    public void ThirtyForty_GroundOnly_IsReady()
    {
        var v = Validate(ThirtyForty(floors: 1));
        Assert.True(v.IsReady, Describe(v));
    }

    [Fact]
    public void ThirtyForty_G2_StacksThreeFloors()
    {
        var v = Validate(ThirtyForty(floors: 3));
        Assert.True(v.IsReady, Describe(v));
        Assert.Empty(Warnings(v));
        Assert.Equal(3, v.Schedule["floors"]!.AsArray().Count);
    }

    [Fact]
    public void ThirtyFifty_WithParkingInTheSetback_IsReady()
    {
        var v = Validate(ThirtyFifty());
        Assert.True(v.IsReady, Describe(v));
        Assert.Empty(Warnings(v));
        Assert.Contains(v.Findings, f => f.Code == "assumed" && f.Message.Contains("10'-6\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedule_GivesSizesAreasChainsAndTotals()
    {
        var v = Validate(ThirtyForty());
        var ground = v.Schedule["floors"]![0]!.AsObject();
        Assert.Equal("24'-0\" x 32'-0\"", ground["outline"]!.ToString());
        var living = ground["rooms"]!.AsArray().First(r => r!["name"]!.ToString() == "Living")!;
        Assert.Equal("13'-0\" x 15'-0\"", living["size"]!.ToString());
        Assert.Equal(195, living["areaSqFt"]!.GetValue<double>());

        var south = ground["chains"]!["south"]!["segments"]!.AsArray().Select(s => s!.ToString()).ToList();
        Assert.Equal(["0'-9\"", "13'-0\"", "0'-4 1/2\"", "9'-1 1/2\"", "0'-9\""], south);
        Assert.Equal("24'-0\"", ground["chains"]!["south"]!["total"]!.ToString());
        var west = ground["chains"]!["west"]!["segments"]!.AsArray().Select(s => s!.ToString()).ToList();
        Assert.Equal(["0'-9\"", "15'-0\"", "0'-4 1/2\"", "15'-1 1/2\"", "0'-9\""], west);

        var totals = v.Schedule["totals"]!.AsObject();
        Assert.Equal(1536, totals["builtUpSqFt"]!.GetValue<double>());
        Assert.Equal(64, totals["groundCoveragePercent"]!.GetValue<double>());
        Assert.Equal(1.28, totals["far"]!.GetValue<double>());

        var door = ground["doors"]!.AsArray().First(d => d!["tag"]!.ToString() == "D1")!;
        Assert.Equal("Kitchen", door["swingsInto"]!.ToString());
        Assert.Equal("Living / Kitchen", door["between"]!.ToString());
        Assert.Equal("17'1-1/2\",15'1\"", door["hingePoint"]!.ToString());
        Assert.Contains("| Living | 13'-0\" x 15'-0\" | 195 sq ft |", v.Summary, StringComparison.Ordinal);
        Assert.Contains("Checks: 0 errors, 0 warnings", v.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Gap_IsFoundAndPlaced()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "rooms", "name", "Living")["width"] = "12'6";
        var v = Validate(plan);
        Assert.False(v.IsReady);
        Assert.True(Has(v, "gap", "Living"), Describe(v));
        Assert.Contains(Errors(v), f => f.Code == "gap" && f.Message.Contains("0'-6\" x 15'-0\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Overlap_WithRoomAndWall_IsFound()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "rooms", "name", "Kitchen")["depth"] = "10'6";
        var v = Validate(plan);
        Assert.True(Has(v, "room_in_wall", "Kitchen runs"), Describe(v));
        Assert.True(Has(v, "overlap", "Kitchen and Stair"), Describe(v));
    }

    [Fact]
    public void RoomOutsideOutline_IsAnError()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "rooms", "name", "Toilet 1")["depth"] = "7'";
        var v = Validate(plan);
        Assert.True(Has(v, "outside_outline", "Toilet 1"), Describe(v));
    }

    [Fact]
    public void RoomWithoutDoor_IsUnreachable()
    {
        var plan = ThirtyForty(floors: 1);
        plan.Floor(0)["doors"]!.AsArray().Remove(Item(plan, 0, "doors", "tag", "D3"));
        var v = Validate(plan);
        Assert.True(Has(v, "unreachable", "Toilet 1 cannot be reached from the entrance"), Describe(v));
    }

    [Fact]
    public void UpperFloor_IsReachedFromTheStairOnly()
    {
        var plan = ThirtyForty();
        plan.Floor(1)["doors"]!.AsArray().Remove(Item(plan, 1, "doors", "tag", "D5"));
        var v = Validate(plan);
        Assert.True(Has(v, "unreachable", "Bedroom 3 cannot be reached from the stair"), Describe(v));
        Assert.True(Has(v, "unreachable", "Toilet 3"), Describe(v));
    }

    [Fact]
    public void NoEntrance_IsAnError()
    {
        var plan = ThirtyForty(floors: 1);
        plan.Floor(0)["doors"]!.AsArray().Remove(Item(plan, 0, "doors", "tag", "MD"));
        var v = Validate(plan);
        Assert.True(Has(v, "no_entrance"), Describe(v));
        Assert.True(Has(v, "unreachable", "Living"), Describe(v));
    }

    [Fact]
    public void DoorSwingsThatCross_Clash()
    {
        var plan = ThirtyFifty();
        Item(plan, 0, "doors", "tag", "D2")["swing_into"] = "Passage";
        Item(plan, 0, "doors", "tag", "D4")["swing_into"] = "Passage";
        Item(plan, 0, "doors", "tag", "D4")["offset"] = PlanJson.L(190 - 40.5);
        var v = Validate(plan);
        Assert.True(Has(v, "door_clash", "D2 and D4"), Describe(v));
        Assert.True(Has(v, "swing_into_passage", "D2"), Describe(v));
    }

    [Fact]
    public void DoorSwing_ThatHitsAWall_IsFound()
    {
        var plan = ThirtyFifty();
        var floor = plan.Floor(0);
        floor["doors"]!.AsArray().Add(new JsonObject { ["tag"] = "DX", ["wall"] = "HB", ["offset"] = PlanJson.L(240 - 225.75), ["width"] = "5'", ["swing_into"] = "Common Toilet" });
        var v = Validate(plan);
        Assert.True(Has(v, "swing_hits_wall", "DX"), Describe(v));
    }

    [Fact]
    public void SwingIntoARoomNotBesideTheDoor_IsAnError()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "doors", "tag", "D1")["swing_into"] = "Bedroom 1";
        var v = Validate(plan);
        Assert.True(Has(v, "swing_room", "D1 swings into 'Bedroom 1'"), Describe(v));
    }

    [Fact]
    public void HingeFarFromACorner_IsAWarning()
    {
        var plan = ThirtyForty(floors: 1);
        var d2 = Item(plan, 0, "doors", "tag", "D2");
        d2["offset"] = PlanJson.L(120 - 40.5);
        d2.Remove("hinge");
        var v = Validate(plan);
        Assert.True(v.IsReady, Describe(v));
        Assert.True(Has(v, "hinge_far", "D2"), Describe(v));
    }

    [Fact]
    public void OpeningsThatOverlapOrCutAJunction_AreErrors()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "windows", "tag", "W1")["offset"] = PlanJson.L(80 - 40.5);
        Item(plan, 0, "doors", "tag", "D1")["offset"] = PlanJson.L(184.5 - 64.5);
        var v = Validate(plan);
        Assert.True(Has(v, "opening_overlap", "MD and W1"), Describe(v));
        Assert.True(Has(v, "opening_in_junction", "D1 on wall V1 cuts into wall HB1"), Describe(v));
    }

    [Fact]
    public void OpeningPastTheWallEnd_IsAnError()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "windows", "tag", "W2")["offset"] = "23'";
        var v = Validate(plan);
        Assert.True(Has(v, "opening_off_wall", "W2"), Describe(v));
    }

    [Fact]
    public void HabitableRoomWithoutWindow_AndToiletWithoutVentilator_AreErrors()
    {
        var plan = ThirtyForty(floors: 1);
        var windows = plan.Floor(0)["windows"]!.AsArray();
        windows.Remove(Item(plan, 0, "windows", "tag", "W4"));
        windows.Remove(Item(plan, 0, "windows", "tag", "VT1"));
        windows.Remove(Item(plan, 0, "windows", "tag", "W1"));
        var v = Validate(plan);
        Assert.True(Has(v, "kitchen_window", "Kitchen"), Describe(v));
        Assert.True(Has(v, "no_ventilation", "Toilet 1"), Describe(v));
        Assert.True(Has(v, "no_window", "Living"), Describe(v));
    }

    [Fact]
    public void ToiletOnAShaft_IsOnlyAHint()
    {
        var plan = ThirtyForty(floors: 1);
        plan.Floor(0)["windows"]!.AsArray().Remove(Item(plan, 0, "windows", "tag", "VT1"));
        Item(plan, 0, "rooms", "name", "Toilet 1")["ventilation"] = "shaft";
        var v = Validate(plan);
        Assert.True(v.IsReady, Describe(v));
        Assert.True(Has(v, "mechanical_ventilation", "Toilet 1"));
    }

    [Fact]
    public void SmallWindow_IsAWarningWithTheTarget()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "windows", "tag", "W1")["width"] = "3'";
        var v = Validate(plan);
        Assert.True(v.IsReady, Describe(v));
        Assert.True(Has(v, "window_area", "Living: windows 12 sq ft for 195 sq ft of floor (1/16)"), Describe(v));
    }

    [Fact]
    public void Stair_WrongRiserCountAndSteepRiser_AreErrors()
    {
        var plan = ThirtyForty();
        var stair = plan.Floor(0)["stair"]!.AsObject();
        stair["risers"] = 18;
        var v = Validate(plan);
        Assert.True(Has(v, "stair_rise", "Use 18 risers of 0'-6 11/16\""), Describe(v));

        stair["riser"] = "7-1/2";
        stair["risers"] = 16;
        v = Validate(plan);
        Assert.True(Has(v, "riser", "Riser 0'-7 1/2\""), Describe(v));
    }

    [Fact]
    public void Stair_NarrowFlightTreadAndFit_AreChecked()
    {
        var plan = ThirtyForty();
        var stair = plan.Floor(0)["stair"]!.AsObject();
        stair["flight_width"] = "2'9";
        stair["tread"] = "9";
        stair["depth"] = "9'";
        var v = Validate(plan);
        Assert.True(Has(v, "stair_width", "too narrow"), Describe(v));
        Assert.True(Has(v, "tread", "less than 0'-10\""), Describe(v));
        Assert.True(Has(v, "stair_fit"), Describe(v));
    }

    [Fact]
    public void TwoFloorsWithoutAStair_IsAnError()
    {
        var plan = ThirtyForty();
        plan.Floor(0).Remove("stair");
        Item(plan, 0, "rooms", "name", "Stair")["type"] = "store";
        Item(plan, 1, "rooms", "name", "Stair")["type"] = "store";
        var v = Validate(plan);
        Assert.True(Has(v, "stair_missing"), Describe(v));
    }

    [Fact]
    public void UpperStairNotAboveTheLowerStair_IsAnError()
    {
        var plan = ThirtyForty();
        var stair = Item(plan, 1, "rooms", "name", "Stair");
        stair["y"] = PlanJson.L(205.5);
        stair["depth"] = PlanJson.L(165);
        var v = Validate(plan);
        Assert.True(Has(v, "stair_stack", "First floor"), Describe(v));
        Assert.True(Has(v, "gap"), Describe(v));
    }

    [Fact]
    public void ToiletOverADryRoom_IsAWetStackWarning()
    {
        var plan = ThirtyForty();
        Item(plan, 0, "rooms", "name", "Kitchen")["type"] = "store";
        var v = Validate(plan);
        Assert.True(Has(v, "wet_stack", "Toilet 2 is not above a wet area of Ground floor (it sits over Kitchen)"), Describe(v));
    }

    [Fact]
    public void UpperFloorProjection_WithoutWallsBelow_IsWarned()
    {
        // The first floor projects 2'-0" to the north over the rear setback (outline, outer walls, rooms moved).
        var plan = ThirtyForty();
        var first = plan.Floor(1);
        first["outline"] = new JsonArray(PlanJson.P(36, 60), PlanJson.P(324, 60), PlanJson.P(324, 468), PlanJson.P(36, 468));
        JsonObject Wall(string id) => first["walls"]!.AsArray().Select(w => w!.AsObject()).First(w => w["id"]!.ToString() == id);
        Wall("OE")["to"] = PlanJson.P(319.5, 463.5);
        Wall("OW")["to"] = PlanJson.P(40.5, 463.5);
        Wall("V1")["to"] = PlanJson.P(203.25, 463.5);
        Wall("ON")["from"] = PlanJson.P(40.5, 463.5);
        Wall("ON")["to"] = PlanJson.P(319.5, 463.5);
        Item(plan, 1, "rooms", "name", "Bedroom 3")["depth"] = PlanJson.L(205.5);
        Item(plan, 1, "rooms", "name", "Toilet 3")["depth"] = PlanJson.L(84);
        plan.Root["setbacks"]!["north"] = "1'";
        var v = Validate(plan);
        Assert.True(v.IsReady, Describe(v));
        Assert.True(Has(v, "cantilever", "First floor projects beyond Ground floor"), Describe(v));
        Assert.True(Has(v, "wall_stack", "Outer wall ON of First floor has no wall below it"), Describe(v));
        Assert.True(Has(v, "wall_stack", "Outer wall OE"), Describe(v));
    }

    [Fact]
    public void SizesNotOnWholeInches_AreErrors()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "rooms", "name", "Living")["width"] = "13'0-1/4\"";
        Item(plan, 0, "doors", "tag", "D1")["width"] = "35-1/2";
        var v = Validate(plan);
        Assert.True(Has(v, "snap", "Living width 13'-0 1/4\" is not on whole inches: use 13'-0\""), Describe(v));
        Assert.True(Has(v, "snap", "D1: width 2'-11 1/2\" is not a whole inch"), Describe(v));
    }

    [Fact]
    public void Setback_LessThanGiven_IsAnError()
    {
        var plan = ThirtyForty(floors: 1);
        plan.Root["setbacks"]!["south"] = "6'";
        var v = Validate(plan);
        Assert.True(Has(v, "setback", "5'-0\" from the south boundary; the setback given is 6'-0\""), Describe(v));
    }

    [Fact]
    public void MinimumSizes_FollowTheUsersRules()
    {
        var strict = PlanRules.FromRules(["Bedrooms at least 14'x14'", "Kitchen at least 10'x10'"]);
        var v = Validate(ThirtyForty(), strict);
        Assert.True(Has(v, "room_small", "Bedroom 2 (bedroom) is 13'-0\" x 15'-0\"; the minimum clear size is 14'-0\" x 14'-0\""), Describe(v));
        Assert.True(Has(v, "room_small", "Kitchen (kitchen)"), Describe(v));
        Assert.Contains("using 2 of your rules", v.Summary, StringComparison.Ordinal);

        var plan = ThirtyFifty();
        Item(plan, 0, "rooms", "name", "Common Toilet")["type"] = "bath_wc";
        Assert.True(Has(Validate(plan), "room_small", "Common Toilet"));
        var relaxed = PlanRules.FromRules(["Bathrooms at least 4'9\"x7'"]);
        Assert.True(Validate(plan, relaxed).IsReady, Describe(Validate(plan, relaxed)));
    }

    [Fact]
    public void NarrowMainDoor_IsAWarning()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "doors", "tag", "MD")["width"] = "3'";
        var v = Validate(plan);
        Assert.True(v.IsReady, Describe(v));
        Assert.True(Has(v, "door_narrow", "main door is usually 3'-6\""), Describe(v));
    }

    [Fact]
    public void NarrowPassage_IsAnError()
    {
        var plan = ThirtyFifty();
        // Shrink the passage to 2'-9": the master bedroom grows by 9".
        Item(plan, 0, "rooms", "name", "Master Bedroom")["width"] = PlanJson.L(141);
        Item(plan, 0, "rooms", "name", "Passage")["x"] = PlanJson.L(190.5);
        Item(plan, 0, "rooms", "name", "Passage")["width"] = PlanJson.L(33);
        var walls = plan.Floor(0)["walls"]!.AsArray();
        var va = walls.Select(w => w!.AsObject()).First(w => w["id"]!.ToString() == "VA");
        va["from"] = PlanJson.P(188.25, 40.5);
        va["to"] = PlanJson.P(188.25, 228.75);
        Item(plan, 0, "doors", "tag", "O1")["offset"] = PlanJson.L(190.5 - 40.5);
        Item(plan, 0, "doors", "tag", "O1")["width"] = PlanJson.L(33);
        var v = Validate(plan);
        Assert.True(Has(v, "passage_narrow", "Passage is only 2'-9\" wide"), Describe(v));
    }

    [Fact]
    public void Vastu_HintsOnlyWhenAsked()
    {
        var off = PlanValidator.Validate(ThirtyForty().Root, PlanRules.Default);
        Assert.DoesNotContain(off.Findings, f => f.Code == "vastu");

        var rules = PlanRules.FromRules(["Follow Vastu for every plan"]);
        var on = PlanValidator.Validate(ThirtyForty().Root, rules);
        Assert.True(on.IsReady, Describe(on));
        Assert.Contains(on.Findings, f => f.Code == "vastu" && f.Severity == Severity.Hint && f.Message.Contains("entrance MD faces south", StringComparison.Ordinal));
        Assert.Contains(on.Findings, f => f.Code == "vastu" && f.Message.Contains("Bedroom 1 is in the NW zone", StringComparison.Ordinal));
        Assert.DoesNotContain(on.Findings, f => f.Code == "vastu" && f.Message.Contains("Kitchen", StringComparison.Ordinal));
        Assert.Contains("Vastu hints on", on.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BadInput_IsReportedWithPaths()
    {
        var plan = ThirtyForty(floors: 1);
        plan.Root.Remove("plot");
        Item(plan, 0, "rooms", "name", "Living")["width"] = "wide";
        Item(plan, 0, "doors", "tag", "D1")["wall"] = "V9";
        var v = Validate(plan);
        Assert.Null(v.Plan);
        Assert.Contains(v.Findings, f => f.Message.StartsWith("plan.plot: missing", StringComparison.Ordinal));
        Assert.Contains(v.Findings, f => f.Message.Contains("plan.floors[0].rooms[0].width: 'wide' is not a length", StringComparison.Ordinal));
        Assert.Contains(v.Findings, f => f.Message.Contains("there is no wall 'V9'", StringComparison.Ordinal));
    }

    [Fact]
    public void FeetWithoutAMark_IsCaughtAsTooSmall()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "rooms", "name", "Living")["depth"] = "11";
        var v = Validate(plan);
        Assert.Contains(v.Findings, f => f.Message.Contains("bare numbers are inches", StringComparison.Ordinal));
    }

    [Fact]
    public void Zone_FollowsTheCompass()
    {
        var box = new Box(0, 0, 300, 300);
        Assert.Equal("SW", PlanValidator.Zone(new Pt(10, 10), box));
        Assert.Equal("NE", PlanValidator.Zone(new Pt(290, 290), box));
        Assert.Equal("C", PlanValidator.Zone(new Pt(150, 150), box));
        Assert.Equal("E", PlanValidator.Zone(new Pt(290, 150), box));
    }
}
