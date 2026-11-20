using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Drafting;

/// <summary>
/// Definitions (name, schema, description written for the LLM) of the drafting tools: run_commands,
/// cancel_command and undo. The plug-in registers them with its bodies; they live here so they are unit-tested.
/// </summary>
public static class DraftingTools
{
    /// <summary>Name of the tool that types AutoCAD commands visibly.</summary>
    public const string RunCommands = "run_commands";

    /// <summary>Name of the tool that presses Esc twice.</summary>
    public const string CancelCommand = "cancel_command";

    /// <summary>Name of the tool that undoes the copilot's last calls.</summary>
    public const string Undo = "undo";

    /// <summary>Most run_commands calls one undo call can take back.</summary>
    public const int MaxUndoCount = 20;

    /// <summary>The run_commands definition (Risk Write; dangerous commands are approved inside the call).</summary>
    public static ToolDefinition RunCommandsDefinition() => new()
    {
        Name = RunCommands,
        Title = "Run AutoCAD commands",
        Risk = ToolRisk.Write,
        Description = RunCommandsDescription,
        InputSchema = Schema.Object(
            Schema.Req("title", Schema.String(
                "Short name of this batch, shown in the panel and used as the undo step name (\"Outer walls\", \"Room labels\").",
                minLength: 1,
                maxLength: DraftingLimits.MaxTitleLength)),
            Schema.Req("steps", Schema.Array(
                "The commands to type, in order (1-30). Batch a whole stage into one call.",
                Schema.Object(
                    Schema.Req("command", Schema.String(
                        "ONE AutoCAD command name as typed at the Command prompt: LINE, PLINE, RECTANG, OFFSET, TRIM, FILLET, -LAYER, -HATCH, TEXT, DIMLINEAR, ZOOM...",
                        minLength: 1,
                        maxLength: DraftingLimits.MaxCommandLength)),
                    Schema.Opt("inputs", Schema.Array(
                        "Answers to the command's prompts, in order, each typed and followed by Enter. \"\" = Enter alone. Options by their capital letters (C, U, E).",
                        Schema.String("One answer: a coordinate (0,0  @360,0  @360<90), a distance, an option letter, a name, text, or \"\" for Enter.", maxLength: DraftingLimits.MaxInputLength),
                        maxItems: DraftingLimits.MaxInputsPerStep)),
                    Schema.Opt("note", Schema.String("Optional short note shown with the step (\"plot boundary\").", maxLength: DraftingLimits.MaxNoteLength))),
                minItems: 1,
                maxItems: DraftingLimits.MaxSteps)),
            Schema.Opt("autoFix", Schema.Boolean(
                "Self-healing, default true: safe syntax fixes when AutoCAD rejects an input (feet-inches in a decimal drawing, \"10 20\" for 10,20, keyword typos, degree signs) and the finishing Enter you left out. Every fix is reported as auto-fixed. false = type exactly as given.")),
            Schema.Opt("autoCheckpoint", Schema.Boolean(
                $"Create a checkpoint (restore_checkpoint can go back to it) before typing. Default: true for calls with {RunCommandsRequest.AutoCheckpointMinSteps} or more steps (a whole stage), false otherwise."))),
    };

    /// <summary>The cancel_command definition (Risk Write, no arguments).</summary>
    public static ToolDefinition CancelCommandDefinition() => new()
    {
        Name = CancelCommand,
        Title = "Cancel AutoCAD command",
        Risk = ToolRisk.Write,
        Description =
            "Press Esc twice in AutoCAD to cancel the command that is running, exactly like the user pressing Esc. " +
            "Use it when run_commands reports that AutoCAD is busy with a command and the user agrees that it may be cancelled, " +
            "or to get AutoCAD back to an empty Command prompt. It does not undo anything that was already drawn. " +
            "Returns which command was cancelled (or that none was running).",
        InputSchema = Schema.Object(),
    };

    /// <summary>The undo definition (Risk Write, no approval: it only takes back the copilot's own calls).</summary>
    public static ToolDefinition UndoDefinition() => new()
    {
        Name = Undo,
        Title = "Undo copilot drawing",
        Risk = ToolRisk.Write,
        Description =
            "Undo the copilot's own last run_commands calls in the active drawing: each call is one undo step, so count 1 takes back " +
            "the whole last batch (like pressing Ctrl+Z once). Use it to throw away a wrong batch before drawing it again. " +
            "It refuses when the drawing was changed after the copilot's last call (by the user, or another program), so the user's " +
            "own work is never undone; then ask the user to press Ctrl+Z themselves. Returns how many calls were undone and what was removed.",
        InputSchema = Schema.Object(
            Schema.Opt("count", Schema.Integer($"How many of the copilot's last run_commands calls to undo (1-{MaxUndoCount}, default 1).", minimum: 1, maximum: MaxUndoCount))),
    };

    /// <summary>All drafting tool definitions.</summary>
    public static IReadOnlyList<ToolDefinition> All() => [RunCommandsDefinition(), CancelCommandDefinition(), UndoDefinition()];

    /// <summary>The run_commands description (exposed for tests and docs).</summary>
    public const string RunCommandsDescription =
        "Type AutoCAD commands and their inputs into the AutoCAD command line, exactly like a drafter at the keyboard. " +
        "The user watches each command and every number appear in the command line and the objects being drawn one by one. " +
        "This is how you draw and edit: with AutoCAD's own commands (LINE, PLINE, RECTANG, CIRCLE, ARC, OFFSET, TRIM, EXTEND, " +
        "FILLET, CHAMFER, BREAK, JOIN, MOVE, COPY, MIRROR, ROTATE, SCALE, STRETCH, ARRAY, -HATCH, TEXT, DIMLINEAR, DIMALIGNED, " +
        "DIMCONTINUE, -LAYER, -LINETYPE, -INSERT, CHPROP, ZOOM...).\n" +
        "\n" +
        "HOW IT TYPES: each step types `command`, then each item of `inputs` followed by Enter, and waits until AutoCAD has taken it " +
        "before typing the next. \"\" is Enter alone (finishes LINE/PLINE, accepts a <default>, ends a selection). Options shown in " +
        "[brackets] are typed by their capital letters (C = Close, U = Undo, E = Exit). If a command still waits after its last input, " +
        "the copilot presses Esc and reports the prompt; inputs left over after a command ended are not typed. " +
        "The whole call is ONE undo step (Ctrl+Z or the undo tool removes it).\n" +
        "SELF-HEALING (autoFix, default on): when AutoCAD rejects an input the copilot retries it once with a pure syntax fix " +
        "(feet-inches or mm/m converted to drawing units when LUNITS does not take them, \"10 20\" -> 10,20, a misspelt option keyword " +
        "-> its capital letters, 90deg -> 90) and presses the finishing Enter you forgot at prompts like 'Specify next point' or " +
        "'Select objects:'. Each fix is listed under 'Auto-fixed' in the result: write it that way yourself next time. " +
        "Calls of 10+ steps first create a checkpoint named \"before: <title>\" (autoCheckpoint) that restore_checkpoint can go back to.\n" +
        "\n" +
        "COORDINATES (current UCS, normally World): absolute x,y such as 0,0 or 360,480; relative @dx,dy such as @360,0; " +
        "polar @distance<angle such as @360<90 (degrees, counter-clockwise, 0 = east/right, 90 = north/up). Typed coordinates are exact: " +
        "object snaps and Dynamic Input do not change them during the call. Never type a lone number at a point prompt (AutoCAD would " +
        "use the mouse direction) and never put spaces in a coordinate.\n" +
        "UNITS: call get_drawing_info first. Feet-inches such as 30' 12'6\" 12'-6\" 4-1/2\" 9\" work ONLY when LUNITS is 3 or 4 " +
        "(architectural); there 1 unit = 1 inch, so 30' = 360. With other LUNITS type plain numbers in drawing units. Distances " +
        "(OFFSET 9\", FILLET radius, text height) follow the same rule.\n" +
        "\n" +
        "DIALOGS ARE OFF during the call (CMDDIA/FILEDIA/ATTDIA = 0), so use command-line versions: -LAYER, -HATCH, -INSERT, -LINETYPE, " +
        "-STYLE, -DIMSTYLE, -UNITS, -BLOCK, -RENAME, -PURGE, -PLOT, -OSNAP, -MTEXT (dialog commands are switched to these automatically). " +
        "For words use TEXT: it takes the text on the command line here (start point or J + justification, height (asked only when the " +
        "text style's height is 0), rotation angle, the text, then \"\"). For leader notes use QLEADER (its text is typed on the " +
        "command line; MLEADER opens a text editor). Change layer/colour with CHPROP, not PROPERTIES. " +
        "Spaces act like Enter at every prompt except text prompts (TEXT's text, attribute values), so put each answer in its own input.\n" +
        "SELECTING: at 'Select objects:' type a point exactly on an object, W or C then two corners, L (last), P (previous), then \"\" to " +
        "finish. Selecting by point needs the object visible on screen, so ZOOM to the area first if needed.\n" +
        "IF POINTS LAND WRONG: read the step's transcript and check get_sysvars WORLDUCS (0: run UCS W and tell the user); if points " +
        "were still pulled to nearby objects, set OSMODE to OSMODE+16384 (running object snaps off, like F3) and tell the user; undo, redo.\n" +
        "\n" +
        "WORK STYLE: batch a whole stage into one call (up to 30 steps; fewer calls = faster), use sensible layers (-LAYER M name C colour " +
        "name \"\"), then call capture_view to look at the result and fix mistakes before the next stage. Never draw the same thing twice: " +
        "check the result first.\n" +
        "NOT ALLOWED: SHELL, AutoLISP ( or !, scripts, APPLOAD/NETLOAD/VBA, OPEN/NEW/CLOSE/QUIT, UNDO/U (use the undo tool). SAVE/QSAVE/" +
        "SAVEAS, EXPORT, PLOT, PURGE, LAYDEL, WBLOCK and ERASE ALL ask the user first.\n" +
        "\n" +
        "RETURNS a summary with PROBLEMS first, and per step: the transcript (each prompt with what was typed), status (finished, " +
        "left_waiting...), rejected inputs with AutoCAD's message and a hint, and the objects created/modified/erased (handle, type, " +
        "layer, geometry in drawing units and feet-inches).\n" +
        "\n" +
        "EXAMPLES (architectural units, 1 unit = 1 inch):\n" +
        "1. 30'x40' plot boundary on its own layer: {\"title\":\"Plot boundary\",\"steps\":[{\"command\":\"-LAYER\",\"inputs\":[\"M\",\"PLOT\",\"C\",\"8\",\"PLOT\",\"\"]}," +
        "{\"command\":\"RECTANG\",\"inputs\":[\"0,0\",\"@30',40'\"]}]}\n" +
        "2. Offset that boundary 9\" inwards (pick a point on its left edge, then a point inside): {\"command\":\"OFFSET\",\"inputs\":[\"9\\\"\",\"0,20'\",\"15',20'\",\"\"]}\n" +
        "3. A centred room label 6\" high: {\"command\":\"TEXT\",\"inputs\":[\"J\",\"MC\",\"15',20'\",\"6\\\"\",\"0\",\"BED ROOM\",\"\"]}\n" +
        "4. Dimension along the bottom edge, 3' below it: {\"command\":\"DIMLINEAR\",\"inputs\":[\"0,0\",\"30',0\",\"@0,-3'\"]}";
}
