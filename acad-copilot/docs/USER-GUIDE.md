# AI Copilot for AutoCAD 2027: User Guide

AI Copilot adds an **AI chat panel on the right of AutoCAD 2027**, with your drawing on the left. You
type what you want, for example *"draw the ground floor plan for a 30x40 east-facing plot with parking, 2 bedrooms
and a staircase"*. The AI then drafts it the way a person would. It types AutoCAD's own commands into the command
line (LINE, PLINE, OFFSET, BREAK, ARC, TEXT, DIMLINEAR, -HATCH, -LAYER ...) together with every number, and you see
each command, each value and each line appear.

There are no special "house" tools. The AI has one general ability, **"run AutoCAD commands with their inputs"**,
and it can **look at the drawing**: it takes a picture of the view and reads the objects. That is why it can draw
any kind of drawing: every floor, elevations, sections, the site plan, furniture and dimensions.

**No API keys.** The panel uses the official command-line apps of the three AI companies, signed in with **your own
accounts**:

| In the panel | Runs | Signs in with |
|---|---|---|
| **Claude** | Claude Code (`claude`) | your Claude Pro / Max (or Team / Enterprise) account |
| **ChatGPT** | OpenAI Codex CLI (`codex`) | your ChatGPT account (Plus / Pro / Team ...) |
| **Gemini** | Gemini CLI (`gemini`) | your Google account |

Usage counts against that account's normal plan limits. AI Copilot does not bill anything per token.

How it works:

```
 AI Copilot panel -> claude / codex / gemini (your login) -> AcadCopilotMcp.exe -> plug-in inside AutoCAD -> command line
```

The panel starts the chosen app in the background (no window). That app talks to AutoCAD through the small
**AutoCAD MCP server** that comes with the plug-in (`AcadCopilotMcp.exe`). The plug-in types the commands on
AutoCAD's own command line. Each request the AI sends is **one undo step**.

> **Status:** this version has been built and checked by the compiler. It has **not run inside AutoCAD yet**. The
> first session in AutoCAD is the real test; see [section 10](#10-verification-status).

---

## 1. Requirements

- **Windows 10 or 11 with AutoCAD 2027** (plain AutoCAD; tested target: 2027.1.1, build 26.0.156). The drafting
  uses only standard AutoCAD commands, so no AutoCAD Architecture is needed; AutoCAD-based products of the same
  release (AutoCAD Architecture, MEP ...) load it too. The plug-in is built for the 2027 release (R26.0, .NET 10)
  only. AutoCAD LT cannot load plug-ins.
- **Node.js** and at least one of the three apps, installed with npm:

  ```powershell
  npm install -g @anthropic-ai/claude-code   # claude
  npm install -g @openai/codex               # codex
  npm install -g @google/gemini-cli          # gemini
  ```

- **A one-time sign-in** for each app you want to use. On this PC, **Claude and ChatGPT are already signed in**.
  **Gemini needs a one-time sign-in**, because it is set to "Gemini API key" mode without a key. Open a normal
  PowerShell window and run:

  | Provider | Command | What happens |
  |---|---|---|
  | Claude | `claude` | Claude Code starts and opens the browser to sign in. If it starts without asking, type `/login`. Check with `claude auth status`. Type `/exit` when done. |
  | ChatGPT | `codex login` | The browser opens; choose **Sign in with ChatGPT**. Check with `codex login status`. |
  | Gemini | `gemini` | Choose **Sign in with Google** and finish in the browser. If it does not ask (as on this PC), type `/auth` and choose **Sign in with Google**. Type `/quit` when done. |

  The **Sign in** button in the panel opens exactly that command in a terminal window for you. Your logins stay
  inside each app (in your user profile). AI Copilot never sees or stores passwords or tokens, and never changes
  the apps' own settings.

---

## 2. Install

**Close AutoCAD first.** AutoCAD only looks for plug-ins when it starts, and it keeps loaded plug-in files open.

From the `acad-copilot` folder, in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1            # Release build -> dist\AcadCopilot.bundle
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -WhatIf  # preview: shows every step, changes nothing
powershell -ExecutionPolicy Bypass -File scripts\install.ps1          # install for your Windows user
```

The installer copies the bundle here (only for you; no administrator rights needed):

```
%APPDATA%\Autodesk\ApplicationPlugins\AcadCopilot.bundle\
    PackageContents.xml                  AutoCAD reads this at start-up
    Contents\AcadCopilot.Plugin.dll      the plug-in (and AcadCopilot.Core.dll)
    Contents\bridge\AcadCopilotMcp.exe   the AutoCAD MCP server the AI apps start
```

AutoCAD's own DLLs are never copied. The installer refuses to run while AutoCAD is open, so a running AutoCAD is never
left with half-replaced files. It also removes Windows' "downloaded from the internet" mark from the files.

**The security question at the first start.** The plug-in is not digitally signed, and this folder is not one of
AutoCAD's trusted locations. So the first time, AutoCAD shows a security message (for example "File Loading -
Security Concern") about `AcadCopilot.Plugin.dll`. Choose **Load** (or **Always Load**, if offered). To stop the
question for good, add the folder
`C:\Users\<you>\AppData\Roaming\Autodesk\ApplicationPlugins\AcadCopilot.bundle\Contents` under **OPTIONS > Files >
Trusted Locations** and restart AutoCAD. Do not set `SECURELOAD` to 0 just for this.

**Uninstall** (AutoCAD closed):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1                  # the plug-in only
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -RemoveUserData  # also settings and logs (%LOCALAPPDATA%\AcadCopilot)
```

Uninstalling never touches the AI apps, their logins or their settings.

---

## 3. First start

1. Start **AutoCAD 2027** and open a drawing, or start a new one.
2. The **AI Copilot panel** docks on the **right**, and the drawing stays on the left. If you closed it, type
   **`COPILOT`** on the command line, or click the **AI Copilot** ribbon tab and then the **AI Copilot** button.
   Both show the panel when it is hidden and hide it when it is shown. You can drag the panel to the left side
   instead.
3. At the top of the panel, pick **Claude**, **ChatGPT** or **Gemini**, and optionally a model ("Default" = the
   app's own default for your account). Each provider shows whether it is signed in. Use **Sign in** if needed,
   then **Check again**.
4. Type your request and press Enter.

`COPILOTSTATUS` on the command line prints the plug-in's state: version, the connection the AI apps use, the
tools, whether the AutoCAD MCP server was found, whether the panel is open, and where the log file is. Use it when
something does not work.

---

## 4. How to work with it

### A typical session

1. **You ask:** *"Draw the ground floor plan for a 30x40 east-facing plot with car parking, 2 bedrooms, a kitchen
   and a staircase. G+1."*
2. **Questions as cards.** If something important is missing (setbacks, bathrooms, how many floors), the AI asks
   with clickable option cards. The suggested option is marked. You can also type your own answer.
3. **Room sizes first.** Before drawing a new plan, the AI shows a table of the plot, the setbacks, the building
   outline and every room with its size and position (for example "Bedroom 1, 11'-0" x 12'-0", rear left").
   Then it asks **"Shall I draw this?"**: *Draw it* / *Change sizes* / *Change rooms*. Nothing is drawn until you
   say OK.
4. **You watch it draw.** It works in stages, shown as a checklist (**Task Progress**): set-up (units, layers,
   text and dimension style), plot and outer walls, inner walls, openings, doors and windows, stair, room names,
   dimensions, title. In the command line you see every command and every value it types.
5. **It checks its own work.** After each stage it takes a picture of the drawing and reads the objects. It looks
   for closed walls, clean wall junctions, doors swinging into the rooms, text that does not overlap and readable
   dimensions, and fixes what is wrong.
6. **Next step.** When a drawing is finished it asks what to do next: *First floor plan*, *Front elevation*,
   *Section*, *Site plan* or *Stop here*. It never starts the next floor or drawing without asking.

### What it draws, and how

- **Units:** you think in feet and inches, and so does the AI. In an empty drawing it switches to architectural
  units (1 drawing unit = 1 inch, display 12'-6") and tells you. If your drawing already uses other units (for
  example millimetres), it asks before changing anything.
- **Exact typing:** while the AI types a stage, AI Copilot switches a few AutoCAD settings so the typed coordinates
  land exactly and no dialog box opens. Typed points win over object snaps and Dynamic Input, and FILEDIA / CMDDIA
  are 0. Your own settings are put back after every stage. The AI works in the World UCS; if your drawing has a
  rotated UCS, it switches to World and tells you.
- **Walls** are drawn as plain double lines: PLINE for the outer face, OFFSET 9" inwards, 4 1/2" inner walls,
  junctions and openings cleaned with BREAK, TRIM or FILLET, like classic manual drafting. Doors are a leaf and an
  arc, windows three lines. This works in plain AutoCAD 2027. (Only if AutoCAD runs as AutoCAD Architecture, which
  `get_drawing_info` reports, can you ask for its wall, door and window objects instead.)
- **Layers:** A-WALL, A-DOOR, A-GLAZ, A-STRS, A-FURN, A-ANNO-TEXT, A-ANNO-DIMS, A-SITE, A-GRID (centre lines),
  A-ELEV and A-HATCH, with sensible colours and lineweights.
- **Annotation:** a text style ARCH (Arial) and a dimension style ARCH-48 for 1/4" = 1'-0" (architectural units,
  tick marks, text above the line). Room names are 9" high and sizes 6", with dimension chains on two sides plus
  overall dimensions, and a title with the scale.
- **Other drawings:** upper floors (same outline, with the stair, toilets and kitchen stacked), elevations and
  sections projected from the plan (plinth, floor levels, sills, lintels, parapet, hatching, level markers), the
  site plan (plot, setbacks, road, compound wall, gate, north arrow) and furniture. Each drawing gets its own area
  of model space and its own title.

### Good requests

- *"Draw the ground floor plan for a 30x50 north-facing plot, 3 bedrooms, 2 toilets, car parking, dog-legged stair."*
- *"Now the first floor with the same outline."*
- *"Draw the front elevation from the ground and first floor plans."*
- *"Add furniture to both bedrooms."*
- *"The kitchen is too small; make it 9 feet wide and move the dining next to it."*
- *"Add dimensions to the first floor plan."*

Name sizes in feet and inches ("12 x 14", "9 inch walls", "3'6" passage"). You can attach a photo or a scan of a
hand sketch with the attach button. The AI reads the room sizes from it and confirms them with you.

If the AI gets something wrong, just tell it in the chat ("the door of bedroom 2 should open the other way",
"the text in the toilet overlaps the door"). It looks at the drawing again and fixes it.

### It learns from you

The AI models themselves cannot be retrained (nobody outside Anthropic, OpenAI or Google can change Claude, ChatGPT
or Gemini). Instead AI Copilot keeps a small **knowledge base on your PC** that the AI reads before every reply and
writes to while it works. It is plain text you can open and change in Notepad:

```
%LOCALAPPDATA%\AcadCopilot\knowledge\
    rules.md        your preferences and corrections ("toilets 5'x7' minimum", "dimensions outside the walls")
    style.md        your drawing style: layers and colours, text heights, dimension style, wall thicknesses,
                    door swings, block names (plus a "My notes" section that is only yours)
    lessons.md      mistakes the AI made in AutoCAD and how it fixed them, newest first
    recipes\*.md    command sequences that worked well ("Dog-legged stair", "Door with swing"), with their steps
    recordings\*.md what you drew by hand while "Learn from me" was on (see "Teach it by drawing" below)
```

- **Corrections stick.** When you correct the AI or say how you like things ("make toilets at least 5 by 7",
  "always put the dimensions outside the walls"), it saves a rule and follows it in every later chat, in every tab
  and with every provider (Claude, ChatGPT and Gemini share the same files). A newer rule on the same subject
  replaces the older one.
- **Your style.** Open one of your own finished drawings and the AI offers *"Learn my style from this drawing?"*
  (or ask: *"learn my style from this drawing"*). It reads the layers, colours, text heights, dimension settings,
  wall thicknesses, door sizes and blocks and writes them into `style.md`. It does not change the drawing. From then
  on it draws with your layers and sizes.
- **Recipes and lessons.** When a stage came out right, the AI may save the steps as a recipe and reuse them next
  time; when it fixes a mistake, it writes a lesson so it does not repeat it. It tells you in one line when it saved
  something.
- **You are in charge.** Edit or delete any line in these files (one item per line starting with `- `), or tell the
  AI *"forget the rule about ..."*. The files are kept small automatically (duplicates merged, oldest items dropped
  when full; lines you wrote yourself without a date are never dropped). Only a summary of about 4,000 characters
  goes into every reply; the AI looks up details (such as recipe steps) when it needs them.
- The knowledge stays on your PC. Like your messages, the summary is sent to the AI provider you picked.

### Teach it by drawing ("Learn from me")

Instead of explaining how you draw, you can show it. Just below the chat tabs is the **Learning** bar:

1. Click **● Start learning**. The bar turns red: **Learning from you · 03:12 · 47 actions**.
2. Draw in AutoCAD as you always do: walls, a door with its swing, a dimension chain, a stair. Every command you run
   is noted, with what you typed at each prompt (for example the OFFSET distance `9`, also written as 0'-9"), the
   current layer, and the objects each command made (layer, colour, linetype, lengths in drawing units and feet-inches,
   text height and style, dimension style, block names, hatch patterns). Zoom and pan are only counted.
3. **Pause** stops the recording for a while (for example while you check something); nothing is lost. **Resume**
   carries on.
4. Click **■ Stop & learn** when you are done. AI Copilot then:
   - saves the recording as a readable file
     `%LOCALAPPDATA%\AcadCopilot\knowledge\recordings\<date>_<time>_<drawing>.md` (your commands in order, grouped
     into steps such as "On A-DOOR: LINE, ARC"),
   - learns the style of the objects you made (layers, text, dimensions, wall thicknesses, blocks) into `style.md`,
   - sends **"Learn from my recording ..."** in the chat tab you are looking at. The AI reads the recording, saves the
     sequences you use as recipes (for example "how I draw a door", "my dimension chain") and your habits as rules, and
     tells you briefly what it learned. If that tab is still busy, the message waits in the message box: press Send
     when it is done.
5. **Discard** asks first, then throws the recording away: nothing is saved or learned.

Good to know:

- Only you can start it, with the button. The red bar is visible in every tab while it records, and the recording keeps
  going when you switch tabs (there is one recording for the whole panel).
- **The AI's own work is never recorded**: while a chat types commands or changes the drawing, those commands are
  skipped, so only what you did by hand is learned.
- It only sees AutoCAD (commands, the command line and the drawing), nothing else on your PC. The recording file stays
  on your PC; it reaches the AI provider only when the AI reads it after Stop & learn, like the rest of the knowledge.
- Long sessions are fine: the first 2,000 actions are kept in detail and the rest is counted by command name.
- Shorter recordings teach better: record one thing at a time ("my door", "my dimensions"), then Stop & learn.
- The recordings are plain Markdown files: open, edit or delete them in the `recordings` folder whenever you like.
  You can also ask the AI later: *"look at my recordings again and save the stair as a recipe"*.

---

## 5. Watch and Fast

| Mode | What you see |
|---|---|
| **Watch** (default) | A short pause between typed values (about half a second) and before each new command (about one second), so you can follow every step. |
| **Fast** | No pauses. AutoCAD still finishes each value before the next one is typed. |

Use the **Watch / Fast** switch in the panel. A change applies to the very next typed value, even in the middle of
a stage.

---

## 6. Stop, Esc and undo

- **Stop** (or **Esc** while the panel has the focus) stops the AI straight away. A command it is typing is
  cancelled. What it already drew stays in the drawing.
- **Esc in the drawing** cancels the AutoCAD command in progress, as always.
- **Ctrl+Z** (or `U` on the command line) undoes the AI's work **one stage at a time**: each request the AI sends to
  AutoCAD ("Outer walls", "Doors and windows" ...) is one undo step. The AI can also undo its own last stages when
  you ask it to.
- **New chat** starts a fresh conversation (the previous one is not sent again).
- With several chat tabs, **Stop** and **Esc** act on the tab you see only (see 6a).

---

## 6a. Several chats at once (tabs)

The row at the top of the panel holds **chat tabs**, like AutoCAD's file tabs. Each tab is its own conversation:
its own AI app and model, its own history, Task Progress, approvals and **Stop**. Tabs think, plan and answer **at the
same time**, so you can let one tab draw the ground floor while another works out the first-floor room list.

- **+** opens a new tab. It is tied to the drawing that is **active when you click +**; its name shows under the tab
  title (for example `Drawing2.dwg`). Everything that tab does acts on that drawing, even when you look at another one.
- The tab title is the first thing you asked in it. Click a tab to see it; the header shows its drawing.
- **AutoCAD has one command line, so drawing takes turns.** When two tabs want to type at the same time, one types
  and the other waits: its tab says **2nd in line** and its status line *Waiting for AutoCAD (2nd in line)*. The tab
  that types shows **Drawing...**. When a tab's turn comes and its drawing is not the active one, the copilot switches
  AutoCAD to that drawing first and tells the AI so. Reading the drawing does not wait while the tab's drawing is the
  active one.
- A tab that needs you (a question or an approval) shows **Needs your answer**. If the panel was closed, it opens on
  that tab.
- **Stop** in a tab stops only that tab: a command it is typing is cancelled with Esc, and a drawing step still
  waiting for its turn is dropped. Ctrl+Z still undoes one stage at a time.
- **x** closes a tab. Closing a tab that is still working asks first (**Stop and close** / **Keep**). The last tab is
  replaced by a fresh one. Tabs stay while AutoCAD runs (closing and reopening the panel keeps them); they are not kept
  after AutoCAD is closed.
- At most **4 tabs work at once** (each runs its own AI app, and memory is limited). Any number of tabs can wait idle;
  a fifth working tab is refused with a message and keeps your text, so you can send it when another tab finishes.
- If a tab's drawing is closed, the tab says *(closed)* and its drawing steps fail with an explanation. Open a new tab
  on another drawing to continue.
- **Watch / Fast** and the approval setting are the same for all tabs.

---

## 6b. Speak instead of typing (microphone)

- Click the **microphone** button in the message box (or press **Ctrl+Space**) and speak. Your words appear in the box
  as you talk. Click again or pause about 2 seconds to stop, check the text, then press **Send**.
  Esc stops listening and keeps the words; Enter stops and sends.
- Sizes are written the way the AI understands them: "30 by 40 feet" becomes `30' x 40'`, "9 inch" becomes `9"`,
  "ten feet six inches" becomes `10'-6"`, "G plus one" becomes `G+1`.
- Settings > **Voice input**: "Send automatically after speaking" (off by default) and the language (automatic,
  English India/US/UK, Hindi).
- It uses the speech recognition built into Windows: no API key. Windows must allow it:
  1. Settings > Privacy > Microphone: turn on "Allow access to the microphone on this device" and
     "Allow desktop apps to access your microphone".
  2. Best quality: Settings > Privacy > Speech > turn on **Online speech recognition**. Without it the offline
     English (US) recognizer is used.
  3. Hindi or another language: Settings > Time & Language > Language > that language > Options > Speech.
- If something is missing, the panel says what and shows an "Open Windows Settings" link.

---

## 7. What needs your approval, and what it cannot do

The AI asks first, with an **Approval needed** card in the panel (**Approve** / **Reject**), before it runs commands
that write files or remove things:

- `SAVE`, `QSAVE`, `SAVEAS`, `SAVEALL`, `WBLOCK`, `EXPORT...`, `DXFOUT` and the other ...OUT commands, `PLOT`, `PUBLISH`,
  `ETRANSMIT`
- `PURGE`, `LAYDEL`, `LAYMRG`, `AUDIT`
- `ERASE` with `ALL`

If you reject, the AI does not try again; it asks you what to do instead. In the panel settings (gear icon) you can
switch to **Never ask (auto-approve)**. Saving cannot be undone.

The AI **can never** do these things, whatever it is asked:

- run scripts, plug-ins, AutoLISP, VBA or .NET code (`SCRIPT`, `APPLOAD`, `NETLOAD`, `ARX`, `VBA...`, anything that
  starts with `(` or `!`)
- run other programs or system commands (`SHELL`, `SH` ...)
- open, close or create drawing files, or quit AutoCAD (`OPEN`, `NEW`, `CLOSE`, `QUIT` ...)
- change AutoCAD's security settings (`SECURELOAD`, `TRUSTEDPATHS`)
- type `UNDO` / `U` itself. It uses its own undo, which only steps back its own stages.

Dialog boxes are not used. The AI types the command-line versions (`-LAYER`, `-HATCH`, `-STYLE`, `-INSERT` ...), so
no window pops up.

---

## 8. Limits (please read)

- **It is not perfect every time.** Check the drawings, especially sizes, door swings and dimensions. Correct it
  by telling it in the chat.
- **No code compliance, no structural design.** Room sizes, stairs and setbacks are typical values or your
  numbers. The AI never claims that a plan meets building codes or bylaws, or that a structure is safe. Columns,
  beams, footings, slabs and stairs must be designed by a structural engineer, and setbacks / FAR need local
  approval.
- It works on the **active drawing**, in **model space**.
- **Watch mode is slow on purpose.** A full floor plan is many commands and takes several minutes. It also uses
  your AI plan's message limits. When a limit is reached, the panel shows the app's message.
- While **you** are in the middle of a command, or a dialog box is open, the AI waits. It tells you what to finish
  or close.
- The AI apps are the vendors' general assistants. In the panel they get only the AutoCAD tools (no file editing,
  no shell, no web), but still review what they did before you save.

---

## 9. Troubleshooting

| Symptom | What to do |
|---|---|
| No panel, and `COPILOT` is an unknown command | The plug-in did not load. Check that `%APPDATA%\Autodesk\ApplicationPlugins\AcadCopilot.bundle\PackageContents.xml` exists. If you answered **Do Not Load** at the security question, restart AutoCAD and choose **Load**. With `SECURELOAD` = 2 (often set by IT) only trusted locations load: add the `Contents` folder to Trusted Locations (section 2). Press **F2** to read load errors on the command line. `APPAUTOLOADER` lists the bundles AutoCAD found. |
| The command line says AI Copilot could not start, or that the chat panel is not available | Run `COPILOTSTATUS` and read `%LOCALAPPDATA%\AcadCopilot\logs\addin.log`. AutoCAD keeps working without the plug-in. |
| A provider shows **Not installed** | Install its app with npm (section 1). It must be on PATH for your Windows user (`where claude`). |
| A provider shows **Sign in** | Click **Sign in**, finish in the browser, then **Check again**. |
| The AI says AutoCAD is busy | Finish or cancel your own command (Esc), close any open dialog box, then ask again. |
| Lines land in slightly wrong places | Tell the AI. It reads what was typed at each prompt, checks the UCS, and if needed turns off running object snaps (like **F3**; press F3 to turn them on again), then undoes and redraws the stage. |
| A stage looks wrong | Tell the AI what is wrong, or press Ctrl+Z to remove the whole stage and ask it to draw it again. |
| The answer stops with a usage-limit message | That account's plan limit was reached: switch provider or wait. |
| Your own Claude Desktop / Codex cannot start the AutoCAD server | AutoCAD must be running with the plug-in loaded. Run `AcadCopilotMcp.exe --version` in PowerShell. If that fails, install the .NET 10 runtime. See `%LOCALAPPDATA%\AcadCopilot\logs\bridge.log`. |
| The installer says AutoCAD is running | Close AutoCAD (save your work), then run it again. |

Logs: `%LOCALAPPDATA%\AcadCopilot\logs\addin.log` (plug-in) and `bridge.log` (MCP server). Settings (provider and
model, no secrets): `%LOCALAPPDATA%\AcadCopilot\settings.json`. What the AI learned:
`%LOCALAPPDATA%\AcadCopilot\knowledge\` (see "It learns from you"). If the AI keeps doing something you do not want,
look for a wrong line in `rules.md` or `style.md` and delete it.

### Using the AutoCAD MCP server from your own apps

With AutoCAD open and the plug-in loaded, you can also drive AutoCAD from your own Claude Desktop, Claude Code, Codex
or Gemini sessions. Point them at:

```
C:\Users\<you>\AppData\Roaming\Autodesk\ApplicationPlugins\AcadCopilot.bundle\Contents\bridge\AcadCopilotMcp.exe
```

Claude Desktop example (`%APPDATA%\Claude\claude_desktop_config.json`; write the full path, because it does not
expand `%APPDATA%`):

```json
{ "mcpServers": { "acad": { "command": "C:\\Users\\<you>\\AppData\\Roaming\\Autodesk\\ApplicationPlugins\\AcadCopilot.bundle\\Contents\\bridge\\AcadCopilotMcp.exe", "args": [] } } }
```

Claude Code: `claude mcp add --scope user acad -- "<path above>"`. Codex: `codex mcp add acad -- "<path above>"`.
Gemini: `gemini mcp add --scope user acad "<path above>"`. Questions are then asked in that app's chat. Approvals for
saving, plotting and so on still appear in the AutoCAD panel. These apps do not get the drafting instructions the
panel gives, so the panel is the better place to draw. This is a different server from Autodesk's own "AutoCAD MCP
Server", which is also installed on this PC. The two do not interfere.

---

## 10. Verification status

| Part | State |
|---|---|
| Build | Compiles in Release with the .NET 10 SDK against the AutoCAD 2027 assemblies (0 warnings, 0 errors on 2026-11-17). `scripts\build.ps1` assembles and checks the bundle: 17 files, 3.8 MB, no AutoCAD DLLs inside, the manifest's DLL exists, and `AcadCopilotMcp.exe --version` runs. The install and uninstall scripts were checked in preview mode (`-WhatIf`) only. |
| Without AutoCAD | Unit and integration tests cover the command policy, the JSON tools, the pipe connection, the MCP server (driven by the official MCP client against a stand-in for AutoCAD), the three AI apps' command lines and output parsing, and the panel (rendered off-screen). |
| AI apps | The same app runners were tested live with Claude Code and Codex CLI (signed in on this PC) in the earlier Revit version of this product. Gemini has not been tested live because it is not signed in yet. |
| **Inside AutoCAD** | **Compile-verified only, waiting for the first real run:** loading, the panel and ribbon tab, typing commands on the command line, the picture check, undo grouping and approvals have not run inside AutoCAD yet. |
| Plain AutoCAD 2027 + learning (2026-11-19) | Retargeted from AutoCAD Architecture to plain AutoCAD 2027 (manifest platform `AutoCAD\|AutoCAD*`, no AEC assemblies needed, instructions without Architecture objects). Tested without AutoCAD: the knowledge files (rules, style, lessons, recipes: dedupe, newest wins, size caps, parallel writers), keyword recall, the per-turn "WHAT YOU HAVE LEARNED" summary and its size limits (including the Codex command-line limit), the style analysis (layers, text, dimensions, wall thicknesses, door swings, blocks) and the learning tools through the built `AcadCopilotMcp.exe`. **Compile-verified only:** `learn_style_from_drawing` reading a real drawing, and the product detection inside AutoCAD. |
| Teach it by drawing (2026-11-19) | Tested without AutoCAD: the recording (commands with their answers, objects, layer changes, pause, the 2,000-action limit, the AI's own commands left out), the grouping into steps, the Markdown file, `list_recordings` / `read_recording` (also through the built `AcadCopilotMcp.exe`), and the Learning bar rendered off-screen (`artifacts\ui\15-learn-from-me-*.png`). **Compile-verified only:** capturing your commands, the command line and the objects from AutoCAD's events, and the style of your new objects. |
| Chat tabs (2026-11-18) | Tested without AutoCAD: parallel turns, the 4-at-once limit, per-tab provider / history / approvals, the turn queue (order, Stop leaves it), routing of questions and plans to their tab over the real pipe, the bridge's `--session` (built `AcadCopilotMcp.exe`), and the tab strip rendered off-screen. **Compile-verified only:** binding a tab to its AutoCAD drawing and switching AutoCAD to that drawing (`DocumentCollection.MdiActiveDocument`) when its turn comes. |

### First-run checklist

1. Close AutoCAD, then run `scripts\install.ps1` (section 2).
2. Start AutoCAD 2027 and answer the security question with **Load**. Open a new drawing.
3. The panel should be docked on the right. If not, type `COPILOT`. If that is unknown, see Troubleshooting.
4. Type `COPILOTSTATUS`. Check that it shows a pipe, the tools (`run_commands`, `capture_view`, ...) and
   `Bridge: ... (found)`, with no start-up error.
5. Pick **Claude** (it should show as signed in). Ask: *"What is in this drawing?"* Expect a short answer with the
   units and layers.
6. Ask: *"Draw a 10 x 12 ft room with 9 inch walls."* Expect the size table and "Shall I draw this?". Choose
   *Draw it*, then watch the command line. Press **Ctrl+Z** once and check that one whole stage disappears.
7. Ask: *"Save the drawing."* Expect an **Approval needed** card. Click **Reject** and check that nothing was saved.
8. Repeat step 5 with **ChatGPT**, and with **Gemini** after signing in.
9. Try a full plan: *"Ground floor plan for a 30x40 east-facing plot, 2 bedrooms, parking, staircase."*
9a. Tabs: open a second drawing (`NEW`), click **+**, and ask for a small room there while the first tab is still
    drawing. Check that the second tab shows **2nd in line**, that AutoCAD switches to the second drawing when its
    turn comes, and that **Stop** in one tab does not stop the other.
9b. Learning: say *"toilets must be at least 5 by 7 feet"*. Expect a one-line "remembered" note and the rule in
    `%LOCALAPPDATA%\AcadCopilot\knowledge\rules.md`. Start a new tab and ask for a plan with a toilet: the table
    should use 5'x7'. Open one of your finished drawings and ask *"learn my style from this drawing"*; check
    `style.md` (layers, text heights, dimension style, wall thickness) and that the drawing was not changed.
    `get_drawing_info` (ask *"what product is this?"*) should report **AutoCAD 2027** and no architecture objects.
9c. Teach it by drawing: click **Start learning**, draw a door by hand (LINE leaf + ARC swing on your door layer) and
    one OFFSET, then **Stop & learn**. Check the file in `knowledge\recordings\` (your commands, what you typed, the
    objects), that the chat says what it learned, and that a drawing step of the AI made while recording is not in
    the file.
10. If anything fails, keep `%LOCALAPPDATA%\AcadCopilot\logs\addin.log` and `bridge.log` and describe what you saw.
