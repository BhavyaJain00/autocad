using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Recovery;

/// <summary>
/// Definitions (name, schema, LLM description) of the recovery tools: create_checkpoint, list_checkpoints,
/// restore_checkpoint and get_resume_info. The plug-in registers them with its bodies (ToolRegistry.Recovery.cs).
/// </summary>
public static class RecoveryTools
{
    /// <summary>Name of the tool that saves a checkpoint.</summary>
    public const string CreateCheckpoint = "create_checkpoint";

    /// <summary>Name of the tool that lists checkpoints.</summary>
    public const string ListCheckpoints = "list_checkpoints";

    /// <summary>Name of the tool that goes back to a checkpoint.</summary>
    public const string RestoreCheckpoint = "restore_checkpoint";

    /// <summary>Name of the tool that reports an unfinished job.</summary>
    public const string GetResumeInfo = "get_resume_info";

    /// <summary>create_checkpoint (Write: places an UNDO mark and writes a copy of the drawing; changes nothing drawn).</summary>
    public static ToolDefinition CreateCheckpointDefinition() => new()
    {
        Name = CreateCheckpoint,
        Title = "Create checkpoint",
        Risk = ToolRisk.Write,
        Description =
            "Save a named checkpoint of the active drawing that restore_checkpoint can go back to: an AutoCAD UNDO mark (exact " +
            "restore while the drawing stays open) plus a copy of the drawing in the copilot's own folder (restore after AutoCAD " +
            "was closed or crashed). Nothing in the drawing changes and the user's file is not saved. Use it before a risky or big " +
            "change; run_commands already creates one automatically before calls of 10 or more steps (\"before: <title>\"). " +
            "A checkpoint with the same name is replaced. Returns the name and what the drawing held (objects, layers).",
        InputSchema = Schema.Object(
            Schema.Req("name", Schema.String("Short name, e.g. \"walls done\" or \"before furniture\".", minLength: 1, maxLength: CheckpointNames.MaxLength))),
    };

    /// <summary>list_checkpoints (Read).</summary>
    public static ToolDefinition ListCheckpointsDefinition() => new()
    {
        Name = ListCheckpoints,
        Title = "List checkpoints",
        Risk = ToolRisk.Read,
        Description =
            "List the checkpoints of the active drawing, newest first: name, time, objects and layers at that moment, whether it can " +
            "be restored exactly with AutoCAD's undo (\"undo\") or only from its saved copy (\"file\": after AutoCAD was restarted).",
        InputSchema = Schema.Object(),
    };

    /// <summary>restore_checkpoint (Destructive: the user approves it).</summary>
    public static ToolDefinition RestoreCheckpointDefinition() => new()
    {
        Name = RestoreCheckpoint,
        Title = "Restore checkpoint",
        Risk = ToolRisk.Destructive,
        Description =
            "Put the active drawing back to a checkpoint (the user approves it first). Everything done after the checkpoint is " +
            "undone, including the user's own changes, so say what will be lost before calling it. Normally it uses AutoCAD's " +
            "UNDO Back to the checkpoint's mark and checks that the drawing matches the checkpoint; if it does not, the undo is " +
            "reversed (REDO) and nothing is lost. When only the saved copy is left (AutoCAD was restarted), call it with " +
            "fromFile true: then all model-space objects are replaced by the copy's (new handles; layouts, layers and styles of " +
            "the current drawing stay). Returns what was restored and how.",
        InputSchema = Schema.Object(
            Schema.Req("name", Schema.String("The checkpoint's name (from list_checkpoints).", minLength: 1, maxLength: CheckpointNames.MaxLength)),
            Schema.Opt("fromFile", Schema.Boolean("true = restore from the saved copy instead of AutoCAD's undo (only when undo is not available). Default false."))),
    };

    /// <summary>get_resume_info (Read).</summary>
    public static ToolDefinition GetResumeInfoDefinition() => new()
    {
        Name = GetResumeInfo,
        Title = "Get resume info",
        Risk = ToolRisk.Read,
        Description =
            "Tell what an unfinished drafting job on the active drawing had done and what remains, from the copilot's job journal " +
            "(kept while drawing: the plan's tasks, every run_commands call and the steps that finished). Use it when the user asks " +
            "to continue or resume, or after AutoCAD or the CLI stopped in the middle of a drawing. Returns the tasks done and " +
            "remaining, the call that was interrupted (steps typed and not typed), its checkpoint, and what to do next; or that " +
            "there is nothing to resume.",
        InputSchema = Schema.Object(),
    };

    /// <summary>All recovery tool definitions.</summary>
    public static IReadOnlyList<ToolDefinition> All() =>
        [CreateCheckpointDefinition(), ListCheckpointsDefinition(), RestoreCheckpointDefinition(), GetResumeInfoDefinition()];
}
