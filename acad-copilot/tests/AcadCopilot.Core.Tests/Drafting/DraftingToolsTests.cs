using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed partial class DraftingToolsTests
{
    [Fact]
    public void Definitions_are_well_formed_and_not_reserved()
    {
        var all = DraftingTools.All();
        Assert.Equal(["run_commands", "cancel_command", "undo"], all.Select(d => d.Name));
        foreach (var definition in all)
        {
            Assert.Empty(definition.Lint());
            Assert.DoesNotContain(definition.Name, BridgeTools.ReservedNames);
            Assert.Equal(ToolRisk.Write, definition.Risk);
        }
    }

    [Fact]
    public void Run_commands_schema_accepts_the_contract_example()
    {
        var args = JsonNode.Parse("""{ "title": "Outer walls", "steps": [ { "command": "LINE", "inputs": ["0,0", "@30'<0", "@40'<90", ""], "note": "plot boundary" } ] }""")!.AsObject();
        Assert.Empty(JsonSchemaValidator.Validate(DraftingTools.RunCommandsDefinition().InputSchema, args));
        Assert.True(RunCommandsRequest.TryParse(args, out _, out _));
    }

    [Theory]
    [InlineData("""{"steps":[{"command":"LINE"}]}""")]
    [InlineData("""{"title":"x","steps":[]}""")]
    [InlineData("""{"title":"x","steps":[{"inputs":["0,0"]}]}""")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","inputs":[1]}]}""")]
    [InlineData("""{"title":"x","steps":[{"command":"LINE","extra":true}]}""")]
    public void Run_commands_schema_rejects_bad_shapes(string json)
    {
        var args = JsonNode.Parse(json)!.AsObject();
        Assert.NotEmpty(JsonSchemaValidator.Validate(DraftingTools.RunCommandsDefinition().InputSchema, args));
    }

    [Fact]
    public void Description_examples_are_valid_calls_or_steps()
    {
        var description = DraftingTools.RunCommandsDescription;
        var examples = ExampleJson().Matches(description).Select(m => m.Value).ToList();
        Assert.True(examples.Count >= 4, "expected 4 examples, found " + examples.Count);
        foreach (var example in examples)
        {
            var node = JsonNode.Parse(example)!.AsObject();
            var call = node.ContainsKey("steps") ? node : new JsonObject { ["title"] = "Example", ["steps"] = new JsonArray(node) };
            Assert.True(RunCommandsRequest.TryParse(call, out var request, out var errors), example + " -> " + string.Join(" | ", errors));
            var plan = DraftingPlan.Create(request!);
            Assert.False(plan.IsBlocked, example);
            Assert.False(plan.NeedsApproval, example);
            Assert.Empty(plan.RewriteNotes);
        }
    }

    [Theory]
    [InlineData("LUNITS is 3 or 4")]
    [InlineData("\"\" is Enter")]
    [InlineData("capital letters")]
    [InlineData("ONE undo step")]
    [InlineData("-LAYER")]
    [InlineData("-HATCH")]
    [InlineData("-INSERT")]
    [InlineData("TEXT")]
    [InlineData("capture_view")]
    [InlineData("get_drawing_info")]
    [InlineData("@distance<angle")]
    [InlineData("Never type a lone number at a point prompt")]
    [InlineData("Spaces act like Enter")]
    [InlineData("ask the user first")]
    public void Description_teaches_the_rules(string fragment)
    {
        Assert.Contains(fragment, DraftingTools.RunCommandsDescription);
    }

    [Fact]
    public void Undo_schema_limits_the_count()
    {
        var schema = DraftingTools.UndoDefinition().InputSchema;
        Assert.Empty(JsonSchemaValidator.Validate(schema, new JsonObject()));
        Assert.Empty(JsonSchemaValidator.Validate(schema, new JsonObject { ["count"] = 3 }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, new JsonObject { ["count"] = 0 }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, new JsonObject { ["count"] = DraftingTools.MaxUndoCount + 1 }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, new JsonObject { ["count"] = 1.5 }));
    }

    [Fact]
    public void Cancel_command_takes_no_arguments()
    {
        var schema = DraftingTools.CancelCommandDefinition().InputSchema;
        Assert.Empty(JsonSchemaValidator.Validate(schema, new JsonObject()));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, new JsonObject { ["x"] = 1 }));
    }

    // The examples in the description are single-line JSON objects starting with {" (one call or one step).
    [GeneratedRegex("\\{\"(title|command)\".*\\}(?=\\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExampleJson();
}
