using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Tools;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.McpBridge.Tests;

/// <summary>
/// The fake add-in with drafting-like tools: get_drawing_info / list_layers (read), run_commands (write, bumps the
/// revision, can be slow or fail by title). Unknown tools fail at once with the current revision, like the plug-in.
/// </summary>
public sealed class DraftingToolHandler(FakePipeHandler inner) : IPipeHandler
{
    private long _revision = 40;

    public ConcurrentDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> TypedStages { get; } = new();

    public TimeSpan StageDelay { get; set; }

    public HashSet<string> FailingStages { get; } = new(StringComparer.Ordinal);

    public long Revision
    {
        get => Interlocked.Read(ref _revision);
        set => Interlocked.Exchange(ref _revision, value);
    }

    public int Count(string tool) => Counts.TryGetValue(tool, out var count) ? count : 0;

    public AuthResult GetServerInfo() => inner.GetServerInfo();

    public async Task<ToolsListResult> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await inner.ListToolsAsync(cancellationToken);
        return new ToolsListResult
        {
            Tools =
            [
                .. tools.Tools,
                Tool("get_drawing_info", ToolRisk.Read),
                Tool("list_layers", ToolRisk.Read),
                Tool("run_commands", ToolRisk.Write),
            ],
        };
    }

    public async Task<ToolResult> CallToolAsync(ToolsCallParams call, CancellationToken cancellationToken)
    {
        Counts.AddOrUpdate(call.Name, 1, (_, n) => n + 1);
        switch (call.Name)
        {
            case "get_drawing_info":
                return ToolResult.Ok($"House.dwg at revision {Revision}.", new JsonObject { ["revision"] = Revision }) with { Revision = Revision };
            case "list_layers":
                return ToolResult.Ok("2 layers.", new JsonObject { ["layers"] = new JsonArray("0", "A-WALL") }) with { Revision = Revision };
            case "run_commands":
                var title = call.Arguments?["title"]?.GetValue<string>() ?? "stage";
                if (StageDelay > TimeSpan.Zero)
                {
                    await Task.Delay(StageDelay, cancellationToken);
                }

                TypedStages.Enqueue(title);
                if (FailingStages.Contains(title))
                {
                    return ToolResult.Fail($"'{title}' failed: Point or option keyword required.") with { Revision = Revision };
                }

                return ToolResult.Ok($"Typed '{title}'.") with { Revision = Interlocked.Increment(ref _revision) };
            case "get_model_info" or "create_wall":
                return await inner.CallToolAsync(call, cancellationToken);
            default:
                return ToolResult.Fail($"Unknown AutoCAD tool '{call.Name}'.") with { Revision = Revision };
        }
    }

    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken) => inner.AskUserAsync(request, cancellationToken);

    public Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken) => inner.UpdatePlanAsync(plan, cancellationToken);

    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, string? sessionId, CancellationToken cancellationToken) => inner.AskUserAsync(request, sessionId, cancellationToken);

    public Task UpdatePlanAsync(PlanUpdate plan, string? sessionId, CancellationToken cancellationToken) => inner.UpdatePlanAsync(plan, sessionId, cancellationToken);

    private static ToolDefinition Tool(string name, ToolRisk risk) => new()
    {
        Name = name,
        Title = name,
        Description = "Fake " + name + ".",
        Risk = risk,
        InputSchema = Schema.Object(Schema.Opt("title", Schema.String("Title.")), Schema.Opt("layer", Schema.String("Layer."))),
    };
}

public sealed class BridgeFeatureTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static (FakeAcad Acad, DraftingToolHandler Handler) Start()
    {
        DraftingToolHandler? handler = null;
        var acad = FakeAcad.Start(wrap: inner => handler = new DraftingToolHandler(inner));
        return (acad, handler!);
    }

    [Fact]
    public void Readonly_option_is_parsed()
    {
        Assert.True(BridgeOptions.Parse(["--pid", "1", "--readonly", "--session", "tab-1"]).ReadOnly);
        Assert.False(BridgeOptions.Parse(["--pid", "1", "--panel"]).ReadOnly);
        Assert.Contains("--readonly", BridgeOptions.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_bridge_lists_and_runs_only_tools_that_read()
    {
        var (acad, handler) = Start();
        await using var _ = acad;
        await using var link = new AcadLink(acad.Options(panel: true) with { ReadOnly = true });
        var knowledge = new KnowledgeStore(Path.Combine(acad.Directory.Path, "knowledge"));
        var service = new BridgeToolService(link, panel: true, knowledge, readOnly: true);

        var names = (await service.ListToolsAsync(None)).Select(t => t.Name).ToList();
        Assert.Equal(["get_model_info", "get_drawing_info", "list_layers", "acad_status", "recall_knowledge", "list_recordings", "read_recording"], names);

        foreach (var (tool, args) in new (string, JsonObject?)[]
        {
            ("run_commands", new JsonObject { ["title"] = "Erase all" }),
            ("create_wall", new JsonObject()),
            ("undo", null),
            ("cancel_command", null),
            ("learn_style_from_drawing", null),
            ("remember_rule", new JsonObject { ["text"] = "Reviewer rule." }),
            ("save_recipe", new JsonObject { ["name"] = "x", ["description"] = "y", ["steps"] = new JsonArray(new JsonObject { ["command"] = "LINE" }) }),
            ("forget", new JsonObject { ["kind"] = "rule", ["text"] = "xx" }),
            ("ask_user", new JsonObject { ["questions"] = new JsonArray() }),
            ("update_plan", new JsonObject { ["tasks"] = new JsonArray() }),
            ("make_coffee", null),
        })
        {
            var refused = await service.CallToolAsync(tool, args, None);
            Assert.True(refused.IsError, tool);
            Assert.Contains("read-only review", refused.Text, StringComparison.Ordinal);
        }

        Assert.Equal(0, handler.Count("run_commands"));
        Assert.Equal(0, handler.Count("create_wall"));
        Assert.Equal(0, handler.Count("undo"));
        Assert.False(File.Exists(Path.Combine(knowledge.Directory, "rules.md")));
        Assert.Empty(acad.Handler.Questions);
        Assert.Empty(acad.Handler.Plans);

        Assert.False((await service.CallToolAsync("get_drawing_info", null, None)).IsError);
        Assert.False((await service.CallToolAsync("recall_knowledge", new JsonObject { ["query"] = "stair" }, None)).IsError);
        Assert.Equal(1, handler.Count("get_drawing_info"));
    }

    [Fact]
    public async Task Read_results_are_cached_until_the_drawing_revision_changes()
    {
        var (acad, handler) = Start();
        await using var _ = acad;
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: true);
        var layers = new JsonObject { ["layer"] = "A-WALL" };

        var first = await service.CallToolAsync("list_layers", layers, None);
        var second = await service.CallToolAsync("list_layers", new JsonObject { ["layer"] = "A-WALL" }, None);
        Assert.Equal(1, handler.Count("list_layers"));
        Assert.Null(JsonNode.Parse(first.Text)!["cached"]);
        Assert.True(JsonNode.Parse(second.Text)!["cached"]!.GetValue<bool>());
        Assert.Equal(1, service.Cache.Hits);

        // Other arguments are another entry.
        await service.CallToolAsync("list_layers", null, None);
        Assert.Equal(2, handler.Count("list_layers"));

        // The user (or another chat) changed the drawing: the revision moved, so AutoCAD is asked again.
        handler.Revision += 3;
        await service.CallToolAsync("list_layers", layers, None);
        Assert.Equal(3, handler.Count("list_layers"));

        // A drawing stage of this chat clears the drawing results at once.
        await service.CallToolAsync("get_drawing_info", null, None);
        Assert.False((await service.CallToolAsync("run_commands", new JsonObject { ["title"] = "Walls" }, None)).IsError);
        await service.CallToolAsync("get_drawing_info", null, None);
        Assert.Equal(2, handler.Count("get_drawing_info"));
    }

    [Fact]
    public async Task Drawing_info_lives_only_briefly_because_view_and_active_command_change_without_a_revision()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-19T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var (acad, handler) = Start();
        await using var _ = acad;
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: true, cache: new ResponseCache(clock));

        await service.CallToolAsync("get_drawing_info", null, None);
        clock.Advance(TimeSpan.FromSeconds(5));
        await service.CallToolAsync("get_drawing_info", null, None);
        Assert.Equal(1, handler.Count("get_drawing_info"));

        clock.Advance(ResponseCache.DrawingInfoMaxAge);
        await service.CallToolAsync("get_drawing_info", null, None);
        Assert.Equal(2, handler.Count("get_drawing_info"));
    }

    [Fact]
    public async Task Recall_is_cached_until_a_knowledge_file_changes_and_brings_the_built_in_guides()
    {
        var (acad, _) = Start();
        await using var __ = acad;
        await using var link = new AcadLink(acad.Options());
        var knowledge = new KnowledgeStore(Path.Combine(acad.Directory.Path, "knowledge"));
        var service = new BridgeToolService(link, panel: true, knowledge);
        var query = new JsonObject { ["query"] = "front elevation" };

        var first = JsonNode.Parse((await service.CallToolAsync("recall_knowledge", query, None)).Text)!;
        Assert.Equal("Elevation", first["data"]!["guides"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("plinth 1'6", first["data"]!["guides"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        var second = JsonNode.Parse((await service.CallToolAsync("recall_knowledge", query, None)).Text)!;
        Assert.True(second["cached"]!.GetValue<bool>());

        Assert.False((await service.CallToolAsync("remember_rule", new JsonObject { ["text"] = "Elevation plinth 2'0 high." }, None)).IsError);
        var third = JsonNode.Parse((await service.CallToolAsync("recall_knowledge", query, None)).Text)!;
        Assert.Null(third["cached"]);
        Assert.Contains(third["data"]!["items"]!.AsArray(), item => item!["text"]!.GetValue<string>().Contains("plinth 2'0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Queued_stages_reach_autocad_in_order_and_stop_after_a_failed_one()
    {
        var (acad, handler) = Start();
        await using var _ = acad;
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: true);
        await service.CallToolAsync("get_drawing_info", null, None); // connect first
        handler.StageDelay = TimeSpan.FromMilliseconds(300);

        var walls = service.CallToolAsync("run_commands", new JsonObject { ["title"] = "Walls" }, None);
        await Task.Delay(50);
        var doors = service.CallToolAsync("run_commands", new JsonObject { ["title"] = "Doors" }, None);
        await Task.Delay(20);
        var text = service.CallToolAsync("run_commands", new JsonObject { ["title"] = "Text" }, None);
        var firstBatch = await Task.WhenAll(walls, doors, text);
        Assert.Equal(["Walls", "Doors", "Text"], handler.TypedStages);
        Assert.All(firstBatch, r => Assert.False(r.IsError));

        handler.FailingStages.Add("Openings");
        var openings = service.CallToolAsync("run_commands", new JsonObject { ["title"] = "Openings" }, None);
        await Task.Delay(50);
        var stair = service.CallToolAsync("run_commands", new JsonObject { ["title"] = "Stair" }, None);
        await Task.Delay(20);
        var dims = service.CallToolAsync("run_commands", new JsonObject { ["title"] = "Dimensions" }, None);
        var second = await Task.WhenAll(openings, stair, dims);

        Assert.True(second[0].IsError);
        Assert.True(second[1].IsError);
        Assert.Contains("Skipped: this queued run_commands", second[1].Text, StringComparison.Ordinal);
        Assert.Contains("Stair", second[1].Text, StringComparison.Ordinal);
        Assert.True(second[2].IsError);
        Assert.Equal(["Walls", "Doors", "Text", "Openings"], handler.TypedStages);

        // A stage sent after the failure was seen (not queued behind it) runs normally.
        handler.StageDelay = TimeSpan.Zero;
        Assert.False((await service.CallToolAsync("run_commands", new JsonObject { ["title"] = "Openings again" }, None)).IsError);
    }

    [Fact]
    public async Task Fifo_gate_keeps_the_order_when_a_waiting_call_is_cancelled()
    {
        var gate = new FifoGate();
        var first = await gate.EnterAsync(None);
        using var cancel = new CancellationTokenSource();
        var second = gate.EnterAsync(cancel.Token);
        var third = gate.EnterAsync(None);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(third.IsCompleted);
        first.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, gate.Pending);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
