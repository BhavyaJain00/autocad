using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests;

public sealed class JsonSchemaValidatorTests
{
    private static readonly JsonObject WallSchema = Schema.Object(
        Schema.Req("level", Schema.String("Level.", minLength: 1)),
        Schema.Req("start", Schema.Point2D("Start.")),
        Schema.Opt("height", Schema.Number("Height.", minimum: 0, maximum: 100)),
        Schema.Opt("count", Schema.Integer("Count.", minimum: 1)),
        Schema.Opt("kind", Schema.String("Kind.", ["exterior", "interior"])),
        Schema.Opt("ids", Schema.ElementIds("Ids.", minItems: 1, maxItems: 3)),
        Schema.Opt("code", Schema.String("Code.", pattern: "^[A-Z]{2}[0-9]$")));

    private static JsonObject Valid() => new()
    {
        ["level"] = "Level 1",
        ["start"] = new JsonObject { ["x"] = 0, ["y"] = 10.5 },
        ["height"] = 10,
        ["count"] = 2,
        ["kind"] = "exterior",
        ["ids"] = new JsonArray(1, 2),
        ["code"] = "AB1",
    };

    [Fact]
    public void Valid_instance_has_no_errors() =>
        Assert.Empty(JsonSchemaValidator.Validate(WallSchema, Valid()));

    [Fact]
    public void Parsed_json_is_validated_like_built_nodes()
    {
        var parsed = JsonNode.Parse(Valid().ToJsonString());
        Assert.Empty(JsonSchemaValidator.Validate(WallSchema, parsed));
    }

    [Fact]
    public void Missing_required_property_is_reported()
    {
        var instance = Valid();
        instance.Remove("level");
        var error = Assert.Single(JsonSchemaValidator.Validate(WallSchema, instance));
        Assert.Contains("missing required property 'level'", error);
    }

    [Fact]
    public void Unknown_property_is_rejected_with_allowed_list()
    {
        var instance = Valid();
        instance["colour"] = "red";
        var error = Assert.Single(JsonSchemaValidator.Validate(WallSchema, instance));
        Assert.Contains("unknown property 'colour'", error);
        Assert.Contains("level", error);
    }

    [Fact]
    public void Nested_object_errors_carry_the_path()
    {
        var instance = Valid();
        instance["start"] = new JsonObject { ["x"] = "zero", ["y"] = 1 };
        var error = Assert.Single(JsonSchemaValidator.Validate(WallSchema, instance));
        Assert.StartsWith("$.start.x:", error);
        Assert.Contains("expected number, got string", error);
    }

    [Theory]
    [InlineData("height", -1, "must be >= 0")]
    [InlineData("height", 101, "must be <= 100")]
    [InlineData("count", 1.5, "expected integer")]
    [InlineData("count", 0, "must be >= 1")]
    public void Numeric_bounds_and_integer_type(string name, double value, string expected)
    {
        var instance = Valid();
        instance[name] = value;
        var error = Assert.Single(JsonSchemaValidator.Validate(WallSchema, instance));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Integer_accepts_whole_doubles()
    {
        var instance = Valid();
        instance["count"] = 3.0;
        Assert.Empty(JsonSchemaValidator.Validate(WallSchema, instance));
    }

    [Fact]
    public void Enum_pattern_and_length_rules()
    {
        var instance = Valid();
        instance["kind"] = "garden";
        instance["code"] = "abc";
        instance["level"] = string.Empty;
        var errors = JsonSchemaValidator.Validate(WallSchema, instance);
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("must be one of", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("must match pattern", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("at least 1 character", StringComparison.Ordinal));
    }

    [Fact]
    public void Array_size_and_item_types()
    {
        var instance = Valid();
        instance["ids"] = new JsonArray(1, "two", 3, 4);
        var errors = JsonSchemaValidator.Validate(WallSchema, instance);
        Assert.Contains(errors, e => e.Contains("at most 3 item", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("$.ids[1]:", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_must_be_an_object()
    {
        var error = Assert.Single(JsonSchemaValidator.Validate(WallSchema, new JsonArray()));
        Assert.Contains("expected object, got array", error);
        Assert.NotEmpty(JsonSchemaValidator.Validate(WallSchema, null));
    }

    [Fact]
    public void AnyOf_oneOf_and_type_arrays()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["length"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "number" }, new JsonObject { ["type"] = "string" }),
                },
                ["mode"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray(new JsonObject { ["const"] = "a" }, new JsonObject { ["type"] = "string", ["minLength"] = 1 }),
                },
                ["maybe"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            },
            ["additionalProperties"] = false,
        };

        Assert.Empty(JsonSchemaValidator.Validate(schema, new JsonObject { ["length"] = "10 ft", ["mode"] = "b", ["maybe"] = null }));
        Assert.Single(JsonSchemaValidator.Validate(schema, new JsonObject { ["length"] = true }));
        Assert.Contains(JsonSchemaValidator.Validate(schema, new JsonObject { ["mode"] = "a" }), e => e.Contains("more than one", StringComparison.Ordinal));
        Assert.Single(JsonSchemaValidator.Validate(schema, new JsonObject { ["maybe"] = 5 }));
    }

    [Fact]
    public void Additional_properties_schema_is_applied()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = new JsonObject { ["type"] = "number" },
        };
        Assert.Empty(JsonSchemaValidator.Validate(schema, new JsonObject { ["a"] = 1, ["b"] = 2.5 }));
        Assert.Single(JsonSchemaValidator.Validate(schema, new JsonObject { ["a"] = "x" }));
    }
}
