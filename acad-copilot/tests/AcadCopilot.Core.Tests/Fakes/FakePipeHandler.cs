using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Tests.Fakes;

/// <summary>In-process stand-in for the AutoCAD add-in behind the pipe (shared by Core and bridge tests).</summary>
public sealed class FakePipeHandler : IPipeHandler
{
    public ConcurrentQueue<ToolsCallParams> Calls { get; } = new();

    public ConcurrentQueue<PlanUpdate> Plans { get; } = new();

    public ConcurrentQueue<QuestionRequest> Questions { get; } = new();

    /// <summary>Chat session ids that came with ui.askUser (null = none).</summary>
    public ConcurrentQueue<string?> QuestionSessions { get; } = new();

    /// <summary>Chat session ids that came with ui.updatePlan (null = none).</summary>
    public ConcurrentQueue<string?> PlanSessions { get; } = new();

    public bool UiAvailable { get; set; } = true;

    public Func<QuestionRequest, CancellationToken, Task<QuestionResponse>>? Answer { get; set; }

    public string? DocumentTitle { get; set; } = "House.dwg";

    public List<ToolDefinition> Tools { get; } =
    [
        new()
        {
            Name = "get_model_info",
            Title = "Get model info",
            Description = "Fake model info.",
            Risk = ToolRisk.Read,
            InputSchema = Schema.Object(Schema.Opt("include_levels", Schema.Boolean("Include levels."))),
        },
        new()
        {
            Name = "create_wall",
            Title = "Create wall",
            Description = "Fake wall creation.",
            Risk = ToolRisk.Write,
            InputSchema = Schema.Object(
                Schema.Req("start", Schema.Point2D("Start.")),
                Schema.Req("end", Schema.Point2D("End.")),
                Schema.Opt("level", Schema.String("Level name."))),
        },
    ];

    public AuthResult GetServerInfo() => new()
    {
        ServerName = "FakeAddin",
        ServerVersion = "9.9.9",
        Pid = Environment.ProcessId,
        AcadVersion = "2027",
        DocumentTitle = DocumentTitle,
    };

    public Task<ToolsListResult> ListToolsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ToolsListResult { Tools = Tools });

    public Task<ToolResult> CallToolAsync(ToolsCallParams call, CancellationToken cancellationToken)
    {
        Calls.Enqueue(call);
        return Task.FromResult(call.Name switch
        {
            "get_model_info" => ToolResult.Ok("Model 'House.dwg'.", new JsonObject { ["document"] = new JsonObject { ["title"] = DocumentTitle } }) with { Revision = 7 },
            "create_wall" => ToolResult.Ok("Created 1 wall.") with { CreatedIds = [1001], Revision = 8 },
            _ => ToolResult.Fail($"Unknown tool '{call.Name}'."),
        });
    }

    public async Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken)
    {
        if (!UiAvailable)
        {
            throw new UiUnavailableException("No panel in this test.");
        }

        Questions.Enqueue(request);
        if (Answer is not null)
        {
            return await Answer(request, cancellationToken);
        }

        return new QuestionResponse
        {
            Answers = [.. request.Questions.Select(q => new QuestionAnswer { Question = q.Question, Selected = [q.Options[0].Label] })],
        };
    }

    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, string? sessionId, CancellationToken cancellationToken)
    {
        QuestionSessions.Enqueue(sessionId);
        return AskUserAsync(request, cancellationToken);
    }

    public Task UpdatePlanAsync(PlanUpdate plan, string? sessionId, CancellationToken cancellationToken)
    {
        PlanSessions.Enqueue(sessionId);
        return UpdatePlanAsync(plan, cancellationToken);
    }

    public Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken)
    {
        if (!UiAvailable)
        {
            throw new UiUnavailableException("No panel in this test.");
        }

        Plans.Enqueue(plan);
        return Task.CompletedTask;
    }
}
