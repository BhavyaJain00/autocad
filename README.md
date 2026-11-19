# CAD Studio + CAD Copilot

Two Windows desktop apps in one repository:

- **CAD Studio** (`apps/desktop`): a standalone **2D architectural CAD application**. It runs on its
  own: no AutoCAD or other CAD product is needed to draw, edit, save or reopen drawings. The
  geometry engine, document model, renderer and file format all live in this repository.
- **CAD Copilot** (`apps/copilot`): an AI assistant (Claude) in a narrow window that docks beside a
  CAD application and drives it through structured, validated tools: it asks clarifying questions,
  shows a task plan with live progress, lists every CAD change it makes and asks before destructive
  ones. It drives CAD Studio today; an AutoCAD connector is being built separately.

See [Status](#status) for exactly what has been verified.

## Requirements

- Windows 10 or 11 (x64)
- Node.js 22.12 or newer (developed with Node 24.11) and npm 10+
- For the copilot: an Anthropic API key ([console.anthropic.com](https://console.anthropic.com/))

## Getting started

```powershell
npm install
# Electron 45 downloads its binary on first launch. To fetch it up front:
node node_modules/electron/install.js
```

### Run CAD Studio

```powershell
npm run dev            # development: Vite hot reload + Electron (CAD Studio window opens)
```

Or the production build: `npm run build:studio`, then `npm start`.

### Run CAD Copilot next to it

1. Build the copilot once: `npm run build:copilot` (or `npm run build` for both apps).
2. In CAD Studio click **AI Copilot** (top right of the toolbar), or type `COPILOT` (alias `AI`) on
   the command line. The copilot starts, connects to this CAD Studio and docks in a column on the
   right; CAD Studio takes the rest of the screen. The status bar shows **AI connected**.
3. First time only: paste your **Anthropic API key** into the card the copilot shows (or open the
   copilot's **Settings** (gear icon) > API key) and click **Save**. The key is checked with
   Anthropic, then stored encrypted with Windows' credential encryption (Electron `safeStorage`)
   and never reaches the window's web page. `ANTHROPIC_API_KEY` in the environment also works.
4. Type what you want, for example *Draw a 12 x 14 ft room*. Answer its questions by clicking,
   approve or reject deletions, and press **Stop** to cancel a running request. Every AI change is
   one undo step in CAD Studio (`Ctrl+Z`), recorded with your instruction.

The copilot can also be started on its own: `npm run dev:copilot` (development) or
`npm run start:copilot` (built app). It lists running CAD applications, connects automatically when
exactly one is running, or lets you pick one from the connection chip in its header. Model, effort,
approval mode, auto-connect and auto-dock are in its Settings.

## Tests

```powershell
npm run typecheck  # TypeScript (all workspaces)
npm test           # unit tests (Vitest)
npm run test:e2e   # builds both apps, then runs every Playwright spec of both (windows open on screen)
```

Run Vitest from PowerShell or cmd. (From Git Bash every suite fails at its first `describe` with
"Cannot read properties of undefined (reading 'config')" because of the drive-letter case of the
working directory.)

## Using CAD Studio

| Action | How |
|--------|-----|
| Draw | Tool palette on the left, the **Draw** menu, or type a command: `L` line, `PL` polyline, `REC` rectangle, `C` circle |
| Exact input | Type into the command line while a tool runs: `x,y` absolute, `@dx,dy` relative, `@distance<angle` polar, or just a distance to go that far in the cursor direction |
| Lengths | Any unit, mixed with the drawing unit: `12`, `12.5`, `12'6"`, `12'-6 1/2"`, `3.5m`, `350mm`. A bare number is in the drawing unit (feet in an architectural drawing) |
| Select | Click; Shift+click toggles; drag left-to-right for a window, right-to-left for a crossing selection |
| Edit | Drag grips of selected objects; drag selected objects to move; `M` move; `Delete` erase; edit values in the Properties panel |
| View | Mouse wheel zooms at the cursor, middle-drag pans, double middle-click zooms to extents |
| Toggles | `F3` object snap, `F7` grid, `F8` ortho, `F9` grid snap (also in the status bar) |
| Undo/redo | `Ctrl+Z` / `Ctrl+Y` |
| Files | `Ctrl+N` new (choose units), `Ctrl+O` open, `Ctrl+S` save, `Ctrl+Shift+S` save as. Native format: `.cadstudio` |
| AI | **AI Copilot** button, `COPILOT` / `AI` command |

Press `F1` in the app for the full command and shortcut list.

## Repository layout

```
apps/desktop/          CAD Studio (Electron): main process, preload bridge, React UI, editor engine
  src/main/            window, native dialogs, atomic file IO, close guard, copilot connector host
  src/renderer/editor/ editor engine: viewport, snapping, canvas rendering, tools, commands
  src/renderer/copilot/  executes copilot tools as undoable document transactions
  e2e/                 Playwright tests against the real app
apps/copilot/          CAD Copilot (Electron): connector discovery, docking, settings, chat UI
  e2e/                 Playwright tests: smoke, fake CAD, and the joint test with real CAD Studio
packages/geometry/     pure computational geometry
packages/units/        unit conversion, input parsing, display formatting
packages/document/     document model: schemas, transactions, undo/redo, spatial index, file format
packages/cad-protocol/ copilot <-> CAD protocol: tool catalog (zod), named-pipe JSON-RPC, discovery
packages/copilot-agent/  the Claude tool-use loop (official Anthropic SDK) and the scripted test model
docs/architecture/     architecture decisions and module contracts
samples/               drawings produced by the verification tests
```

Architecture: [docs/architecture/README.md](docs/architecture/README.md), module APIs:
[contracts.md](docs/architecture/contracts.md), copilot protocol:
[copilot.md](docs/architecture/copilot.md).

## Status

Last full verification: 13 Nov 2026, on Windows 10 at 100 % display scaling.

**Verified**

- `npm run typecheck`: clean for every workspace.
- `npm test`: 4752 unit tests in 109 files pass (geometry, units, document model, CAD Studio editor,
  tools, rendering, panels, dialogs, main process and copilot executor; cad-protocol; copilot-agent;
  the copilot's main process and chat UI).
- `npm run build`: both apps build.
- End-to-end tests (Playwright) drive the real built Electron apps with real mouse and keyboard
  input:
  - `apps/desktop/e2e/smoke.spec.ts` (12) and `integration.spec.ts` (18): CAD Studio on its own:
    drawing (rectangle, line, polyline, circle; typed coordinates in feet and inches), click,
    window and crossing selection, grip editing, delete, undo/redo, wheel zoom, pan, status-bar
    toggles, layers, the properties panel, a Save As / New / Open round trip, the 900 px layout,
    the unsaved-changes guard, and the copilot connector over its real named pipe.
  - `apps/copilot/e2e/smoke.spec.ts` (6) and `fake-cad.spec.ts` (9): CAD Copilot against a fake CAD:
    bridge contract, settings, connecting, docking, questions, plan, approvals, stale revisions,
    Stop, reconnecting, corrupt settings, dead adverts, two CADs at once, no CAD / no API key.
  - `apps/copilot/e2e/integration.spec.ts` (8): **CAD Copilot driving the real CAD Studio**:
    connecting and docking the two real windows (and undocking); a conversation that asks
    questions, shows a 3-step plan and draws a 12 x 14 ft room (exactly 3657.6 x 4267.2 mm) with a
    circle on a new layer, recorded as AI operations with the user's instruction and undone with
    one `Ctrl+Z`; Reject / Approve of a delete; a hand edit in CAD Studio making the AI's next write
    fail until it re-inspects; Stop; closing and relaunching CAD Studio; the **AI Copilot** button
    starting, connecting and docking the copilot; CAD Studio dying in the middle of a tool call.
- `npm run dev` and `npm run dev:copilot` each open their window; with both running the copilot
  connects to CAD Studio and docks beside it. `npm start` / `npm run start:copilot` open the built
  apps (also when `ELECTRON_RUN_AS_NODE` is set in the terminal).

**Not verified**

- **Live Claude calls.** All copilot tests use the scripted test model (`CADCOPILOT_E2E=1` +
  `CADCOPILOT_SCRIPT`); the Claude request format is covered only by unit tests against a stubbed
  client and a local fake server. How well a real model plans and draws has not been measured.
- **AutoCAD.** The AutoCAD connector (`integrations/autocad-plugin`) is being built separately and
  has never run inside AutoCAD.
- Docking on several monitors or with display scaling other than 100 %, high-DPI canvas sizing.

**Known issues**

- In an architectural (feet and inches) drawing a bare number means feet (`500` is 500'), as
  designed; AutoCAD reads it as inches.
- Grips are drawn on selected objects on locked layers, although they cannot be edited there.
- Starting a second CAD Studio with the same Windows profile prints harmless Chromium cache
  warnings (CAD Studio allows several instances; CAD Copilot allows one per profile).
- CAD Studio's renderer is one ~640 kB chunk (Vite prints a size warning only).
- With the TEST MODEL badge (tests only) the copilot header hides the drawing name at the default
  460 px width.
