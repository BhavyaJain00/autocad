using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed class RunCommandsRequestTests
{
    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Reads_a_valid_request_and_trims_inputs()
    {
        var ok = RunCommandsRequest.TryParse(
            Args("""{"title":" Outer walls ","steps":[{"command":" LINE ","inputs":[" 0,0","@30'<0 ",""],"note":" plot boundary "},{"command":"ZOOM","inputs":["E"]},{"command":"REGEN"}]}"""),
            out var request,
            out var errors);
        Assert.True(ok, string.Join(" | ", errors));
        Assert.Empty(errors);
        Assert.Equal("Outer walls", request!.Title);
        Assert.Equal(3, request.Steps.Count);
        Assert.Equal("LINE", request.Steps[0].Command);
        Assert.Equal(["0,0", "@30'<0", ""], request.Steps[0].Inputs);
        Assert.Equal("plot boundary", request.Steps[0].Note);
        Assert.Empty(request.Steps[2].Inputs);
        Assert.Null(request.Steps[2].Note);
        Assert.Equal(4, request.InputCount);
    }

    [Fact]
    public void Text_inputs_keep_inner_spaces()
    {
        var request = RunCommandsRequest.Parse(Args("""{"title":"Labels","steps":[{"command":"TEXT","inputs":["J","MC","15',20'","6\"","0","BED  ROOM 1",""]}]}"""));
        Assert.Equal("BED  ROOM 1", request.Steps[0].Inputs[5]);
    }

    [Fact]
    public void Missing_arguments_are_explained()
    {
        Assert.False(RunCommandsRequest.TryParse(null, out _, out var errors));
        Assert.Contains("title", errors[0]);
        Assert.False(RunCommandsRequest.TryParse(Args("{}"), out _, out errors));
        Assert.Contains(errors, e => e.StartsWith("$.title:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("$.steps:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"title":"","steps":[{"command":"LINE"}]}""", "$.title")]
    [InlineData("""{"title":"x","steps":[]}""", "$.steps")]
    [InlineData("""{"title":"x","steps":[{"inputs":["0,0"]}]}""", "$.steps[0].command")]
    [InlineData("""{"title":"x","steps":[{"command":""}]}""", "$.steps[0].command")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE 0,0 10,10"}]}""", "ONE command")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE;"}]}""", "ONE command")]
    [InlineData("""{"title":"x","steps":[{"command":"LI*NE"}]}""", "not an AutoCAD command name")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","inputs":"0,0"}]}""", "$.steps[0].inputs")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","inputs":[0]}]}""", "in quotes")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","inputs":[true]}]}""", "must be a string")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","inputs":["0,0\n10,10"]}]}""", "control characters")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","inputs":["\u0003"]}]}""", "control characters")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","foo":1}]}""", "unknown property 'foo'")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE"}],"extra":1}""", "unknown property 'extra'")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","note":5}]}""", "$.steps[0].note")]
    [InlineData("""{"title":"x","steps":["LINE"]}""", "$.steps[0]: must be an object")]
    public void Invalid_requests_are_rejected_with_their_path(string json, string expectedFragment)
    {
        Assert.False(RunCommandsRequest.TryParse(Args(json), out var request, out var errors));
        Assert.Null(request);
        Assert.Contains(errors, e => e.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Limits_are_enforced()
    {
        var tooManySteps = new JsonObject
        {
            ["title"] = "x",
            ["steps"] = new JsonArray(Enumerable.Range(0, DraftingLimits.MaxSteps + 1).Select(_ => (JsonNode)new JsonObject { ["command"] = "REGEN" }).ToArray()),
        };
        Assert.False(RunCommandsRequest.TryParse(tooManySteps, out _, out var errors));
        Assert.Contains(errors, e => e.Contains("1-30", StringComparison.Ordinal));

        var tooManyInputs = new JsonObject
        {
            ["title"] = "x",
            ["steps"] = new JsonArray(new JsonObject
            {
                ["command"] = "PLINE",
                ["inputs"] = new JsonArray(Enumerable.Range(0, DraftingLimits.MaxInputsPerStep + 1).Select(_ => (JsonNode)JsonValue.Create("@1,0")!).ToArray()),
            }),
        };
        Assert.False(RunCommandsRequest.TryParse(tooManyInputs, out _, out errors));
        Assert.Contains(errors, e => e.Contains("at most 60 inputs", StringComparison.Ordinal));

        var longInput = new JsonObject
        {
            ["title"] = "x",
            ["steps"] = new JsonArray(new JsonObject { ["command"] = "TEXT", ["inputs"] = new JsonArray(new string('A', DraftingLimits.MaxInputLength + 1)) }),
        };
        Assert.False(RunCommandsRequest.TryParse(longInput, out _, out errors));
        Assert.Contains(errors, e => e.Contains("at most 300 characters", StringComparison.Ordinal));

        var longTitle = new JsonObject { ["title"] = new string('T', DraftingLimits.MaxTitleLength + 1), ["steps"] = new JsonArray(new JsonObject { ["command"] = "REGEN" }) };
        Assert.False(RunCommandsRequest.TryParse(longTitle, out _, out errors));
        Assert.Contains(errors, e => e.StartsWith("$.title:", StringComparison.Ordinal));
    }

    [Fact]
    public void Exactly_the_limits_are_accepted()
    {
        var request = new JsonObject
        {
            ["title"] = new string('T', DraftingLimits.MaxTitleLength),
            ["steps"] = new JsonArray(Enumerable.Range(0, DraftingLimits.MaxSteps).Select(_ => (JsonNode)new JsonObject
            {
                ["command"] = "PLINE",
                ["inputs"] = new JsonArray(Enumerable.Range(0, DraftingLimits.MaxInputsPerStep).Select(_ => (JsonNode)JsonValue.Create(new string('1', DraftingLimits.MaxInputLength))!).ToArray()),
            }).ToArray()),
        };
        Assert.True(RunCommandsRequest.TryParse(request, out var parsed, out var errors), string.Join(" | ", errors));
        Assert.Equal(DraftingLimits.MaxSteps * DraftingLimits.MaxInputsPerStep, parsed!.InputCount);
    }

    [Fact]
    public void AutoLisp_command_passes_validation_so_the_policy_can_explain_it()
    {
        Assert.True(RunCommandsRequest.TryParse(Args("""{"title":"x","steps":[{"command":"(command)"}]}"""), out var request, out _));
        Assert.True(DraftingPlan.Create(request!).IsBlocked);
    }

    [Fact]
    public void Parse_throws_a_tool_exception_listing_problems()
    {
        var ex = Assert.Throws<ToolException>(() => RunCommandsRequest.Parse(Args("""{"title":"x","steps":[{"command":"LINE 0,0"}]}""")));
        Assert.Contains("$.steps[0].command", ex.Message);
    }

    [Theory]
    [InlineData("abc", false)]
    [InlineData("a\tb", true)]
    [InlineData("a\rb", true)]
    [InlineData("a\u2028b", true)]
    [InlineData("क्\u200Dष", false)]
    public void Control_characters_are_detected(string text, bool expected)
    {
        Assert.Equal(expected, RunCommandsRequest.HasControlCharacters(text));
    }
}
