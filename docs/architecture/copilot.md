# CAD Copilot — architecture and protocol

CAD Copilot is a desktop AI assistant (Electron) that docks beside a CAD application and drives it
through structured tools, in the style of MecAgent: a narrow chat window on the right of the CAD
window that asks clarifying questions, shows a task plan with live progress, narrates its CAD
activity and performs the work in the real CAD program.

```
 ┌──────────────────────────────┐  named pipe + NDJSON JSON-RPC  ┌───────────────────────────────┐
 │ apps/copilot  (Electron)     │ ─────────────────────────────▶ │ CAD connector (one per CAD)   │
 │  main: CopilotSession        │ ◀───────────────────────────── │  • integrations/autocad-plugin│
 │   (packages/copilot-agent)   │      notifications             │    (C# .NET 8 AutoCAD plugin) │
 │   → Claude API (Anthropic)   │                                │  • CAD Studio main process    │
 │  renderer: chat UI (React)   │                                │    (apps/desktop bridge)      │
 └──────────────────────────────┘                                └───────────────────────────────┘
          shared contract: packages/cad-protocol (tool catalog, messages, discovery)
```

Rules (from the product specs):

- The copilot never simulates CAD work. A change exists only when the connector's tool result says
  so (`success: true` with created/modified ids) — and the UI only reports confirmed results.
- The LLM can only call catalogued tools with strictly validated arguments. No arbitrary code,
  scripts, LISP or shell commands are ever sent to a CAD program.
- Destructive tools always need user approval; other write tools follow the approval setting.
- Every tool call is one undo step in the CAD program and is logged.
- `expectedRevision` protects against stale plans: if the drawing changed since the agent inspected
  it, the connector rejects the call and the agent re-inspects.
- No building-code compliance is claimed.

## Transport

- **Windows named pipe** per connector: `\\.\pipe\cadcopilot-<cad>-<pid>` (local machine only, no
  firewall prompts, current-user ACL). Framing: **newline-delimited JSON** (one JSON-RPC 2.0
  message per line, UTF-8, `\n` terminated; embedded newlines are escaped by JSON).
- **Discovery:** every running connector writes `%APPDATA%\CadCopilot\connectors\<cad>-<pid>.json`
  (`ConnectorAdvert`) at start and deletes it on exit. The copilot lists that folder, ignores adverts
  whose `pid` is no longer running, and connects to the one the user picks (or the only one).
- **Authentication:** the advert holds a random 256-bit hex `token`. The first request on a
  connection must be `auth`; anything else gets error `-32001` and the connection is closed.

## Methods (client → connector)

| Method | Params | Result |
|---|---|---|
| `auth` | `{ token, client: { name, version } }` | `{ ok: true }` |
| `hello` | `{}` | `HelloResult` |
| `tools.call` | `{ requestId, operationId, name, arguments, expectedRevision? , source: 'ai' \| 'user', instruction? }` | `ToolResult` |
| `window.getBounds` | `{}` | `WindowBounds \| null` |
| `window.setBounds` | `{ bounds: WindowBounds }` | `{ ok: true }` |
| `window.focus` | `{}` | `{ ok: true }` |

`WindowBounds` are the outer bounds of the CAD main window in **physical screen pixels** (what
Win32 `GetWindowRect` reports). Electron code converts with `screen.screenToDipRect` /
`screen.dipToScreenRect`. `window.setBounds` must restore/unmaximise the window first.

Notifications (connector → client, no `id`): `document.changed { documentId, revision }`,
`document.activated { document: DocumentInfo | null }`, `selection.changed { ids }`,
`connector.closing {}`.

**Failures.** Tool-level failures are not JSON-RPC errors: `tools.call` resolves with a
`ToolResult` whose `success` is false and whose `errors` say why (invalid arguments, stale
revision, tool not supported by this CAD, no drawing open, the CAD rejected the operation). The
model reads those and corrects itself. JSON-RPC errors (`BridgeError`) are reserved for
protocol-level failures: `-32001` unauthorised, `-32006` connector busy / timed out, `-32603`
internal error, plus the standard parse/request/method/params codes. (`-32002`…`-32005`, `-32007`,
`-32008` are reserved names for connectors that cannot build a ToolResult.)

**Stale plans.** The copilot remembers the drawing revision the model last saw (every ToolResult
carries `revision`) and sends it as `expectedRevision` with write and destructive calls. If the
user edited the drawing in between, the connector refuses with a failed ToolResult and the model
re-inspects before acting.

## Tool catalog

Defined once in `packages/cad-protocol` (zod schemas → JSON Schema for Claude and for the C#
plugin's validation). Lengths in tool arguments are numbers in the call's `unit` (`mm`, `cm`, `m`,
`in`, `ft`; default = the drawing's unit reported by `get_drawing_info`). Points are `{x, y}`.
Angles are degrees, CCW from +X. Each connector declares which tools it supports in
`HelloResult.supportedTools`; the copilot exposes only those to the model.

| Category | Tools (risk: R read, W write, D destructive) |
|---|---|
| Inspection | `get_drawing_info` R, `list_layers` R, `inspect_entities` R, `get_selected_objects` R, `find_objects` R, `inspect_room` R, `get_drawing_revision` R |
| Geometry | `create_line` W, `create_polyline` W, `create_rectangle` W, `create_circle` W, `create_arc` W, `create_text` W, `create_entities` W (batch: many lines/polylines/rectangles/circles/arcs/texts in ONE undo step) |
| Architecture | `create_wall` W, `create_room` W, `create_door` W, `create_window` W, `create_staircase` W, `create_column` W, `create_plot_boundary` W, `create_floor_plan` W |
| Modification | `move_entities` W, `copy_entities` W, `rotate_entities` W, `scale_entities` W, `mirror_entities` W, `update_entity_properties` W, `move_room` W, `resize_room` W, `delete_entities` D |
| Annotation | `add_dimension` W, `add_room_labels` W |
| Layers | `create_layer` W, `set_current_layer` W, `set_layer_state` W |
| Validation | `validate_drawing` R (overlaps, open room boundaries, openings outside walls, zero-length geometry) |
| View | `zoom_extents` R, `zoom_to_entities` R, `highlight_entities` R, `clear_highlight` R |
| Document | `undo_last_operation` W, `save_document` D |

Copilot-only (never sent to a connector): `ask_user` (clarifying multiple-choice questions, answered
in the UI) and `update_plan` (task list with statuses shown as "Task Progress").

## Connectors

**CAD Studio** (`apps/desktop`). The drawing lives in the renderer, so the connector is split:

- main process (`src/main/copilot-connector.ts`): `startConnectorServer({ cad: 'cadstudio', ... })`
  from `@cadstudio/cad-protocol/node`; forwards `tools.call` to the renderer over IPC
  (`copilot:tool-call` / `copilot:tool-result`, 60 s timeout → error `-32006`), answers `hello`
  from the state the renderer publishes, implements the window methods with `BrowserWindow`.
- preload: `window.cadstudio.copilot` (`CopilotHostBridge` in `src/shared/bridge.ts`).
- renderer (`src/renderer/copilot/`): `tool-executor.ts` maps catalog tools to document
  transactions with `source: 'ai'` (one transaction = one undo step, recorded in the operation
  history with the user's instruction); `install.ts` wires it to the bridge and publishes
  document/selection changes.

CAD Studio currently supports the inspection, geometry (line, polyline, rectangle, circle,
`create_entities`), modification, layer, view, validation and document tools. Arc/text,
architecture and annotation tools arrive with CAD Studio phases 2 and 3; until then they are not in
`supportedTools`, so the model never sees them.

**AutoCAD** (`integrations/autocad-plugin`, planned): a C# .NET 8 plugin for AutoCAD 2025/2026
hosting the same pipe protocol inside `acad.exe`, executing tools on AutoCAD's main thread with
document locking and one undo group per call. Not built yet; AutoCAD LT cannot load plugins.

## Copilot app (`apps/copilot`)

- main process: connector discovery (polls the adverts folder), `CadConnection`, the
  `CopilotSession`, docking, settings (JSON in `userData`), the API key (Electron `safeStorage`),
  attachments (native picker, size limits).
- **Docking:** on connect (when `autoDock`), the copilot reads the CAD window bounds, takes the work
  area of the display that window is on, resizes the CAD window to the left part and places its
  own window in a full-height column on the right (`panelWidth`, default 460 DIP). Undocking or
  quitting restores the CAD window's previous bounds. Resizing the copilot panel re-fits the CAD.
- renderer: a pure view of `window.copilot` (`src/shared/bridge.ts`): the main process sends
  `TimelineEntry` items; a pure reducer turns them into the transcript (messages, question cards,
  "CAD activity" rows, "Task Progress", approval cards).
- **Scripted test model:** with `CADCOPILOT_E2E=1` and `CADCOPILOT_SCRIPT=<json file>` the session
  uses `createScriptedTransport` instead of Claude (no network). The UI then shows a "TEST MODEL"
  badge. This exists for end-to-end tests and offline demos only and is never the default.

## Session events (copilot main → renderer)

`CopilotSession` (packages/copilot-agent) emits: `message.delta`, `message.done`, `tool.started`,
`tool.finished`, `question.asked` (awaits `answerQuestion`), `approval.requested` (awaits
`resolveApproval`), `plan.updated`, `turn.finished`, `error`. `stop()` aborts the stream and any
pending tool call.

## Model

Claude via the official `@anthropic-ai/sdk`: default model `claude-opus-5-5`, user-selectable
(`claude-fable-5-2`, `claude-sonnet-5-5`, `claude-haiku-5-5`), effort selector (low → max, default
high), streaming with `finalMessage()`, strict tool schemas, server-side refusal fallback
(`server-side-fallback-2026-07-01`, `fallbacks: "default"`), prompt caching of the stable system
prompt + tool list. The API key is stored with Electron `safeStorage`, never in the renderer.
