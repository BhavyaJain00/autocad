using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Tools;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.McpBridge.Tests;

/// <summary>
/// Decorates the shared fake add-in with a capture_view tool that returns a real PNG, the way the AutoCAD plug-in's
/// capture_view does. Arguments: {"mode":"ok"} (default) one PNG; "bad" one good PNG plus two images an LLM cannot
/// take; "error" a failed result that still carries a PNG. Everything else goes to the inner fake.
/// </summary>
public sealed class ImageToolHandler(FakePipeHandler inner) : IPipeHandler
{
    /// <summary>A real 2 x 3 px RGB PNG (79 bytes).</summary>
    public const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAAA2iEnWAAAAFklEQVR42mP4//8/AwSAWIoaBv8ZGABZDAdz/0OXgAAAAABJRU5ErkJggg==";

    public static byte[] TinyPng => Convert.FromBase64String(TinyPngBase64);

    public static ToolDefinition CaptureViewDefinition { get; } = new()
    {
        Name = "capture_view",
        Title = "Capture view",
        Description = "Fake picture of the current drawing view.",
        Risk = ToolRisk.Read,
        InputSchema = Schema.Object(Schema.Opt("mode", Schema.String("ok, bad or error.", ["ok", "bad", "error"]))),
    };

    public AuthResult GetServerInfo() => inner.GetServerInfo();

    public async Task<ToolsListResult> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await inner.ListToolsAsync(cancellationToken);
        return new ToolsListResult { Tools = [.. tools.Tools, CaptureViewDefinition] };
    }

    public Task<ToolResult> CallToolAsync(ToolsCallParams call, CancellationToken cancellationToken)
    {
        if (call.Name != CaptureViewDefinition.Name)
        {
            return inner.CallToolAsync(call, cancellationToken);
        }

        inner.Calls.Enqueue(call);
        var png = ToolImage.FromPng(TinyPng);
        var data = new JsonObject { ["viewCenter"] = new JsonArray(180.0, 240.0), ["viewHeight"] = 480.0 };
        var result = (call.Arguments?["mode"]?.GetValue<string>() ?? "ok") switch
        {
            "bad" => ToolResult.Ok("Captured with problems.", data) with
            {
                Images =
                [
                    png,
                    new ToolImage { MimeType = "image/bmp", Base64Data = TinyPngBase64 },
                    new ToolImage { MimeType = ToolImage.PngMimeType, Base64Data = "not base64!" },
                ],
                Warnings = ["Fake warning."],
            },
            "error" => ToolResult.Fail("The view could not be read completely.") with { Images = [png] },
            _ => ToolResult.Ok("Captured the current view (2 x 3 px).", data) with { Images = [png], Revision = 12 },
        };
        return Task.FromResult(result);
    }

    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken) =>
        inner.AskUserAsync(request, cancellationToken);

    public Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken) =>
        inner.UpdatePlanAsync(plan, cancellationToken);

    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, string? sessionId, CancellationToken cancellationToken) =>
        inner.AskUserAsync(request, sessionId, cancellationToken);

    public Task UpdatePlanAsync(PlanUpdate plan, string? sessionId, CancellationToken cancellationToken) =>
        inner.UpdatePlanAsync(plan, sessionId, cancellationToken);
}
