using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Drafting;

namespace AcadCopilot.Plugin.Tools;

/// <summary>Drafting tools (run_commands, cancel_command, undo).</summary>
public sealed partial class ToolRegistry
{
    static partial void AddDraftingTools(List<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Add(new ToolSpec(DraftingTools.RunCommandsDefinition(), RunCommandsToolAsync));
        tools.Add(new ToolSpec(DraftingTools.CancelCommandDefinition(), CancelCommandToolAsync));
        tools.Add(new ToolSpec(DraftingTools.UndoDefinition(), UndoToolAsync));
    }

    // run_commands: validate -> policy (built-in + the user's acad.pgp, AutoLISP-defined commands) -> one approval for
    // the commands that need it -> type everything visibly on AutoCAD's main thread (DraftingEngine).
    private static async Task<ToolResult> RunCommandsToolAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!RunCommandsRequest.TryParse(arguments, out var request, out var errors))
        {
            return ToolResult.Fail("Invalid run_commands arguments; fix them and call again.", errors);
        }

        var (aliases, plan, scripted) = await context.InvokeAsync(
            () =>
            {
                var document = AcadDispatcher.RequireActiveDocument();
                var loaded = PgpAliases.Load(document.Database);
                var checkedPlan = DraftingPlan.Create(request!, loaded);
                return (loaded, checkedPlan, DraftingEngine.FindScriptedCommands(checkedPlan));
            },
            cancellationToken).ConfigureAwait(false);

        if (plan.IsBlocked || scripted.Count > 0)
        {
            var reasons = plan.BlockedErrors.Concat(scripted).ToList();
            context.Log("Refused: " + string.Join(" ", reasons));
            return DraftingEngine.NotRun(plan, RunOutcome.Blocked, "Not allowed, nothing was typed: " + string.Join(" ", reasons)).ToToolResult() with
            {
                Errors = reasons,
            };
        }

        if (plan.NeedsApproval)
        {
            var subjects = plan.ApprovalSteps
                .SelectMany(s => s.Findings.Where(f => f.NeedsApproval).Select(f => f.Subject))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var title = "Allow " + string.Join(", ", subjects);
            if (!await context.ApproveAsync(title, plan.ApprovalSummary(), cancellationToken).ConfigureAwait(false))
            {
                return DraftingEngine.NotRun(
                    plan,
                    RunOutcome.Declined,
                    $"The user did not approve '{title}', so nothing was typed. Do not retry it; ask the user what to do instead.").ToToolResult();
            }
        }

        var outcome = await context.InvokeAsync(
            () => DraftingEngine.RunAsync(plan, aliases, context.Dispatcher, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return outcome.ToToolResult();
    }

    private static Task<ToolResult> CancelCommandToolAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken) =>
        context.InvokeAsync(DraftingCommands.CancelAsync, cancellationToken);

    private static Task<ToolResult> UndoToolAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var count = (int)Math.Clamp(new ToolArguments(arguments).GetLong("count", 1), 1, DraftingTools.MaxUndoCount);
        return context.InvokeAsync(() => DraftingCommands.UndoAsync(context.Dispatcher, count, cancellationToken), cancellationToken);
    }
}
