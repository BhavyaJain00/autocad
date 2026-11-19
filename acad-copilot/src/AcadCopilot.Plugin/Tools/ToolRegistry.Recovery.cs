using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Recovery;

namespace AcadCopilot.Plugin.Tools;

/// <summary>Recovery tools (create_checkpoint, list_checkpoints, restore_checkpoint, get_resume_info).</summary>
public sealed partial class ToolRegistry
{
    // Called from AddDraftingTools (ToolRegistry.Drafting.cs), so ToolRegistry.cs needs no change.
    private static void AddRecoveryTools(List<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Add(new ToolSpec(RecoveryTools.CreateCheckpointDefinition(), CreateCheckpointToolAsync));
        tools.Add(new ToolSpec(RecoveryTools.ListCheckpointsDefinition(), ListCheckpointsToolAsync));
        tools.Add(new ToolSpec(RecoveryTools.RestoreCheckpointDefinition(), RestoreCheckpointToolAsync));
        tools.Add(new ToolSpec(RecoveryTools.GetResumeInfoDefinition(), GetResumeInfoToolAsync));
    }

    private static Task<ToolResult> CreateCheckpointToolAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var name = new ToolArguments(arguments).GetString("name");
        return context.InvokeAsync(() => CheckpointService.CreateToolAsync(context.Dispatcher, name), cancellationToken);
    }

    private static Task<ToolResult> ListCheckpointsToolAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken) =>
        context.InvokeAsync(CheckpointService.ListTool, cancellationToken);

    private static Task<ToolResult> RestoreCheckpointToolAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var args = new ToolArguments(arguments);
        var name = args.GetString("name");
        var fromFile = args.GetBool("fromFile", false);
        return context.InvokeAsync(() => CheckpointService.RestoreToolAsync(context.Dispatcher, name, fromFile, cancellationToken), cancellationToken);
    }

    private static Task<ToolResult> GetResumeInfoToolAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        return context.InvokeAsync(
            () =>
            {
                var document = AcadDispatcher.RequireActiveDocument();
                var title = Path.GetFileName(document.Name);
                var jobs = RecoveryJournal.FindUnfinished(document, otherSessionsOnly: false);
                var checkpoints = new JsonArray(CheckpointService.AllOf(document)
                    .OrderByDescending(c => c.CreatedUtc)
                    .Take(10)
                    .Select(c => (JsonNode?)c.Name)
                    .ToArray());
                if (jobs.Count == 0)
                {
                    return ToolResult.Ok(
                        $"Nothing to resume on {title}: the copilot's journal has no unfinished job for this drawing.",
                        new JsonObject { ["found"] = false, ["drawing"] = title, ["checkpoints"] = checkpoints });
                }

                var job = jobs[0];
                var data = job.ToJson();
                data["found"] = true;
                data["advice"] = job.Advice();
                data["checkpoints"] = checkpoints;
                data["otherUnfinishedJobs"] = jobs.Count - 1;
                data["thisConversation"] = string.Equals(job.Session, RecoveryContext.SessionId, StringComparison.OrdinalIgnoreCase);
                return ToolResult.Ok(job.Summarize() + " " + job.Advice(), data);
            },
            cancellationToken);
    }
}
