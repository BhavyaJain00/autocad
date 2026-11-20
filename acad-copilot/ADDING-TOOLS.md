# Adding a tool

The AI drafts through **one general tool, `run_commands`**: it types AutoCAD commands and their inputs on the command
line, the way a person drafts. The user explicitly did not want purpose-built drawing tools (no `create_wall`,
`add_door` or house planner). So add a tool only for something the command line cannot do well:

- **reading** data the AI needs to plan or check its work (objects, styles, blocks, views);
- **looking** at the drawing (pictures);
- **safe session helpers** the command line handles badly.

New drawing behaviour usually belongs in the **instructions** (`src/AcadCopilot.Core/Agents/CopilotInstructions.cs`)
or the **command policy** (`src/AcadCopilot.Core/Drafting/CommandTables.cs`), not in a new tool.

Every tool lives in the plug-in's `ToolRegistry`. The MCP bridge lists the tools dynamically (`tools.list` over the
pipe), so a new tool needs **no change anywhere else**. Build, restart AutoCAD, and every CLI sees it.

## Where tools live

| File | Tools | Owner |
|---|---|---|
| `src/AcadCopilot.Plugin/Tools/ToolRegistry.cs` | `ToolSpec`, the registry, `CreateDefault()` (calls the partial methods below in order) | foundation |
| `src/AcadCopilot.Plugin/Tools/ToolRegistry.Drafting.cs` | `run_commands`, `cancel_command`, `undo` | drafting |
| `src/AcadCopilot.Plugin/Tools/ToolRegistry.Inspect.cs` | `get_drawing_info`, `capture_view`, `list_layers`, `list_entities`, `get_entity`, `get_sysvars` | vision |
| `src/AcadCopilot.Plugin/Tools/ToolContext.cs` | what a handler gets: main-thread access, approvals, units, handles, log | foundation |
| `src/AcadCopilot.Plugin/Tools/ToolRegistry.Sheets.cs` | `plan_full_set`, `arrange_drawings`, `create_sheets`, `plot_to_pdf` (definitions and pure maths in `Core/Sheets/`, AutoCAD work in `Plugin/Sheets/`) | drawing sets |
| `src/AcadCopilot.Plugin/Tools/ToolRegistry.Learning.cs` | `learn_style_from_drawing` (reads the drawing; analysis in `Core/Learning/StyleAnalyzer.cs`) | learning |
| `src/AcadCopilot.Core/Interaction/BridgeTools.cs` | `ask_user`, `update_plan`, `acad_status`, implemented by the bridge itself | core |
| `src/AcadCopilot.Core/Learning/KnowledgeTools.cs` | `recall_knowledge`, `remember_rule`, `save_recipe`, `save_lesson`, `forget`, implemented by the bridge itself over the knowledge files (no AutoCAD needed) | learning |

Each `ToolRegistry.*.cs` file is a `public sealed partial class ToolRegistry` that implements one
`static partial void Add...Tools(List<ToolSpec> tools)` method. For a new group, declare another partial method in
`ToolRegistry.cs` and call it from `CreateDefault()`.

A tool that never touches AutoCAD (it only reads or writes files, like the learning tools) belongs in the **bridge**
instead: in the plug-in every tool whose risk is not Read waits in the drawing queue and activates its tab's drawing
first. Bridge tools are listed in `BridgeToolService.ListToolsAsync`, dispatched in `CallToolAsync`, and their names
go into `BridgeTools.ReservedNames`.

A tool is a `ToolSpec(ToolDefinition Definition, Func<ToolContext, JsonObject, CancellationToken, Task<ToolResult>> Handler)`.

## What happens on every call (do not do it yourself)

`PluginPipeHandler` (`src/AcadCopilot.Plugin/Ipc/`) runs each `tools.call` like this:

1. **Unknown name**: a failed result that lists the available tools.
2. **Validation**: the arguments are checked against `InputSchema` with `JsonSchemaValidator`. Invalid arguments never
   reach your handler; the AI gets the list of errors (`$.steps[0].inputs: expected array, got string`) and fixes the
   call.
3. **Approval**: `Risk = Destructive` shows the panel's **Approval needed** card first. If the user rejects, your
   handler never runs and the AI is told not to retry. Tools that only *sometimes* need approval (`run_commands` before
   a `SAVE` or `PURGE`) stay `Write` and call `context.ApproveAsync(...)` themselves.
4. **One writer at a time**: `Write` and `Destructive` calls are serialised. `Read` calls are not.
5. **Your handler** runs on a thread-pool thread with a `ToolContext` and the cancellation token. The token fires on
   Stop in the panel, or when the bridge abandons the call.
6. **Errors**:
   - `throw new ToolException("clear, actionable message")` becomes a failed result with that message, for problems
     the AI can fix.
   - `AcadBusyException` (from the dispatcher) becomes the "AutoCAD is busy" RPC error.
   - `Autodesk.AutoCAD.Runtime.Exception` becomes `AutoCAD error <ErrorStatus> in <tool>`.
   - Anything else is logged with its stack trace and becomes `Unexpected error in <tool>`.
7. **Revision**: the current drawing revision is added to every result. Each call is logged in
   `%LOCALAPPDATA%\AcadCopilot\logs\addin.log`.

## ToolContext in one minute

| Member | Use |
|---|---|
| `ReadAsync((document, transaction) => ..., ct)` | Read the drawing on AutoCAD's main thread in an open/close transaction (no lock). |
| `WriteAsync((document, transaction) => ..., ct)` | Change the drawing on the main thread: document lock and one committed transaction. |
| `InvokeAsync(() => ..., ct)` | Anything else on the main thread (views, system variables, the editor). |
| `Document`, `Database`, `Editor` | Only valid **inside** the three calls above (they throw on other threads). |
| `ApproveAsync(title, details, ct)` | Ask the user in the panel. `false` = rejected, cancelled or no panel. |
| `GetUnits(database)`, `Length(value, units)`, `Point(point, units)`, `Round(value)` | Results in drawing units **plus** feet-inch text (`{"du": 150, "ftIn": "12'-6\""}`, `du` = drawing units). |
| `HandleOf(id)`, `HandleValueOf(id)`, `GetObjectId(database, "2A3F")` | Hex handles ("2A3F"), the ids the AI sees everywhere. |
| `Log(...)`, `Warn(...)` | `addin.log`, tagged with the tool and the call id. |

Never touch AutoCAD objects outside `ReadAsync` / `WriteAsync` / `InvokeAsync`. Never block the main thread
waiting for something that needs the main thread (for example `ApproveAsync` inside `InvokeAsync`). Never show a
dialog.

## Conventions the AI relies on

- **Names**: `snake_case`, verb first, 2-64 characters (`list_blocks`, `get_entity`). `ask_user`, `update_plan`
  and `acad_status` are reserved for the bridge, and the registry refuses them (and duplicates) at start-up.
- **Risk**: `Read` (looks only), `Write` (changes the drawing), `Destructive` (always asks first). Pick the
  lowest that is true.
- **Schemas**: strict and flat. Build them with `Schema.Object(Schema.Req(...), Schema.Opt(...))`, which sets
  `additionalProperties: false`, and use only the keywords `JsonSchemaValidator` supports (type, const, enum,
  properties, required, additionalProperties, items, minItems/maxItems, uniqueItems, minLength/maxLength, pattern,
  minimum/maximum, exclusiveMinimum/exclusiveMaximum, allOf/anyOf/oneOf). Prefer flat objects without allOf/anyOf/oneOf:
  Codex and Gemini are stricter about schemas than Claude. `definition.Lint()` must return no problems.
- **Descriptions** are written for the LLM: what the tool does, when to use it, the units, what it returns, and its
  limits.
- **Results**: `ToolResult.Ok(message, data)` / `ToolResult.Fail(error)`. Lengths are in drawing units with an
  `ftIn` text. Objects are identified by hex handle. Put created / modified / erased handles in
  `CreatedIds` / `ModifiedIds` / `DeletedIds` (`HandleValueOf`). Pictures go in `Images` (`ToolImage`); the bridge
  sends them as MCP image content.
- **Keep results small**: page long lists (`offset` / `limit`) and say when a list was cut.

## Example: a read tool

`list_blocks` lists the block definitions so the AI can place existing furniture blocks with `-INSERT`. This is an
illustration. The API names are checked against the installed `acdbmgd.dll`, but the snippet itself has not been
compiled.

```csharp
using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Tools;

public sealed partial class ToolRegistry
{
    private static ToolSpec ListBlocksTool() => new(
        new ToolDefinition
        {
            Name = "list_blocks",
            Title = "List blocks",
            Risk = ToolRisk.Read,
            Description =
                "List the block definitions of the active drawing (name and number of references) so you can place " +
                "an existing block with -INSERT. Anonymous, layout and xref blocks are left out.",
            InputSchema = Schema.Object(
                Schema.Opt("name_contains", Schema.String("Only blocks whose name contains this text (any case)."))),
        },
        async (context, arguments, cancellationToken) =>
        {
            var filter = new ToolArguments(arguments).GetStringOrNull("name_contains");
            var blocks = await context.ReadAsync(
                (document, transaction) =>
                {
                    var list = new JsonArray();
                    var table = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in table)
                    {
                        var block = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                        if (block.IsLayout || block.IsAnonymous || block.IsFromExternalReference)
                        {
                            continue;
                        }

                        if (filter is not null && !block.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var references = block.GetBlockReferenceIds(true, false);
                        list.Add(new JsonObject { ["name"] = block.Name, ["references"] = references.Count });
                    }

                    return list;
                },
                cancellationToken).ConfigureAwait(false);

            return ToolResult.Ok($"{blocks.Count} block definitions.", new JsonObject { ["blocks"] = blocks });
        });
}
```

Register it in the group it belongs to, here `AddInspectTools` in `ToolRegistry.Inspect.cs`:

```csharp
tools.Add(ListBlocksTool());
```

If the new tool changes how the AI should work (for example "look up block names with `list_blocks` before
`-INSERT`"), add one line to `CopilotInstructions.cs`. Keep `CopilotInstructions.Default` under 15,500 characters
(a test checks it): every turn also appends the product note and the "WHAT YOU HAVE LEARNED" digest (up to 4,000),
and Codex gets the whole text on its command line (Windows limit 32,767). The text is written for plain AutoCAD:
never suggest AutoCAD Architecture commands there (`CopilotInstructions.ArchitectureNote` is added only when the host
is AutoCAD Architecture).

## Changing which commands the AI may type

`run_commands` checks every step against the command policy in `src/AcadCopilot.Core/Drafting/` before anything is
typed. The lists are in `CommandTables.cs`:

| Table | Effect |
|---|---|
| `Blocked`, `BlockedPrefixes` | never typed (shell, code loading, scripts, files, UNDO/U ...), with the reason shown to the AI |
| `ProtectedSystemVariables` | never changed through SETVAR (SECURELOAD, TRUSTEDPATHS ...) |
| `NeedsApproval`, `NeedsApprovalPrefixes`, `EraseCommands` | typed only after Approve in the panel (SAVE, PLOT, EXPORT..., PURGE, ERASE ALL ...) |
| `CommandLineVersions` | dialog commands replaced by their command-line version (LAYER -> -LAYER, HATCH -> -HATCH ...) |
| `DialogOnly` | dialog-only commands, refused with what to use instead |
| `DefaultAliases` | acad.pgp aliases that lead to any of the above; the user's own acad.pgp is read as well |

Inputs that start with `(` or `!` (AutoLISP) are always refused. Command names are normalised first: the leading
`_ . - +` and the transparent `'` are removed.

## Testing

The tool bodies need AutoCAD, so outside AutoCAD they are **only compile-verified**. Say so in your report, and add
the tool to the first-run checklist in `docs/USER-GUIDE.md`. What you can test without AutoCAD:

- the definition: `definition.Lint()` is empty, and `JsonSchemaValidator.Validate(definition.InputSchema, args)` accepts
  your typical arguments and rejects wrong ones;
- any pure helper kept free of AutoCAD types (put it in `AcadCopilot.Core`, test it in `tests/AcadCopilot.Core.Tests`);
- the bridge side with the stand-in AutoCAD in `tests/AcadCopilot.McpBridge.Tests`.

Then build and test: `powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Test` (it waits for the shared build
lock `.build.lock`, so only one build runs at a time on this PC).

## Checklist

1. Can `run_commands` do it? Then improve the instructions instead.
2. Name, title, risk, strict schema, LLM-ready description.
3. AutoCAD access only inside `ReadAsync` / `WriteAsync` / `InvokeAsync`; `ToolException` for fixable problems.
4. Results with handles and feet-inch text; small, paged lists.
5. Registered in a `Add...Tools` partial method; `Lint()` clean; tests for schema and helpers.
6. `docs/USER-GUIDE.md` (first-run checklist) and, if needed, `CopilotInstructions.cs` updated.
