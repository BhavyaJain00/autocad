using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests;

public sealed class ToolModelTests
{
    [Theory]
    [InlineData("get_model_info", true)]
    [InlineData("create_wall2", true)]
    [InlineData("Create_wall", false)]
    [InlineData("2walls", false)]
    [InlineData("a", false)]
    [InlineData("create-wall", false)]
    public void Tool_name_rules(string name, bool valid) => Assert.Equal(valid, ToolDefinition.IsValidName(name));

    [Fact]
    public void Lint_flags_loose_schemas()
    {
        var definition = new ToolDefinition
        {
            Name = "bad_tool",
            Title = "Bad",
            Description = "Bad.",
            InputSchema = new JsonObject { ["type"] = "object" },
        };
        Assert.Contains(definition.Lint(), p => p.Contains("additionalProperties", StringComparison.Ordinal));
    }

    [Fact]
    public void Bridge_tool_definitions_are_strict_and_valid()
    {
        foreach (var definition in new[] { BridgeTools.AskUserDefinition(), BridgeTools.UpdatePlanDefinition(), BridgeTools.AcadStatusDefinition() })
        {
            Assert.Empty(definition.Lint());
            Assert.Contains(definition.Name, BridgeTools.ReservedNames);
        }
    }

    [Fact]
    public void Ask_user_schema_accepts_a_typical_call_and_maps_to_question_request()
    {
        var args = JsonNode.Parse("""
            {"questions":[{"header":"Bedrooms","question":"How many bedrooms?","options":[
              {"label":"2 bedrooms"},{"label":"3 bedrooms","description":"Most common","recommended":true}],
              "allowCustom":true}]}
            """)!.AsObject();
        Assert.Empty(JsonSchemaValidator.Validate(BridgeTools.AskUserDefinition().InputSchema, args));
        var request = CopilotJson.FromNode<QuestionRequest>(args);
        var question = Assert.Single(request.Questions);
        Assert.Equal("How many bedrooms?", question.Question);
        Assert.True(question.Options[1].Recommended);
        Assert.False(question.MultiSelect);
    }

    [Fact]
    public void Ask_user_schema_rejects_single_option()
    {
        var args = JsonNode.Parse("""{"questions":[{"question":"Sure?","options":[{"label":"Yes"}]}]}""")!.AsObject();
        Assert.NotEmpty(JsonSchemaValidator.Validate(BridgeTools.AskUserDefinition().InputSchema, args));
    }

    [Fact]
    public void Plan_status_uses_snake_case_on_the_wire()
    {
        var args = JsonNode.Parse("""{"tasks":[{"title":"Walls","status":"in_progress"},{"title":"Roof","status":"completed"}]}""")!.AsObject();
        Assert.Empty(JsonSchemaValidator.Validate(BridgeTools.UpdatePlanDefinition().InputSchema, args));
        var plan = CopilotJson.FromNode<PlanUpdate>(args);
        Assert.Equal(PlanTaskStatus.InProgress, plan.Tasks[0].Status);
        Assert.Equal(1, plan.CompletedCount);
        Assert.Contains("\"in_progress\"", CopilotJson.Serialize(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_result_round_trips_and_omits_nulls()
    {
        var result = ToolResult.Ok("Created 2 walls.", new JsonObject { ["walls"] = 2 }) with
        {
            CreatedIds = [10, 11],
            Warnings = ["Wall overlap"],
            Revision = 5,
        };
        var json = result.ToJson();
        Assert.Contains("\"createdIds\":[10,11]", json, StringComparison.Ordinal);
        var back = CopilotJson.Deserialize<ToolResult>(json);
        Assert.True(back.Success);
        Assert.Equal([10L, 11L], back.CreatedIds);
        Assert.Equal(5, back.Revision);
        Assert.Equal(2, back.Data!["walls"]!.GetValue<int>());

        var failed = ToolResult.Fail("Nope", "more");
        Assert.False(failed.Success);
        Assert.Equal(["Nope", "more"], failed.Errors);
        Assert.DoesNotContain("\"data\"", failed.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_definition_serializes_risk_as_lowercase()
    {
        var definition = BridgeTools.AcadStatusDefinition() with { Risk = ToolRisk.Destructive };
        var json = CopilotJson.Serialize(definition);
        Assert.Contains("\"risk\":\"destructive\"", json, StringComparison.Ordinal);
        var back = CopilotJson.Deserialize<ToolDefinition>(json);
        Assert.Equal(ToolRisk.Destructive, back.Risk);
        Assert.Equal("object", back.InputSchema["type"]!.GetValue<string>());
    }

    [Fact]
    public void Schema_builder_clones_shared_nodes()
    {
        var point = Schema.Point2D("P.");
        var first = Schema.Object(Schema.Req("a", point));
        var second = Schema.Object(Schema.Req("b", point));
        Assert.NotNull(first["properties"]!["a"]);
        Assert.NotNull(second["properties"]!["b"]);
    }

    [Fact]
    public void Tool_arguments_typed_getters()
    {
        var args = new ToolArguments(JsonNode.Parse("""{"n":2,"d":2.5,"s":"x","b":true,"ids":[1,2],"o":{"x":1},"list":[{"a":1}]}""")!.AsObject());
        Assert.Equal(2, args.GetLong("n"));
        Assert.Equal(2.5, args.GetDouble("d"));
        Assert.Equal(7.0, args.GetDouble("missing", 7.0));
        Assert.Equal("x", args.GetString("s"));
        Assert.Null(args.GetStringOrNull("missing"));
        Assert.True(args.GetBool("b"));
        Assert.Equal([1L, 2L], args.GetLongList("ids"));
        Assert.Equal(1, args.GetObject("o").GetDouble("x"));
        Assert.Single(args.GetObjectList("list"));
        Assert.Throws<ToolException>(() => args.GetLong("d"));
        Assert.Throws<ToolException>(() => args.GetString("n"));
        var missing = Assert.Throws<ToolException>(() => args.GetString("nope"));
        Assert.Contains("nope", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_helpers_read_clr_backed_numbers()
    {
        Assert.True(CopilotJson.TryGetDouble(JsonValue.Create(5), out var five));
        Assert.Equal(5, five);
        Assert.True(CopilotJson.TryGetDouble(JsonNode.Parse("1e3"), out var thousand));
        Assert.Equal(1000, thousand);
        Assert.False(CopilotJson.TryGetDouble(JsonValue.Create("5"), out _));
        Assert.Equal("boolean", CopilotJson.KindName(JsonValue.Create(true)));
        Assert.Throws<JsonException>(() => CopilotJson.FromNode<PlanUpdate>(null));
    }
}
