# AI Copilot for AutoCAD 2027

A plug-in for **AutoCAD 2027** (plain AutoCAD, R26.0, .NET 10; installed here: 2027.1.1 = 26.0.156) that adds a MecAgent-style AI chat panel
docked on the right of the drawing. The AI **drafts like a person**: it types AutoCAD's own commands and their inputs
on the command line (LINE, PLINE, OFFSET, BREAK, TRIM, FILLET, ARC, TEXT, DIMLINEAR, -HATCH, -LAYER ...), visibly,
one stage at a time. After each stage it looks at the drawing (screenshot plus object data) and fixes mistakes.

There are no purpose-built drawing tools. The AI has one general ability, **`run_commands`**, plus tools to **look at
the drawing**, so it can draw any drawing: every floor, elevations, sections, the site plan, furniture and
dimensions.

**No API keys.** The panel drives the user's own, already signed-in official CLIs: **Claude Code** (`claude`),
**OpenAI Codex CLI** (`codex`, ChatGPT login) or **Gemini CLI** (`gemini`, Google login). The CLIs reach AutoCAD through
an MCP server shipped with the plug-in (`AcadCopilotMcp.exe`).

End-user documentation: [docs/USER-GUIDE.md](docs/USER-GUIDE.md). Adding tools: [ADDING-TOOLS.md](ADDING-TOOLS.md).

```
 AutoCAD 2027 (acad.exe)                                        child processes (no windows)
 ┌────────────────────────────────────────────────────┐
 │ AI Copilot palette (WPF, docked right)              │
 │   ChatViewModel <-> CopilotBackend ─────────────────┼── runs ──> claude / codex / gemini (stream JSON -> AgentEvents)
 │      ^ ICopilotUi: questions, plan, approvals       │                │ MCP configured per invocation
 │      |                                              │                v
 │ PipeServer <── \\.\pipe\AcadCopilot-<pid> ──────────┼──────────── AcadCopilotMcp.exe (stdio MCP server "acad")
 │   PluginPipeHandler: validate -> approve -> ToolRegistry handler
 │   AcadDispatcher -> AutoCAD main thread: run_commands types on the command line (one undo group per call)
 └────────────────────────────────────────────────────┘
```

The same MCP server also works from the user's own Claude Desktop / Claude Code / Codex / Gemini sessions (see
docs/USER-GUIDE.md section 9).

## Status (0.1.0, 2026-11-17)

| What | State |
|---|---|
| Design | Rebuilt from the Revit version of this product (panel, CLI runners, pipe, MCP bridge, tested live there). The Revit house tools are gone; drafting is AutoCAD commands typed through `run_commands`. |
| Build | `scripts\build.ps1 -Zip` (2026-11-17, while the other parts were still being written): Release build of Core, the MCP bridge and the plug-in against the AutoCAD 2027 assemblies in `C:\Program Files\Autodesk\AutoCAD 2027\`, 0 warnings, 0 errors. Bundle `dist\AcadCopilot.bundle`: 17 files, 3.8 MB, no Autodesk DLLs, manifest module present, `AcadCopilotMcp.exe --version` = 0.1.0. `install.ps1 -WhatIf` (folder and zip) and `uninstall.ps1 -WhatIf` pass; nothing was installed. |
| Tests (no AutoCAD, no windows) | xUnit: `tests\AcadCopilot.Core.Tests` (command policy, schemas, pipe, discovery, CLI runners, backend), `tests\AcadCopilot.McpBridge.Tests` (the built `AcadCopilotMcp.exe` driven by the official MCP client against a stand-in AutoCAD, including image results), `tests\AcadCopilot.Plugin.Ui.Tests` (panel rendered off-screen). Run them with `scripts\build.ps1 -Test`. |
| CLIs | The runners were verified live with Claude Code and Codex CLI in the Revit version. Gemini CLI on this PC is in "API key" mode without a key: not verified live. |
| Chat tabs (2026-11-18) | Several conversations in parallel, one per tab, each bound to a drawing; drawing actions take turns in AutoCAD (see "Chat tabs" below). Tested without AutoCAD (queue, sessions, routing over the real pipe, bridge `--session`, view models, off-screen renders `artifacts\ui\11-chat-tabs-*.png`, `12-close-working-tab-*.png`). Binding to a document and switching the active document are compile-verified only. |
| Plain AutoCAD 2027 + learning (2026-11-19) | Retargeted from AutoCAD Architecture (removed from this PC) to plain AutoCAD 2027: manifest `Platform="AutoCAD\|AutoCAD*"`, no AEC references anywhere (the plug-in references only `acmgd`, `acdbmgd`, `accoremgd`, `AdWindows`), product detection via `AcadProduct`, instructions without Architecture objects unless the host is AutoCAD Architecture. Continuous learning (see "Learning"): Core 619 and bridge 67 tests green, including the learning tools through the built `AcadCopilotMcp.exe`. `learn_style_from_drawing` and the product detection inside AutoCAD are compile-verified only. |
| Composer helpers (2026-11-19) | **Point** (click points in the drawing, `[point 12'-6",30'-0" (150,360)]` goes into the message; `Plugin/Interaction/AcadDrawingInteraction.cs` runs `Editor.GetPoint` inside `DocumentCollection.ExecuteInCommandContextAsync`, waits for its turn in the AutoCAD queue), **selection chip** (pickfirst selection via `Document.ImpliedSelectionChanged` + `Editor.SelectImplied`; the message carries handles + short summaries), **task queue** per tab (Enter while busy queues; drag / arrows reorder; Stop or a failure pauses, Resume continues), **quick prompts** (built-ins + the user's own in `panel.json`), Settings **AI behaviour** switches (agent B's `ICopilotAssist.Features`), **review card** with Fix these (`ReviewCompleted`), **switch notice** (`ProviderSwitched`), **Resume previous job** card (crash recovery's `ResumeMonitor.OfferChanged`, wired in `PaletteHost`). UI 222 tests green, renders `artifacts\ui\16-*` .. `19-*.png`. The AutoCAD side (picking, selection events, `pick_point` / `get_selection` / `select_objects` bodies) is compile-verified only. |
| Learn from me (2026-11-19) | Recorder of the user's own manual work (see "Learn from me" below): Core 642, bridge 68, UI 196 tests green (recording model, steps, Markdown, caps, copilot actions ignored, `list_recordings` / `read_recording` also through the built `AcadCopilotMcp.exe`, Learning bar renders `artifacts\ui\15-learn-from-me-*.png`). The AutoCAD event capture (`Plugin/Learning/ActionRecorder.cs`) is compile-verified only. |
| Assistant, recovery, services (2026-11-19) | Four parts added in parallel: **AI switching** (another signed-in AI continues a job after a usage limit, sign-in problem or repeated failure, with a HANDOVER message built from the tab's `ConversationJournal`), **Ask before drawing**, **second-opinion review** (a separate read-only CLI call, `--readonly` bridge, review card with Fix these), **tutor mode**; **self-healing input** ("Auto-fixed"), **checkpoints** and **crash recovery** (job journal, `get_resume_info`, Resume card); **electrical / plumbing first-draft layouts** (block library + legend) and **colour presentation elevations** with PNG export. Everything that touches AutoCAD in these parts is compile-verified only. |
| Integration (2026-11-20) | Recovery wired into the pipe handler (`RecoveryContext.SessionId` around every tool handler, `RecoveryHooks.PlanUpdated` on `update_plan`) and `CopilotApp` (`ResumeMonitor.Start` after the palette exists, `Stop` at shutdown); the Resume card listens to `ResumeMonitor.OfferChanged` and resumes in a tab bound to that drawing; the panel's in-memory handover journal renamed `ConversationJournal` (the on-disk crash-recovery journal is `Core.Recovery.JobJournal` in `journal\`); activity rows show "Auto-fixed" lines, the checkpoint made before a call and an **Open exports folder** link; two quick prompts (electrical draft, colour elevation + PNG); instructions, guides and docs for the new tools. Release build of the whole solution 0 warnings / 0 errors; tests green: Core 803, bridge 75, UI 228. |
| Web research (2026-11-20) | `CopilotSettings.WebResearch` / `CopilotFeatures.WebResearch` (default off, shared by all tabs, persisted, read at the start of each turn) -> `TurnRequest.WebResearch`. **On**, only the read-only web tools: Claude Code `--tools WebSearch,WebFetch` (plus `Read` with attachments) and `--allowedTools mcp__acad__*,WebSearch,WebFetch`; Codex `-c web_search="live"` with the read-only sandbox, `features.shell_tool=false` and `approval_policy="never"` unchanged; Gemini's policy stops denying `google_web_search` / `web_fetch` and allows them (shell and write tools stay denied). **Off**: as before, plus Codex now gets `-c web_search="disabled"` explicitly (Codex 0.154 has a `web_search` mode of disabled / cached / indexed / live; the flag makes "off" really off). The reviewer never gets web tools. Instructions: one line in Default (no web unless TURN SETTINGS say so; otherwise point at the button), `WebResearchNote` in TURN SETTINGS when on (3-6 sources, 2-3 adapted layout ideas with rooms, sizes, Vastu notes and links via ask_user, no copying, user's rules first). Panel: globe **Web** toggle in every tab's composer row (grey off, accent on, icon only below 400 px) and Settings > AI BEHAVIOUR "Web research (let the AI search the internet for ideas)", same value; activity rows "Searching the web: <query>" / "Reading <site>". Tests: Core 809, bridge 75, UI 232 green; renders `artifacts\ui\20-web-*.png`. Live web use by each CLI is not verified (no CLI was run). |
| Focus layout (2026-11-20) | Palette always docked right at full height, 460 px until the user resizes it (`PaletteState.WidthChosen` / `DockWidth`; re-checked on creation, the next two idles and every COPILOT; auto-hide turned off). **Focus mode** (`Palette/FocusLayout.cs`, pure `FocusLayoutController` over `ILayoutSurface`; `Palette/AcadLayoutSurface.cs` inside AutoCAD): ribbon `Autodesk.Windows.RibbonControl.AutoHideMode` None -> HidePanelBar (tab names; API names verified by reflection over AdWindows.dll), NAVBARDISPLAY 0, NAVVCUBEDISPLAY 0 per drawing (document lock, DBMOD reset with `acdbSetDbmod` from acdb26.dll, user value restored around BeginSave / SaveComplete); restored on the Focus button, palette hide, `QuitWillStart` and Terminate; previous values in `focus-layout.json` for crash recovery at the next start; the command line is never touched; other palettes are not auto-hidden (no public API lists them). **Follow the AI's drawing** (`Drafting/ViewFollow.cs` + pure `Inspect/ViewFraming.cs`): after a run_commands call, zoom (`Editor.SetCurrentView`) to the created objects + 10% when outside the view or under 3% of it, at most 8x in, plan view of model space only, never during a command. Settings in panel.json (`FocusModeOnOpen`, `FollowAiDrawing`, both default on); slim one-line header below 420 px. Tests: Core 809, bridge 75, UI 248 green; renders `artifacts\ui\21-*.png`. Everything inside AutoCAD is compile-verified only. |
| **Compile-verified only (needs AutoCAD)** | Everything that runs inside AutoCAD: loading from the bundle, the palette and ribbon tab, the main-thread dispatcher, `run_commands` (typing, prompts, undo grouping, approvals), `capture_view` and the other inspect tools, `COPILOT` / `COPILOTSTATUS`. None of it has run inside AutoCAD yet. |

## Repository layout

| Path | What |
|---|---|
| `src/AcadCopilot.Core` (net10.0, no AutoCAD references) | tool contracts and JSON-schema validator, pipe JSON-RPC and discovery files, CLI runners, panel backend, **system instructions** (`Agents/CopilotInstructions.cs`), **command policy** and feet-inch helpers (`Drafting/`), **learning** (`Learning/`: knowledge files, recall, digest, style analysis), host product names (`AcadProduct.cs`) |
| `src/AcadCopilot.McpBridge` (net10.0 exe `AcadCopilotMcp.exe`) | stdio MCP server (official MCP C# SDK `ModelContextProtocol.Core` 2.4.0) |
| `src/AcadCopilot.Plugin` (net10.0-windows10.0.19041.0, WPF, `AcadCopilot.Plugin.dll`) | the AutoCAD plug-in: `Acad/` (start-up, dispatcher, revision tracker, bridge locator), `Commands/` (COPILOT, COPILOTSTATUS), `Ipc/` (pipe handler), `Palette/` (PaletteSet docked right, focus layout, ribbon tab), `Tools/` (registry and tool bodies), `ViewModels/`, `Views/` (chat panel) |
| `deploy/AcadCopilot.bundle/PackageContents.xml` | AutoCAD autoloader manifest (bundle template): loads `./Contents/AcadCopilot.Plugin.dll` at start-up in AutoCAD 2027 (`Platform="AutoCAD\|AutoCAD*"` = plain AutoCAD plus AutoCAD-based products of the release, as in Autodesk's own FeaturedApps bundle), R26.0 only, with the COPILOT and COPILOTSTATUS commands |
| `scripts/build.ps1`, `install.ps1`, `uninstall.ps1` | build + bundle, per-user install, uninstall |
| `tests/` | xUnit test projects (see Status) |

## Build, test, install

.NET SDK 10 lives at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (not on PATH); `build.ps1` finds it.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1               # Release build -> dist\AcadCopilot.bundle
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Test -Zip    # + all tests + dist\AcadCopilot.bundle-<version>.zip
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -WhatIf     # preview (AutoCAD must be closed for the real run)
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

- AutoCAD assemblies (`acmgd`, `acdbmgd`, `accoremgd`, `AdWindows`) come from `AcadInstallDir` in
  `Directory.Build.props` (default `C:\Program Files\Autodesk\AutoCAD 2027\`, override with
  `-AcadInstallDir` / `-p:AcadInstallDir=...`). They are referenced with `Private=false` and never shipped:
  `build.ps1` also skips and rejects any file whose company is Autodesk.
- **Build lock**: RAM is tight and builds share `obj` folders, so every `dotnet build`/`test` in `build.ps1` runs
  under `<repo>\.build.lock` (created exclusively, released right after; older than 12 minutes = stale). Builds use
  `-m:1 -nodeReuse:false -p:UseSharedCompilation=false`, and the script ends with `dotnet build-server shutdown`,
  so no build process is left running. Use the same lock when you run `dotnet` by hand.
- **Isolated package build**: `build.ps1` builds the package with `--artifacts-path artifacts\build` (its own
  `bin\` and `obj\`). An open VS Code with the C# Dev Kit runs design-time builds of these projects outside the
  lock, and the shared `obj\Release` of the plug-in lost WPF's generated `CopilotPane.g.cs` that way (CS2001), even
  with `--no-incremental`. `-InPlace` builds in the projects' own folders. `-Test` always runs the tests in place,
  because the MCP end-to-end test starts `src\AcadCopilot.McpBridge\bin\<Config>\net10.0\AcadCopilotMcp.exe`.
- Warnings are errors in every project except the plug-in.
- The plug-in build copies the bridge into `src/AcadCopilot.Plugin/bin/<Config>/net10.0-windows10.0.19041.0/bridge/`.
- Tests never open windows and never touch the real `%LOCALAPPDATA%\AcadCopilot` (temp folders, unique pipe
  names, `ACADCOPILOT_HOME`).
- `install.ps1` refuses while `acad.exe` runs (or while the installed `AcadCopilotMcp.exe` runs), copies into a
  staging folder and swaps it in, unblocks the files and warns about all-users copies. It never starts or stops
  AutoCAD and never touches the CLIs' configuration. `uninstall.ps1 [-RemoveUserData]` is the reverse.

## Runtime layout

```
%APPDATA%\Autodesk\ApplicationPlugins\AcadCopilot.bundle\        <- dist\AcadCopilot.bundle (per user, no admin rights)
    PackageContents.xml                  autoloader manifest (AppVersion = build version)
    Contents\AcadCopilot.Plugin.dll, AcadCopilot.Core.dll
    Contents\bridge\AcadCopilotMcp.exe (+ its dlls, runtimeconfig, deps)
%LOCALAPPDATA%\AcadCopilot\
    instances\<acadPid>.json     discovery file (pipe + token), written at start-up, deleted at shutdown
    logs\addin.log, logs\bridge.log
    workspace\                   private working folders of the CLIs (claude\, codex\, gemini ...)
    knowledge\                   what the AI learned: rules.md, style.md, lessons.md, recipes\*.md (user-editable),
                                 recordings\*.md ("Learn from me": the user's own manual work)
    checkpoints\<drawing>_<hash>\ copies of the drawing per checkpoint (*.dwg) + index.json; max 20 per drawing, 7 days
    journal\<session>.json       job journal per chat session (or "drawing-<drawing>_<hash>"): plan tasks, run_commands
                                 calls and finished steps, drawing path, AutoCAD pid; deleted after 7 days
    settings.json                selected provider + model per provider, AI behaviour switches (no secrets)
%USERPROFILE%\Documents\AcadCopilot Exports\   PNGs written by export_view_png (<name>_<yyyyMMdd-HHmmss>.png)
```

The bundle also carries `Contents\Blocks\AcadCopilot-Services.dxf` (the electrical / plumbing block library, made by
`scripts\make-symbol-library.ps1`), and the built-in recipes "Electrical layout (first draft)", "Plumbing layout (first
draft)" and "Colour presentation elevation" are seeded into `knowledge\recipes\` at start-up.

AutoCAD 2027 does not trust `%APPDATA%\Autodesk\ApplicationPlugins` by default, and the DLL is not signed. So with
`SECURELOAD` = 1 the user answers a security prompt once (or adds `...\AcadCopilot.bundle\Contents` to Trusted
Locations). With `SECURELOAD` = 2 only trusted locations load. The bridge is framework-dependent (.NET 10). It is
started with `DOTNET_ROOT` set to the runtime AutoCAD itself runs on. Override its location with
`ACADCOPILOT_BRIDGE`; relocate `%LOCALAPPDATA%\AcadCopilot` with `ACADCOPILOT_HOME`.

## Tools the AI sees

| Tool | Risk | Owner file | What |
|---|---|---|---|
| `get_drawing_info` | Read | `Tools/ToolRegistry.Inspect.cs` | file, units (LUNITS, INSUNITS, LUPREC, inches per unit), extents, current layer, layer count, entity counts by type, view, text/dim style, DIMSCALE, LTSCALE, active command, revision |
| `run_commands` | Write (+ approval for dangerous commands) | `Tools/ToolRegistry.Drafting.cs` | `{title, steps:[{command, inputs[], note}]}`, 1-30 steps typed visibly as ONE undo group; each input typed + Enter, `""` = Enter. Returns per step the prompts shown, finished / left waiting (then Esc), rejected inputs, entities created / modified / erased (handle, type, layer, geometry in drawing units and feet-inches) |
| `cancel_command` | Write | `Tools/ToolRegistry.Drafting.cs` | Esc Esc |
| `undo` | Write | `Tools/ToolRegistry.Drafting.cs` | undo the last N `run_commands` calls |
| `capture_view` | Read | `Tools/ToolRegistry.Inspect.cs` | PNG of the current view (MCP image content) + view facts |
| `list_layers`, `list_entities`, `get_entity`, `get_sysvars` | Read | `Tools/ToolRegistry.Inspect.cs` | layers; entities filtered by layer / DXF type / window, paged; one entity by handle; system variables |
| `ask_user`, `update_plan` | Read | bridge (`--panel`) | option cards and the Task Progress checklist in the panel |
| `acad_status` | Read | bridge | connection to AutoCAD |
| `recall_knowledge` | Read | bridge (`Core/Learning/KnowledgeTools.cs`) | `{query?, kinds?, limit?}` keyword search over rules, style, recipes (with steps) and lessons; empty query = overview |
| `remember_rule`, `save_recipe`, `save_lesson`, `forget` | Write (no approval) | bridge (`Core/Learning/KnowledgeTools.cs`) | write / remove items in the knowledge files (see "Learning") |
| `learn_style_from_drawing` | Write (no approval; only reads the drawing) | `Tools/ToolRegistry.Learning.cs` | reads the active drawing's layers, text, dimension styles, walls, door swings, blocks, hatches into `style.md` |
| `create_checkpoint` | Write | `Tools/ToolRegistry.Recovery.cs` | `{name}`: an AutoCAD UNDO mark plus a copy of the drawing in `checkpoints\` (see "Self-healing, checkpoints, crash recovery") |
| `list_checkpoints` | Read | `Tools/ToolRegistry.Recovery.cs` | checkpoints of the active drawing, newest first, with how each can be restored (`undo`, `file`, `undo+file`) |
| `restore_checkpoint` | Destructive (approval) | `Tools/ToolRegistry.Recovery.cs` | `{name, fromFile?}`: UNDO Back to the checkpoint's mark, verified after every Back (REDO/MREDO when it does not match); `fromFile` replaces model space with the saved copy |
| `get_resume_info` | Read | `Tools/ToolRegistry.Recovery.cs` | what an unfinished job on the active drawing finished and what remains (job journal), the interrupted call's steps, its checkpoint, what to do next |
| `list_recordings`, `read_recording` | Read | bridge (`Core/Learning/KnowledgeTools.cs`) | the user's "Learn from me" recordings in `knowledge\recordings\` (newest first; `read_recording {name?, offset?, maxCharacters?}` in parts of at most 60,000 characters, names resolved only inside that folder) |
| `pick_point` | Write (takes a turn; changes nothing) | `Tools/ToolRegistry.Interaction.cs` | `{prompt, count? 1-20, timeoutSeconds? 5-600}`: asks the USER to click points (prompt in the command line and a banner in the panel; Enter finishes, Esc cancels / fails); returns WCS points with feet-inches |
| `get_selection` | Read | `Tools/ToolRegistry.Interaction.cs` | `{which? auto/current/last, limit?}`: the user's pickfirst selection (or the last one), counts by type / layer, `EntitySummary` per object |
| `select_objects` | Write (takes a turn; changes nothing) | `Tools/ToolRegistry.Interaction.cs` | `{handles[]}`: selects + highlights objects for the user (grips), `[]` clears |
| `insert_symbol_library` | Write | `Tools/ToolRegistry.Services.cs` | imports the 20 electrical / plumbing blocks (bundle `Contents\Blocks\AcadCopilot-Services.dxf`, R12 DXF via `Database.DxfIn` + `WblockCloneObjects`) and the layers E-LITE, E-POWR, E-SWCH, E-ANNO, P-WATR, P-DRAN, P-FIXT, P-ANNO; optional own `.dwg` / `.dxf` library |
| `place_services_legend` | Write | `Tools/ToolRegistry.Services.cs` | legend table (real blocks) + "DRAFT - TO BE CHECKED BY A QUALIFIED ... ENGINEER" note |
| `fill_presentation_regions` | Write | `Tools/ToolRegistry.Services.cs` | true-colour solid / gradient / pattern hatches by internal point or boundary handle on A-ELEV-PRES-* layers, behind the linework |
| `add_presentation_backdrop` | Write | `Tools/ToolRegistry.Services.cs` | sky gradient, ground band, 45-degree shadows of projections |
| `export_view_png` | Write | `Tools/ToolRegistry.Services.cs` | PNG (default 2400 px, white paper, exact fill colours) to `Documents\AcadCopilot Exports`; result data `file`, `folder` and a preview image (the panel shows **Open exports folder**) |

`get_drawing_info` also returns `product` ("AutoCAD 2027" for plain AutoCAD, the vertical's name otherwise) and
`architectureObjects` (true only in AutoCAD Architecture / MEP).

Wire types are in `AcadCopilot.Core.Tools`: `ToolDefinition {Name, Title, Description, Risk, InputSchema}`,
`ToolResult {Success, Message, Data, CreatedIds/ModifiedIds/DeletedIds, Warnings, Errors, Revision, Images}`.
Handles are hex text in results ("2A3F"). Lengths are in drawing units with a feet-inch text.

### Command policy (`src/AcadCopilot.Core/Drafting/`, pure, unit-tested)

Every `run_commands` step is checked before anything is typed. Command names are normalised first (leading
`_ . - +` and the transparent `'` removed, acad.pgp aliases resolved).

- **Blocked**: SHELL, SH and the other external commands, APPLOAD, NETLOAD, ARX, SCRIPT, VBA\*, ACTIVEX\*, CUI\*, MENU\*,
  QUIT, EXIT, CLOSE, CLOSEALL, OPEN, NEW, QNEW, RECOVER, UNDO and U (the copilot's `undo` tool keeps the undo groups
  intact), MULTIPLE, changes to SECURELOAD / TRUSTEDPATHS. Any command or input that starts with `(` or `!` is
  refused too (AutoLISP).
- **Needs approval** (card in the panel): SAVE, QSAVE, SAVEAS, SAVEALL, WBLOCK, EXPORT\*, the ...OUT writers, PLOT,
  PUBLISH, ETRANSMIT, PURGE, LAYDEL, LAYMRG, AUDIT, ERASE with `ALL`.
- **Dialog commands** are replaced by their command-line versions (LAYER -> -LAYER, HATCH -> -HATCH, STYLE -> -STYLE
  ...). Dialog-only commands are refused, with what to use instead.

### Self-healing, checkpoints, crash recovery

- **Self-healing** (`Core/Drafting/InputRepair.cs`, pure, unit-tested; applied in `Plugin/Drafting/DraftingEngine.cs`;
  `run_commands {autoFix: false}` turns it off). Syntax only, never a guess about geometry, each fix listed as
  "Auto-fixed: ..." in the result: before typing, Unicode minus/quote marks and spaces in coordinates ("10, 20",
  "10 20" at a point prompt, `12'-6 1/2"`); after AutoCAD rejects an input and asks again, ONE retry with feet-inches
  or mm/cm/m converted to drawing units when LUNITS is not 3/4 and INSUNITS gives the scale (never when a bare number in
  the same coordinate would be ambiguous), a misspelt / bracketed / non-capital option keyword typed as its capital
  letters (only when exactly one option fits), a degree sign removed (AUNITS 0); the forgotten finishing Enter at
  finish prompts only (LINE/PLINE/SPLINE next point, `Select objects:`, `Enter text:`, -HATCH internal point,
  TRIM/EXTEND, `<Exit>` defaults, -LAYER/-LINETYPE main prompt, DIMCONTINUE/DIMBASELINE; never where Enter accepts
  another `<default>`); a command that still opens a dialog retyped as its `-` version when AutoCAD knows it. The
  plan-time LAYER -> -LAYER rewrites are reported the same way.
- **Checkpoints** (`Core/Recovery/Checkpoints.cs`, `CheckpointStore.cs`; `Plugin/Recovery/CheckpointService.cs`). A
  checkpoint is an AutoCAD `UNDO Mark` (exact restore while the drawing stays open: handles, layers, styles, paper
  space) plus a `Database.Wblock()` copy saved to `checkpoints\` (survives a crash; never the user's file). Marks are
  anonymous, so the copilot keeps its own mark stack with a fingerprint (entities by handle, layer count) per mark;
  `restore_checkpoint` types `UNDO Back` once per mark and checks the fingerprint after every Back, skips marks that U
  already used up, answers No to "This will undo everything", and on any mismatch reverses with REDO/MREDO so nothing
  is lost. `fromFile` (only on request, after a restart) erases model space and inserts the copy's model space
  (`Database.Insert`; new handles, current layouts/layers kept) inside one undo group, after a safety checkpoint of the
  current state. `run_commands` creates "before: <title>" automatically for calls of 10+ steps (`autoCheckpoint`).
- **Crash recovery** (`Core/Recovery/JobJournal*.cs`, `ResumeInfo.cs`; `Plugin/Recovery/`). Every `run_commands` call is
  journaled before typing and after each finished step (atomic writes), with the checkpoint made for it; plan tasks are
  journaled when the pipe handler forwards `update_plan` (`RecoveryHooks.PlanUpdated`). `get_resume_info` reads the
  journals of the active drawing: tasks done/remaining, the interrupted call (steps typed / not typed), its checkpoint
  and advice. `ResumeMonitor` (start-up and every opened drawing) exposes the newest unfinished job of an AutoCAD session
  that has ended as `CurrentOffer` / `OfferChanged` for a "Resume previous job" card; `Dismiss(session)` retires it, and
  a new plan on the same drawing retires older ended jobs. Journals and checkpoint copies older than 7 days are deleted
  once per session.
- **Wiring**: `PluginPipeHandler` sets `RecoveryContext.SessionId` (AsyncLocal, flows into the main-thread items) to
  the chat tab around every tool handler and resets it afterwards, also on exceptions, so each tab's calls journal
  under its own key (calls without a tab journal under one key per drawing); `UpdatePlanAsync` calls
  `RecoveryHooks.PlanUpdated`. `CopilotApp` starts `ResumeMonitor` at the end of its first-idle start-up (after the
  palette exists) and stops it at shutdown. `PaletteHost` subscribes to `ResumeMonitor.OfferChanged` (and shows a
  `CurrentOffer` made before the palette existed): the card goes into the tab bound to that drawing, else the tab shown.
  **Resume** sends `ResumeMonitor.ResumePrompt(offer)` in a tab bound to the job's drawing (a new tab when none is and
  that drawing is active; otherwise the card asks the user to switch to it first and stays open), then
  `ResumeMonitor.Dismiss`; **Dismiss** retires the journal.
- **Two journals, no overlap**: `Core/Recovery/JobJournal*.cs` is the on-disk crash-recovery journal (`journal\`);
  `Core/Panel/ConversationJournal.cs` is the in-memory record of a tab's conversation (recent messages, task list, tool
  calls) used for AI-switch handovers and the reviewer. It writes no files.
- Compile-verified only (needs AutoCAD): the engine integration, UNDO Mark/Back/REDO/MREDO, Wblock/SaveAs/Insert, the
  monitor's document events. The pure rules, catalog, store, journal and resume text are unit-tested.

### System instructions (`src/AcadCopilot.Core/Agents/CopilotInstructions.cs`)

Every CLI turn gets the same drafting instructions (Claude Code `--append-system-prompt-file`, Codex
`developer_instructions`, Gemini `GEMINI.md` in its private workspace). In short:

- **Workflow**: `get_drawing_info` first. Ask for missing facts with `ask_user`. For a new plan, show the room
  schedule (sizes and positions; the numbers must close) and wait for "Draw it". Then draw in stages (setup >
  outline > walls > openings > doors/windows > stair > text > dimensions > title), one `run_commands` call per
  stage, each followed by `capture_view` and a check. Ask before the next floor or drawing.
- **Units**: feet-inches. Empty drawings are set to LUNITS 4 / INSUNITS 1 (1 unit = 1 inch). `run_commands` keeps
  typed coordinates exact during each call (see below), so the AI only switches a rotated UCS to World. Turning
  running object snaps off (OSMODE + 16384) is a last resort when points still land wrong.
- **Drafting recipes**: layers A-WALL, A-DOOR, A-GLAZ, A-STRS, A-FURN, A-ANNO-TEXT, A-ANNO-DIMS, A-SITE, A-GRID,
  A-ELEV, A-HATCH. Plain-line walls (PLINE + OFFSET 9", 4 1/2" inner walls, BREAK/TRIM/FILLET junctions and
  openings). Doors are a leaf and an ARC, windows three lines, stairs use -ARRAY treads. Text style ARCH and
  dimension style ARCH-48 for 1/4" = 1'-0". Elevations, sections, site plan and furniture each get their own area
  and title.
- **Learning**: `recall_knowledge` before drawing; follow the learned rules and style (they override the defaults);
  `remember_rule` on every correction or stated preference, `save_recipe` after a reusable stage worked,
  `save_lesson` after fixing a mistake, offer "Learn my style from this drawing?" on the user's own finished drawing.
- **Honesty**: no code-compliance or structural claims. Approval-gated commands only when asked.

`CopilotInstructions.Default` is written for **plain AutoCAD** and never mentions AutoCAD Architecture objects. The
backend composes each turn's text with `CopilotInstructions.Compose(product, learned)`
(`CopilotBackend.BuildInstructions`): Default, plus a one-line note about WALLADD / DOORADD / WINDOWADD only when the
host product (`AcadSessionInfo.Product`, from `AcadHost.ProductName` / `AcadCopilot.Core.AcadProduct`) is AutoCAD
Architecture or MEP, plus the "WHAT YOU HAVE LEARNED" digest (below), read fresh for every turn.

The TOOLS section names the newer tools in one line each (`pick_point` / `get_selection` / `select_objects` and the
`[point ...]` / `[selected in AutoCAD: ...]` message tokens; `create_checkpoint` / `restore_checkpoint` /
`get_resume_info`; the services and presentation tools); "Auto-fixed" notes, the IF POINTS LAND WRONG advice and the
details live in the tool descriptions. SAFETY says electrical / plumbing layouts are first drafts with the DRAFT note.
`DraftingGuides` (returned by `recall_knowledge`) has guides for the dog-legged stair, elevation, section, site plan,
furniture, hatching, electrical layout, plumbing layout, presentation elevation and Vastu. Turn settings from the panel
("Ask before drawing" off, tutor mode on) are added as a TURN SETTINGS block; the reviewer gets its own read-only
instructions (`CopilotInstructions.Reviewer`).

Default is about 15,415 characters; keep it under 15,500 (test). The digest is at most 4,000 and the composed text
at most 20,000 (`MaxComposedLength`): Codex receives it on its command line, which Windows limits to 32,767
characters after quoting (a test builds the worst case).

### Learning (`src/AcadCopilot.Core/Learning/`, pure, unit-tested)

No model can be retrained, so the copilot learns into a local knowledge base that every turn reads and the AI writes
to, `%LOCALAPPDATA%\AcadCopilot\knowledge\` (`AppPaths.Knowledge`; created at plug-in start-up), plain Markdown the
user can edit:

| File | Written by | Content |
|---|---|---|
| `rules.md` | `remember_rule {text, category}` | preferences / corrections under `## <category>` headings, one `- rule (yyyy-MM-dd)` per line, newest first |
| `style.md` | `learn_style_from_drawing` (and the user) | learned sections (Units, Layers, Text, Dimensions, Walls, Doors/windows/stairs, Blocks and furniture, Hatches), `Learned from`, and the user's `My notes` (never overwritten) |
| `lessons.md` | `save_lesson {mistake, fix}` | `- mistake \| Fix: fix (date)`, newest first |
| `recipes\<slug>.md` | `save_recipe {name, description, steps, notes}` | title, description, ` ```json ` block with the run_commands steps that worked |

- `KnowledgeStore`: dedupe (same text = refreshed; same subject words with other numbers = replaced, newest wins),
  caps (60 rules / 3,000 characters, 40 lessons, 60 recipes of at most 12,000 characters, 2,500 characters of
  learned style; the oldest *dated* items go first, undated lines the user wrote are never dropped), atomic writes
  (temp file + rename) under a named mutex, because every chat tab runs its own bridge.
- `KnowledgeRecall`: keyword scoring (title 3, text 2, shared 4-letter prefix 1, crude singulars and a few drafting
  synonyms such as bathroom/WC -> toilet), newest first on ties; recipes come back with their steps.
- `KnowledgeDigest`: the "WHAT YOU HAVE LEARNED" section: all rules, the style, the recipe names and the lesson
  mistakes (details through `recall_knowledge`), at most 4,000 characters.
- `StyleAnalyzer`: turns a `StyleSample` (read by the plug-in) into style.md lines: layers by use with colour /
  linetype / lineweight, text styles and most used heights, the used dimension styles' key variables, wall
  thicknesses (each segment of 2'+ to its nearest overlapping parallel segment 2"-24" away, rounded to 1/4"),
  polyline widths, 90-degree door-swing radii, block names grouped as doors / windows / furniture, hatch patterns.
- Where the tools run: `remember_rule`, `save_recipe`, `save_lesson`, `recall_knowledge`, `forget` only touch files,
  so the **bridge** answers them itself (`KnowledgeTools.Execute`; names reserved in `BridgeTools.ReservedNames`).
  They work while AutoCAD is busy or closed, never wait in the drawing queue (the plug-in queues every non-Read
  tool and activates the tab's drawing first) and also serve the user's own MCP clients. `learn_style_from_drawing`
  needs the drawing, so the **plug-in** implements it (`Tools/ToolRegistry.Learning.cs`: one read transaction on the
  main thread through the dispatcher, then `StyleAnalyzer` + `KnowledgeStore.MergeStyle` off the main thread).

### Learn from me (teach by drawing)

The panel's **Learning** bar (above the chat of every tab; `ViewModels/LearningViewModel.cs`, one recorder for the
whole panel, created in `CopilotApp` and passed through `PaletteHost` / `CopilotPaneOptions.Recorder`):
**Start learning** -> red "Learning from you · 03:12 · 47 actions" with **Pause** / **Resume**, **Stop & learn** and
**Discard** (asks first). Only the user's click starts it; nothing is attached to AutoCAD while it is off.

| Piece | Where | What |
|---|---|---|
| Recorder | `Plugin/Learning/ActionRecorder.cs` (AutoCAD, main thread) | While a recording exists: `Document.CommandWillStart/CommandEnded/CommandCancelled/CommandFailed` (command name), `Database.ObjectAppended/ObjectModified/ObjectErased` (entities per command, described with `Inspect/EntitySummary` + `RecordingText.DescribeObject`), `Application.SystemVariableChanged` (CLAYER), `DocumentCollection.DocumentCreated/DocumentActivated/DocumentToBeDestroyed`. The typed answers are the command-line lines new between the command's start and end (`AcadInput.History` = `Internal.Utils.GetLastCommandLines` put into time order by `CommandLineText.Chronological`, then `CommandLineText.NewLines`). Handlers never throw. |
| Copilot flag | `Core/Learning/CopilotActivity.cs`, `Plugin/Ipc/PluginPipeHandler.cs` | every tool call that takes a turn in AutoCAD (typing, undo, Esc, drawing switch) runs inside `CopilotActivity.Shared.Begin()`; commands and layer changes that start while it is active (or within 1 s after) are not recorded. |
| Model | `Core/Learning/UserRecording.cs` (pure) | actions in time order (commands with lines and objects, layer changes, drawing switches, pause markers), pause without data loss, elapsed time without pauses, caps: 2,000 actions in detail (the rest counted by name), 40 lines and 30 described objects per command; ZOOM / PAN / REGEN only counted; COPILOT commands ignored. |
| Steps and file | `Core/Learning/RecordingSteps.cs`, `RecordingMarkdown.cs`, `RecordingLibrary.cs` | steps break at a layer change (the -LAYER command moves into the new step), a drawing switch, a pause, 90 s idle or 12 commands. Markdown: summary (units, commands used, objects by layer) and per step each command with `prompt` `answer` pairs (typed distances also in feet-inches), the objects created / modified / erased. Saved atomically as `knowledge\recordings\<yyyy-MM-dd_HHmm>_<drawing>.md` (never overwritten). |
| Stop & learn | `ActionRecorder.StopAndSaveAsync`, `CopilotPanelViewModel` | saves the file, runs `StyleAnalyzer` on the user's new objects (`ToolRegistry.ReadStyleSample` with an id list) and merges into `style.md`, then the tab shown sends "Learn from my recording <file>: ..." (`ChatViewModel.SendOrDraft`; a busy tab keeps it in its message box). The instructions' LEARNING section tells the AI to `read_recording` it, `save_recipe` / `remember_rule` and summarise. **Discard** throws it away (nothing was written). |

### Pipe protocol (bridge <-> plug-in, `AcadCopilot.Core.Ipc`)

Newline-delimited JSON-RPC 2.0 over `\\.\pipe\AcadCopilot-<acadPid>` (`PipeOptions.CurrentUserOnly`). Discovery:
`%LOCALAPPDATA%\AcadCopilot\instances\<pid>.json` = `{pid, pipe, token, acadVersion, documentTitle, startedAt}`.

| Method | Direction | Params -> Result |
|---|---|---|
| `auth` | client -> server, must be first | `{token, clientName, clientVersion, sessionId?}` -> `{serverName, serverVersion, pid, acadVersion, documentTitle}` |
| `tools.list` | client -> server | - -> `{tools: ToolDefinition[]}` |
| `tools.call` | client -> server | `{name, arguments, callId, sessionId?}` -> `ToolResult` (tool failures are results, not RPC errors) |
| `ui.askUser` | client -> server | `QuestionRequest {questions}` + `sessionId?` -> `QuestionResponse {answers, cancelled}` |
| `ui.updatePlan` | client -> server | `PlanUpdate {title?, tasks:[{title, status}]}` + `sessionId?` -> `{ok:true}` |
| `$/cancelRequest` | notification, either way | `{id}` cancels an in-flight request |
| `notifications.toolsChanged` | server -> client | tool list changed |
| `notifications.documentChanged` | server -> client | `{documentTitle, revision}` |

Error codes (`RpcErrorCodes`): -32001 unauthorized, -32003 UI unavailable, -32004 AutoCAD busy, -32800 cancelled.
The token is compared in constant time. `sessionId` (`RpcSessions`: 1-64 letters, digits, `-`, `_`) names the panel chat
tab a bridge works for; a request without one uses the connection's id from `auth`; none at all = a client outside the
panel (active tab, active drawing).

### MCP bridge (`AcadCopilotMcp.exe`)

```
AcadCopilotMcp [--pid <acadPid>] [--panel] [--session <id>] [--instances-dir <dir>] [--connect-timeout <s>] [--log <file>|none]
```

- Finds AutoCAD through the discovery files (`--pid`, else the newest live instance), connects lazily, reconnects
  after AutoCAD restarts, and sends `notifications/tools/list_changed` when the tool list changes.
- Exposes every plug-in tool plus `acad_status`; with `--panel` (the panel passes it) also `ask_user` and
  `update_plan`. `--session <id>` (the panel passes the tab's id) is sent with auth and every call. Images in a `ToolResult` (`capture_view`) are sent as MCP image content after the JSON text.
- stdout carries only MCP; logs go to `logs\bridge.log`. Closing stdin ends the process.

### CLI runners (`AcadCopilot.Core.Agents`)

All runners start the CLI without a window via `ProcessStartInfo.ArgumentList` (never through a shell; npm `.cmd`
shims are unwrapped), write the prompt to **stdin**, configure the AutoCAD MCP server **per invocation**, and never
write the user's CLI configuration or logins. Stop kills the whole process tree (job object with kill-on-close, so
the CLIs also die if AutoCAD exits).

| CLI | Command line (prompt on stdin) | Status / sign-in |
|---|---|---|
| Claude Code | `claude -p --output-format stream-json --verbose --include-partial-messages --mcp-config <turn>\mcp.json --strict-mcp-config --allowedTools mcp__acad__* --permission-mode dontAsk --permission-prompts none --tools "" --setting-sources project,local --disable-slash-commands --append-system-prompt-file <turn>\instructions.md [--model m] [--resume id] [--add-dir <attachments>]`; env `MCP_TOOL_TIMEOUT=900000`, `CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS=0`, `ENABLE_TOOL_SEARCH=false`, `ENABLE_CLAUDEAI_MCP_SERVERS=false` | `claude auth status --json`; sign-in `claude auth login` |
| Codex CLI | `codex exec [resume] --json --skip-git-repo-check --ignore-user-config -c approval_policy="never" -c sandbox_mode="read-only" -c features.shell_tool=false -c features.apps=false -c mcp_servers.acad.command=... .args=[...] .env={...} .tool_timeout_sec=900 .default_tools_approval_mode="approve" -c developer_instructions=... [-m model] [<thread id>] -` | `codex login status`; sign-in `codex login` |
| Gemini CLI | `gemini -o stream-json -e none --allowed-mcp-server-names acad --approval-mode default --policy <ws>\.gemini\copilot-policy.toml [-m m] [--resume id]` in a private workspace whose `.gemini\settings.json` declares the trusted bridge (timeout 900000 ms) and whose `GEMINI.md` carries the instructions; env `GEMINI_CLI_TRUST_WORKSPACE=true`, `GEMINI_CLI_NO_RELAUNCH=true` | read-only probe of `~\.gemini\settings.json` + credentials; sign-in `gemini` (then `/auth`) |

### Panel and plug-in

- `CopilotApp` (IExtensionApplication) starts the log, the `AcadDispatcher` (AutoCAD main thread), the
  `RevisionTracker`, the `ToolRegistry`, the `CopilotBackend` with the three runners, the `PipeServer` + discovery
  file, the palette (`PaletteSet` "AI Copilot", docked right) and the ribbon tab "AI Copilot". Start-up failures are
  logged and reported on the command line; AutoCAD keeps running.
- `PluginPipeHandler`: unknown tool -> error listing the tools; JSON-schema validation; approval card for
  `Destructive` tools (`run_commands` asks itself, per dangerous command, through `ToolContext.ApproveAsync`); writes
  serialised; `ToolException` -> failed result; busy -> -32004; revision on every result.
- **Web research** switch: globe **Web** button in the composer row and Settings > AI BEHAVIOUR, both bound to
  `ChatViewModel.WebResearch` (`ICopilotAssist.Features.WebResearch`); see the Status row for what each CLI gets.
- Activity rows (`ViewModels/ActivityItems.cs`): web tools read "Searching the web: <query>" / "Reading <site>"
  (`ToolActivityText.WebTitle`); per `run_commands` command its outcome, problem and "Auto-fixed: ..."
  line; the checkpoint made before the call; the `capture_view` / `export_view_png` picture; for `export_view_png` an
  **Open exports folder** link (`explorer /select,<file>`). Quick prompts (`QuickPromptsViewModel.BuiltIns`): 30x40 G+1
  plan, Add dimensions, Review this plan, Learn my style, Plot to PDF, Electrical layout (draft), Colour elevation + PNG.
- Watch / Fast: `AcadCopilot.Core.Drafting.DraftingPace` (watch = 450 ms between typed inputs, 900 ms before each
  command; fast = no pauses), bound to the panel switch; a change applies to the next input.
- `run_commands` (definition and LLM description in `Core/Drafting/DraftingTools.cs`, request limits in
  `RunCommandsRequest.cs`: 30 steps, 60 inputs per step, 300 characters per input) types each command and input with
  `SendStringToExecute`, waits until AutoCAD has taken it, presses Esc when a command still waits after its last
  input, and does not type inputs left over after a command ended.
- **Chat tabs** (see below): `CopilotPanelViewModel` holds one `ChatViewModel` per tab, each over its own
  `CopilotSession`.
- The view model (`ChatViewModel`) implements `ICopilotUi`: option cards, Task Progress, approval cards (Approve /
  Reject / Always allow for non-destructive tools; settings: "Never ask (auto-approve)"). Esc in the panel = Stop.

### Chat tabs (parallel sessions)

| Piece | Where | What |
|---|---|---|
| Sessions | `Core/Panel/CopilotSession.cs`, `CopilotBackend.cs` (`ICopilotHub`) | one `CopilotSession` per tab: its own CLI session ids (resume), provider + model, "Always" approvals, UI (`ICopilotUi`), Stop (`TurnToken`), drawing (`DrawingRef`). The backend keeps runners, settings (last provider / model = default for new tabs), sign-in, `ActiveSession` (the tab shown), the running limit (`MaxRunningSessions` = 4; a fifth working tab gets a clear `TurnFailed`, the text stays in its composer) and `DrawingQueue`. Its old single-chat members act on `DefaultSession`. |
| Bridge per tab | `CopilotBackend.CreateLaunch` | every turn starts the bridge with `--pid <acad> --panel --session <tab id>` (Gemini therefore gets one private workspace per tab). |
| Turn queue | `Core/Panel/AcadTurnQueue.cs` | FIFO, one at a time; `StatusOf(tab)` = Drawing / Waiting (position, "2nd in line"); cancelling a waiting entry removes it. Sessions mirror it in `AcadTurn` (StateChanged). |
| Routing in AutoCAD | `Plugin/Ipc/PluginPipeHandler.cs` | resolves the tab (`ResolveSession`; unknown id = closed tab, refused), links the call to the tab's `TurnToken`, routes cards / plan / approvals / activity rows to that tab. Typing tools (risk other than Read: `run_commands`, `undo`, `cancel_command`) enter the queue; when their turn comes the tab's drawing is activated first (`DrawingDirectory.ActivateAsync`: `DocumentCollection.MdiActiveDocument`, waits for idle) and the result message starts with "AutoCAD switched from 'A' to 'B' ...". Read tools run at once when the tab's drawing is active, otherwise they take a turn too. |
| Drawing ids | `Plugin/Acad/DrawingDirectory.cs` | id kept in `Document.UserData` for the document's lifetime; `DocumentToBeDestroyed` marks tabs "(closed)"; `DocumentBecameCurrent` refreshes titles. `AcadDispatcher.ExpectedDrawingId` (AsyncLocal, flows into main-thread items) makes every item of a tab's call refuse to act on another drawing (`DrawingNotActiveException`; a read then retries through the queue). |
| Panel | `Plugin/ViewModels/CopilotPanelViewModel.cs`, `Views/CopilotPane.xaml` | tab strip (title = first message, drawing chip, status Working / Drawing... / 2nd in line / Needs your answer), **+**, **x** (asks "Stop and close / Keep" for a working tab; the last tab is replaced), "2 of 4 chats working". Sign-in states and the approval mode are kept in step across tabs; Watch / Fast is global. The pane's DataContext is the selected tab. Tabs live as long as the palette, i.e. the AutoCAD session. |

## Risks for the first run inside AutoCAD (most likely first)

1. **Typing engine details.** Whether every command accepts typed input the way the instructions assume (TEXT
   ending, -LINETYPE file prompt, -ARRAY, BREAK on exploded lines, ARC with Angle). The result of each step shows the
   prompts, so the AI can adapt, but expect some first-run fixes in `CopilotInstructions.cs`.
2. **Typed-input semantics.** `run_commands` types with `Document.SendStringToExecute`. For the duration of a call
   it sets CMDDIA / FILEDIA / ATTDIA 0, TEXTED 1, TEXTEVAL 0, DYNPICOORDS 1, OSNAPCOORD 1, NOMUTT 0, PICKAUTO 0
   and PICKADD 2, and restores them afterwards (`Plugin/Drafting/SysvarScope.cs`, with a crash backup in
   `%LOCALAPPDATA%\AcadCopilot\drafting-sysvars.json`). Whether that keeps every typed point exact with the user's
   object snaps and Dynamic Input on is not verified inside AutoCAD yet.
3. **Loading**: the security prompt for the unsigned DLL from `%APPDATA%`, or `SECURELOAD` = 2. See the user guide.
4. **Gemini not signed in** on this PC (API-key mode without a key): Gemini turns are unverified until then.
5. **Switching drawings** for a tab's turn (`MdiActiveDocument` from the application context) is compile-verified
   only; if AutoCAD refuses (document activation disabled, a modal state) the tool fails with a message asking the user
   to switch.
6. **Token use**: screenshots after every stage and long command lists consume the plan's limits faster than plain
   chat. Watch mode makes a full floor plan take minutes.
