# CAD Copilot in AutoCAD

The CAD Copilot connector for AutoCAD is a .NET plug-in that runs inside `acad.exe`. It lets the
CAD Copilot app (`apps/copilot`) find a running AutoCAD, read the active drawing and draw or edit
in it through the same structured tools it uses with CAD Studio. Source:
[`integrations/autocad-plugin`](../../integrations/autocad-plugin/README.md).

> ## Verification status — read this first
>
> **The plug-in has been compiled against Autodesk's reference assemblies (NuGet `AutoCAD.NET`
> 25.0.1, the AutoCAD 2025 API) but has never been run inside AutoCAD.** AutoCAD is not installed on
> the machine it was built on. Treat the first run in a real AutoCAD as a test.
>
> | Tested for real (by running it) | Only compiled, not yet run in AutoCAD |
> |---|---|
> | The protocol library `CadCopilot.Protocol` (xUnit, `tests/CadCopilot.Protocol.Tests`): NDJSON framing, JSON-RPC, adverts, tokens, pipe names, the named-pipe server with real pipes (auth, errors, ordering, concurrency, notifications, stop), schema validation against the real tool catalog, units and rounding, the drafting maths and the `validate_drawing` checks, and source checks of the plug-in (registered tool names = catalog minus `create_floor_plan`, `PackageContents.xml` commands = `[CommandMethod]`s) | Everything in `src/CadCopilot.AutoCAD` that calls the AutoCAD API: loading, start-up on the first Idle, main-thread dispatching, document locking, undo grouping, the revision counter (database events), every AutoCAD tool, the `COPILOT*` commands, document / selection notifications, window bounds and focus |
> | The plug-in's AutoCAD-free architecture geometry (`src/CadCopilot.AutoCAD/Arch/Core`: walls, openings, rooms, door / window / stair symbols, the XData record codec), xUnit in `tests/CadCopilot.Arch.Tests` | Loading through the autoloader bundle (`PackageContents.xml`) in AutoCAD 2025 and 2026, and AutoCAD's security prompts for it |
> | The fake CAD (`CadCopilot.FakeCad`, an in-memory drawing behind the same protocol) with its 20 tools, in xUnit and, through Vitest (`integrations/autocad-plugin/interop`), against the real TypeScript client of `packages/cad-protocol` | A real install with `install.ps1`; building against `AutoCAD.NET` 25.1.x (the AutoCAD 2026 API) |
> | `scripts/build-bundle.ps1` (builds, assembles, checks and zips the bundle) and `scripts/install.ps1 -WhatIf` / `uninstall.ps1 -WhatIf` (dry runs) | |
>
> Last full run on the build machine: 0 build warnings, `CadCopilot.Protocol.Tests` 203 passed,
> `CadCopilot.Arch.Tests` 42 passed, interop 31 passed (Vitest; two of them run the two xUnit
> projects), `build-bundle.ps1` succeeded, `export-tool-schemas.mjs --check` up to date (43 tools).

## Supported versions

| AutoCAD | Internal release | Supported |
|---|---|---|
| AutoCAD 2025 | R25.0 (.NET 8) | yes |
| AutoCAD 2026 | R25.1 (.NET 8) | yes (same binary) |
| AutoCAD 2024 and older | R24.x and older (.NET Framework 4.8) | no — the plug-in is a .NET 8 assembly |
| AutoCAD 2027 and newer | R26.x and newer | not yet — the bundle declares `SeriesMax="R25.1"`, so AutoCAD ignores it |
| **AutoCAD LT** (any year) | — | **no** — AutoCAD LT cannot load .NET plug-ins at all |

Full AutoCAD on 64-bit Windows only. The AutoCAD-based toolsets (Architecture, Mechanical, MEP,
Map 3D, Electrical, Civil 3D …) are AutoCAD underneath and match the bundle's `Platform="AutoCAD*"`;
they should work the same way but have not been tried either.

## Installing

Close AutoCAD first: AutoCAD only looks for plug-ins when it starts, and keeps loaded DLLs open.

### Option 1: the install script (recommended)

You need the bundle: either `CadCopilot.bundle-<version>.zip` from a release, or build it yourself
(see [For developers](#for-developers)). Then, in PowerShell:

```powershell
cd integrations\autocad-plugin
powershell -ExecutionPolicy Bypass -File scripts\install.ps1                       # uses dist\CadCopilot.bundle
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -BundlePath C:\Downloads\CadCopilot.bundle-0.1.0.zip
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -WhatIf               # show what it would do, change nothing
```

The script:

- refuses to run while `acad.exe` is running;
- copies the bundle to `%APPDATA%\Autodesk\ApplicationPlugins\CadCopilot.bundle` (only for you;
  no administrator rights needed), replacing an older version only once the new copy is complete;
- removes Windows' "downloaded from the internet" mark from every file (`Unblock-File`), without
  which .NET refuses to load the DLLs;
- prints every file it installed and warns if another copy is installed for all users.

### Option 2: copy the bundle by hand

Extract the zip and copy the `CadCopilot.bundle` folder (the folder itself, with
`PackageContents.xml` directly inside it) to one of AutoCAD's plug-in folders:

| Folder | Who | Notes |
|---|---|---|
| `%APPDATA%\Autodesk\ApplicationPlugins` | you only | what `install.ps1` uses |
| `%ProgramData%\Autodesk\ApplicationPlugins` | all users | needs administrator rights |
| `%ProgramFiles%\Autodesk\ApplicationPlugins` | all users | needs administrator rights; AutoCAD treats this folder as trusted |

If the files came from a download, right-click each DLL → Properties → tick **Unblock**, or run
`Get-ChildItem <folder>\CadCopilot.bundle -Recurse | Unblock-File`. Install it in one folder only,
otherwise AutoCAD loads two connectors.

### Option 3: NETLOAD (one session, for testing)

In AutoCAD type `NETLOAD`, pick `CadCopilot.AutoCAD.dll` (in the bundle's `Contents` folder, or the
build output `src\CadCopilot.AutoCAD\bin\Release\net8.0-windows`). Keep `CadCopilot.Protocol.dll`
in the same folder. The plug-in stays loaded until AutoCAD closes; it is not loaded again in the next
session. A .NET assembly cannot be unloaded, so to load a newer build restart AutoCAD.

## AutoCAD's security prompts

AutoCAD only loads code from locations it trusts. What happens depends on the `SECURELOAD`
system variable:

| SECURELOAD | Plug-in outside a trusted location |
|---|---|
| 0 | loads without asking (not recommended) |
| 1 (default) | AutoCAD shows a security warning that the file `CadCopilot.AutoCAD.dll` is not in a trusted location / not digitally signed |
| 2 | silently not loaded — the connector never starts and COPILOTSTATUS is an unknown command |

The plug-in is **not digitally signed**, and `%APPDATA%\Autodesk\ApplicationPlugins` is not a
trusted location by default, so with the default `SECURELOAD=1` expect the prompt the first time.

- Choose **Always Load** if you trust this build: AutoCAD remembers the answer for that file.
- **Load Once** loads it for this session only; you are asked again next time.
- **Do Not Load** skips it. Typing a `COPILOT*` command later loads it on demand (and asks again).

The wording of the dialog differs slightly between releases. To stop the prompt for good, add the
bundle's `Contents` folder to the trusted locations: `OPTIONS` → **Files** → **Trusted Locations**
→ Add `%APPDATA%\Autodesk\ApplicationPlugins\CadCopilot.bundle\Contents` (with your user name
expanded), or set the system variable `TRUSTEDPATHS`. A trusted location should not be writable by
other users; your own `%APPDATA%` folder is fine for a single-user install. Do not lower `SECURELOAD`
to 0 just for this plug-in. With `SECURELOAD=2` (often enforced by IT) the trusted location is the
only way in.

## Checking that it runs

1. Start AutoCAD and open or create a drawing. The command line shows
   `CAD Copilot connector ready (cadcopilot-autocad-<pid>…).` once AutoCAD is idle.
2. Type `COPILOTSTATUS`. It prints the connector state (`running` / `stopped`), the pipe name, the
   advert file in `%APPDATA%\CadCopilot\connectors`, the number of connected copilot clients, the
   tools this build supports, the active drawing (id, unit, revision, entity count), the copilot's
   last operation and the last lines of the connector log.

If `COPILOTSTATUS` is an unknown command, the plug-in was not loaded: see
[Troubleshooting](#troubleshooting).

## Connecting CAD Copilot

1. Start AutoCAD (the connector writes its advert file on start-up).
2. Start CAD Copilot. AutoCAD appears in the list of CAD applications as **AutoCAD 2025** /
   **AutoCAD 2026** (or the toolset's name). Click **Connect**; when it is the only CAD application
   running, the copilot connects by itself.
3. The copilot docks beside the AutoCAD main window and talks to the active drawing.

Everything stays on your computer: the connection is a Windows named pipe that only your user
account can open, and every connection must first present the random token from the advert file.
Several copilot windows may be connected at the same time; their requests are executed one after
another on AutoCAD's main thread.

## Commands

| Command | What it does |
|---|---|
| `COPILOTSTATUS` | connector state, pipe, clients, supported tools, active drawing, recent log lines |
| `COPILOTINFO` | prints the `get_drawing_info` result exactly as the copilot receives it |
| `COPILOTLAYERS` | prints the `list_layers` result |
| `COPILOTCREATE` | asks for two points and draws a test line through the `create_line` tool path (validation, undo group, revision) |
| `COPILOTSELECT` | prints the current selection as the copilot sees it (handles and entity types) |
| `COPILOTUNDO` | undoes the copilot's last operation if nothing else changed the drawing since |
| `COPILOTSTART` | starts the connector (also when `CADCOPILOT_DISABLE=1`, or after a failed start) |
| `COPILOTSTOP` | stops the connector and removes its advert; connected copilots are told and disconnect |

Setting the environment variable `CADCOPILOT_DISABLE=1` before starting AutoCAD keeps the connector
from starting automatically; `COPILOTSTART` starts it anyway.

## What the tools do in AutoCAD

The copilot only offers the model the tools the connector lists in its `hello` answer
(`supportedTools`); `COPILOTSTATUS` shows that list for your build. The catalog is shared with
CAD Studio (`packages/cad-protocol`). This build implements **42 of the 43 catalog tools**; the
only one missing is `create_floor_plan` (not offered in AutoCAD: the copilot builds plans from
`create_room`, `create_wall`, `create_door` and `create_window` instead).

| Family | In AutoCAD terms |
|---|---|
| Inspection (`get_drawing_info`, `list_layers`, `inspect_entities`, `get_selected_objects`, `find_objects`, `inspect_room`, `get_drawing_revision`) | Reads model space of the active drawing: drawing name, path and unit (INSUNITS), entity counts per type and extents, the layer table (colour, visible = on and thawed, locked, current, entity count), entities by handle with their geometry, the pickfirst selection, the rooms made by the copilot. Read-only: no lock, no undo step. |
| Geometry (`create_line`, `create_polyline`, `create_rectangle`, `create_circle`, `create_arc`, `create_text`, `create_entities`) | Adds `Line`, lightweight `Polyline` (a rectangle is a closed 4-vertex polyline), `Circle`, `Arc` and text entities (`DBText`; `MText` when the text has several lines or more than 255 characters) to model space on the requested or current layer, ByLayer or a true colour. `create_entities` adds up to 500 in one call and one undo step. |
| Architecture (`create_wall`, `create_room`, `create_door`, `create_window`, `create_staircase`, `create_column`, `create_plot_boundary`; `move_room`, `resize_room`) | Plain AutoCAD entities (closed polylines, lines, arcs, circles, MText) on the layers "External Walls", "Internal Walls", "Doors", "Windows", "Rooms" (not plotted), "Text", "Staircases", "Columns" and "Plot Boundary" (created with CAD Studio's colours when missing). Each carries a `CADCOPILOT` XData record (wall axis and thickness, room size, opening position, ...) so the copilot can find and rebuild them later; to AutoCAD and to other users they are ordinary geometry. A door or window cuts its wall polyline into the remaining pieces. Walls of neighbouring rooms are not merged and corners are not cleaned up. |
| Modification (`move_entities`, `copy_entities`, `rotate_entities`, `scale_entities`, `mirror_entities`, `update_entity_properties`, `delete_entities`) | Transforms entities by handle with displacement / rotation / scaling / mirror matrices (single-line text stays readable when `MIRRTEXT` is 0), deep-clones them like `COPY`, changes layer and colour, erases them (destructive: the copilot asks for confirmation). Entities on locked layers are refused up front. |
| Annotation (`add_dimension`, `add_room_labels`) | A non-associative `RotatedDimension` (linear) or `AlignedDimension` in the current dimension style, on the layer "Dimensions" when it exists, else the current layer; room labels (name and area) as MText on "Text", updated in place when a room already has one. |
| Layers (`create_layer`, `set_current_layer`, `set_layer_state`) | Layer table records: create with a colour, make current (CLAYER), show (on and thaw) / hide (off), lock / unlock, recolour, rename. |
| Validation (`validate_drawing`) | Read-only checks of model space: `duplicates`, `zero_length`, `self_intersections`, `tiny_gaps`, `hidden_or_locked`. Lines, lightweight polylines, circles and arcs in the XY plane are checked geometrically; other entities only by the layer check. |
| View (`zoom_extents`, `zoom_to_entities`, `highlight_entities`, `clear_highlight`) | Changes the current model-space view (no drawing change; refused while a layout's paper space is active) and highlights entities the way AutoCAD highlights a selection. |
| Document (`undo_last_operation`, `save_document`) | Undo of the copilot's last operation (see [Undo](#undo)); saving the drawing to its own file (see below). |

**Saving.** `save_document` writes the drawing back to its own file, like `QSAVE` (a `.bak` is
kept; a drawing opened from `.dxf` is written as DXF). A drawing that was never saved has no file
yet, so the call fails and asks you to save it once with `SAVEAS`; a drawing opened read-only fails
too. The file is written after the call's transaction has been committed, with the drawing locked.
Not yet observed in AutoCAD: whether the title bar's "modified" mark (`DBMOD`) is cleared after a
save made this way, so AutoCAD may still ask to save when you close the drawing.

Rules that apply to every tool:

- **Entity ids** are AutoCAD handles as upper-case hexadecimal strings (`"2A3F"`), the same value
  `LIST` shows as "Handle". They stay valid across save and reopen.
- **Coordinates** are World Coordinate System coordinates of model space, `{x, y}`; angles are in
  degrees, counter-clockwise from +X. Paper-space layouts are not inspected or edited.
- **Failures are explained, not hidden.** When a tool cannot run (no drawing open, AutoCAD busy,
  a locked layer, invalid arguments, a stale plan) the copilot receives a failed result with the
  reason and the drawing is left exactly as it was: each call runs in one transaction that is
  aborted on any failure.
- **Stale plans.** The connector counts every change to the drawing (yours and the copilot's:
  added, modified or erased objects, layers, and a change of the current layer or of `INSUNITS` /
  `MEASUREMENT`) as a new revision; zooms and pans do not count. If you edit the drawing between the
  copilot looking at it and the copilot changing it, the change is refused and the copilot looks
  again before acting.
- **Busy AutoCAD.** Every tool call waits for AutoCAD's main thread and is refused while one of
  your commands is running (see [Troubleshooting](#troubleshooting)).

## Undo

Each copilot tool call that changes the drawing is **one undo step** in AutoCAD, named `COPILOT`
in the undo list — including `create_entities` with many entities. So:

- `U` / `Ctrl+Z` in AutoCAD reverts the copilot's last call (and `REDO` restores it), just like
  one of your own commands.
- The copilot's own **Undo** (`undo_last_operation`) and the `COPILOTUNDO` command revert its last
  operation only while it is still the last change in the drawing; if you changed the drawing
  afterwards they refuse, so your work is never undone by accident.
- `undo_last_operation` cannot run AutoCAD's UNDO inside the tool call, so it answers first and runs
  `UNDO 1` right afterwards, when AutoCAD is idle: its result still shows the revision from before
  the undo (with a warning saying so), and the undo is skipped (with a message on the command line)
  if the drawing changed or another drawing became active in between.
- Known limit: these run AutoCAD's `UNDO 1` (up to three times, until the drawing actually changes),
  so a zoom or pan made after the copilot's operation is stepped back first: the zoom tools change
  the view under a document lock, which AutoCAD records as an undo step named `COPILOT` just like a
  `ZOOM`.
- Read-only tools record nothing; view tools (zoom) record no drawing change but do add that view
  undo step; a call that fails changes nothing (its transaction is aborted).
- Saving (`save_document`) cannot be undone and is never the copilot's "last operation".

## Units

Lengths in tool arguments carry a unit (`mm`, `cm`, `m`, `in`, `ft`; the default is the drawing's
unit). The drawing's unit comes from `INSUNITS`:

| INSUNITS | Unit the copilot uses |
|---|---|
| 1 inches, 2 feet, 4 millimetres, 5 centimetres, 6 metres | that unit |
| 0 (unitless) | inches when `MEASUREMENT` = 0 (imperial), millimetres when `MEASUREMENT` = 1 (metric) — every result then carries a warning saying so |
| any other unit (yards, miles, mils, microinches → inches; decimetres, microns, kilometres, … → millimetres) | the copilot works in that protocol unit and the connector converts exactly to the drawing's real unit; every result carries a warning saying so |

To avoid guessing, set `INSUNITS` in your drawing (or template) before working with the copilot,
for example `INSUNITS` → `4` for millimetres. Results are rounded to 6 decimals.

## Troubleshooting

**AutoCAD is not listed in CAD Copilot.**

1. Type `COPILOTSTATUS` in AutoCAD. Unknown command → the plug-in is not loaded (next item).
   `stopped` → type `COPILOTSTART` and read the message; check that `CADCOPILOT_DISABLE` is not set.
2. Check that the advert file `%APPDATA%\CadCopilot\connectors\autocad-<pid>.json` exists while
   AutoCAD runs. CAD Copilot reads that folder for the same Windows user: AutoCAD and CAD Copilot
   must run as the same user, and at the same elevation (do not start only one of them "as
   administrator"). If `CADCOPILOT_HOME` is set, it must have the same value for both programs.
3. Wait a few seconds: the connector starts when AutoCAD becomes idle after start-up, and the
   copilot polls the folder.

**The plug-in does not load (COPILOT* commands unknown).**

- Version: AutoCAD 2025 or 2026 only; AutoCAD LT never loads plug-ins.
- Location: the folder must be `...\ApplicationPlugins\CadCopilot.bundle\PackageContents.xml`,
  not a nested `CadCopilot.bundle\CadCopilot.bundle\…` from extracting the zip twice.
- Security: you may have answered **Do Not Load**, or `SECURELOAD` is 2 — add the `Contents`
  folder to Trusted Locations (see [AutoCAD's security prompts](#autocads-security-prompts)).
- Blocked files: run `install.ps1` again, or `Unblock-File` the DLLs.
- Load errors are shown on the command line when AutoCAD starts; press `F2` to see the text window.
  `APPAUTOLOADER` → `LIST` shows the bundles AutoCAD found, `APPAUTOLOADER` → `RELOAD` re-reads the
  plug-in folders.
- Try `NETLOAD` of `Contents\CadCopilot.AutoCAD.dll`: its error message is usually more explicit.

**"AutoCAD is busy running …" in the copilot.** A command is active in AutoCAD (for example
`LINE` waiting for a point, or a grip edit). The connector never interrupts you: finish the command
or press `Esc`, then ask the copilot again.

**"AutoCAD did not take the request … within 30 s."** AutoCAD's main thread did not become free:
a modal dialog is open (Options, a file dialog, an error message), a long operation is running, or
AutoCAD is minimised during a plot. Close the dialog and retry. Long-running requests that already
started are not cut off.

**"No drawing is open."** Open or create a drawing; the copilot works on the active drawing and
follows you when you switch drawings.

**A locked layer.** Tools that edit entities on a locked layer, or draw on one, are refused before
anything changes ("These entities are on locked layers: … Unlock the layer first", "Layer … is
locked"); unlock the layer or ask the copilot to (`set_layer_state`). Other refusals from AutoCAD
itself are reported as `AutoCAD rejected <tool>: <ErrorStatus> (…)`.

**"The drawing changed since you last inspected it."** You (or another program) changed the
drawing between the copilot's look and its change; the copilot inspects again and retries. Changing
the current layer counts as a change.

**The highlight disappeared.** Highlights are temporary, like AutoCAD's own selection highlight:
a regeneration (`REGEN`, some zooms) clears them.

## Uninstalling

Close AutoCAD, then:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1          # -WhatIf to preview
```

It removes `%APPDATA%\Autodesk\ApplicationPlugins\CadCopilot.bundle` and stale AutoCAD advert files
in `%APPDATA%\CadCopilot\connectors`; CAD Copilot's own settings in `%APPDATA%\CadCopilot` are kept.
For a manual or all-users install, delete the `CadCopilot.bundle` folder you copied. If you added a
trusted location for it, remove that in `OPTIONS` → Files → Trusted Locations.

## For developers

Requirements: the .NET 8 SDK (AutoCAD is not needed to build) and network access for the first
NuGet restore. The plug-in compiles against Autodesk's reference assemblies (`AutoCAD.NET`, with
`ExcludeAssets="runtime"`), which are never shipped: `acad.exe` provides them.

```powershell
cd integrations\autocad-plugin
powershell -ExecutionPolicy Bypass -File scripts\build-bundle.ps1
```

`build-bundle.ps1` builds `CadCopilot.sln` in Release (the per-user SDK in
`%LOCALAPPDATA%\Microsoft\dotnet` first, then `dotnet` on PATH; `-Dotnet <path>` to choose), stops on
any build error, and writes:

```
dist\CadCopilot.bundle\
  PackageContents.xml            version from Directory.Build.props written into AppVersion
  Contents\
    CadCopilot.AutoCAD.dll       the plug-in (ModuleName)
    CadCopilot.AutoCAD.deps.json
    CadCopilot.AutoCAD.pdb       symbols (leave out with -NoSymbols)
    CadCopilot.Protocol.dll      the wire protocol
    CadCopilot.Protocol.pdb
dist\CadCopilot.bundle-<version>.zip   the CadCopilot.bundle folder, zipped
```

It checks that the assembly versions match the props version, that no Autodesk assembly ended up
in the bundle (names taken from the restore output of the `AutoCAD.NET*` packages), and that the
commands registered for demand loading in `PackageContents.xml` equal the `[CommandMethod]`s in the
source. When you add a command, add it to `bundle\CadCopilot.bundle\PackageContents.xml` too.
`-AutoCadNetVersion 25.1.1` compiles against the AutoCAD 2026 API instead (not tried yet);
25.0.1 is the lowest supported and the default, so one binary serves 2025 and 2026.

The manifest (`bundle\CadCopilot.bundle\PackageContents.xml`): one `Components` element for
`OS="Win64" Platform="AutoCAD*" SeriesMin="R25.0" SeriesMax="R25.1"` (repeated on the
`ComponentEntry`), the DLL loaded with `LoadOnAutoCADStartup="True"` (the connector must advertise
itself without user action) and `LoadOnCommandInvocation="True"` with the eight `COPILOT*` commands.
A new release must keep the `UpgradeCode` and raise `<Version>` in `Directory.Build.props`.

Tests that run without AutoCAD:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests\CadCopilot.Protocol.Tests -c Release
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests\CadCopilot.Arch.Tests -c Release
node scripts\export-tool-schemas.mjs --check
# from the repository root: the fake CAD against the TypeScript client, plus both xUnit projects
npx vitest run --config vitest.next.config.ts integrations/autocad-plugin/interop
```

### First run in a real AutoCAD (checklist)

The plug-in has never run inside AutoCAD, so the first session is a test. In this order, with a new
drawing from `acadiso.dwt` (INSUNITS = 4, mm), the text window open (`F2`):

1. `NETLOAD` `bin\Release\net8.0-windows\CadCopilot.AutoCAD.dll` → expect `CAD Copilot connector
   ready (\\.\pipe\cadcopilot-autocad-<pid>)` at the next idle. A load error here is the most
   likely first failure (assembly resolution of `CadCopilot.Protocol.dll`, .NET 8 runtime).
2. `COPILOTSTATUS` → `running`, 42 tools, `Problem:` lines absent, the advert file exists in
   `%APPDATA%\CadCopilot\connectors`.
3. `COPILOTINFO`, `COPILOTLAYERS` → JSON results with `"success": true` and `"unit": "mm"`.
4. `COPILOTCREATE` (two points) → a line appears; `UNDO` → the undo list names it; `COPILOTSTATUS`
   shows the revision moved by exactly one.
5. Select something, `COPILOTSELECT` → the handles match `LIST`.
6. Connect CAD Copilot (or a small Node script using `listAdverts` / `connectToConnector` from
   `@cadstudio/cad-protocol/node`): `hello`, then a read tool, then
   `create_line` with `expectedRevision`, then the same call again with the old revision (must be
   refused as stale), then `undo_last_operation` and watch the command line for `CAD Copilot undid`.
7. While `LINE` waits for a point, call a tool → "AutoCAD is busy running the command LINE"; with
   the `OPTIONS` dialog open → the same refusal; a long blocking dialog → busy after 30 s.
8. `zoom_extents` in a plan view and in a rotated 3D view (`-VIEW` → SW Isometric) → the drawing
   fills the window in both.
9. `create_room`, `create_door` on one of its walls, `move_room`, `resize_room`, `add_room_labels`,
   `add_dimension`, `validate_drawing` → check the layers, the `CADCOPILOT` XData (`XDLIST` from
   Express Tools; `LIST` does not show XData), and that one `U` reverts one call.
10. `save_document` on a saved drawing → the file's time stamp changes; note whether the title bar
    still shows the drawing as modified.
11. `COPILOTSTOP` → the advert file is gone and the copilot shows the connector closing; close
    AutoCAD with a copilot connected → no crash, no advert file left.

Debugging inside AutoCAD: build in Debug, `NETLOAD` the DLL from `bin\Debug\net8.0-windows`, and
attach Visual Studio to `acad.exe` (managed .NET Core debugging). `CadCopilot.FakeCad` speaks the same
protocol over an in-memory drawing for working on the copilot side without AutoCAD. How to add a
tool: [`integrations/autocad-plugin/ADDING-TOOLS.md`](../../integrations/autocad-plugin/ADDING-TOOLS.md).
