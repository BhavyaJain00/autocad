# AI Copilot for Blender 5.3 Desktop

An AI Copilot integration for **Blender 5.3 Desktop** (`C:\Program Files\Blender Foundation\Blender 5.3\blender.exe`) built on the exact same architecture, native WPF UI panel, and CLI/MCP engine as **AI Copilot for AutoCAD 2027** (`acad-copilot`).

- **Native Desktop App Integration (not web-based):**
  - Docks the **WPF AI Copilot Panel (`CopilotPane`)** seamlessly on the right side of the Blender 5.3 desktop window (460 px wide, dark theme matching Blender 5.3, resizing Blender's window to the left so the 3D Viewport, Outliner, Properties, and N-panel are never blocked).
  - Adds the **Floating Quick Ask AI Button (`✨ AI`) & Popup Box (`Ctrl+Shift+Space`)** in the top-right corner of Blender 5.3's 3D Viewport.
  - Adds an **`AI Copilot` Tab** in Blender 5.3's 3D Viewport Sidebar (`N` panel) and an **`AI Copilot` Button** in the 3D Viewport header.
- **No API Keys:**
  - Uses your existing signed-in official CLIs: **Claude Code** (`claude`), **OpenAI Codex CLI** (`codex` / ChatGPT login), or **Gemini CLI** (`gemini` / Google login).
  - Connects to Blender 5.3 through the shipped stdio MCP server (`AcadCopilotMcp.exe` -> Named Pipe `\\.\pipe\AcadCopilot-<pid>` -> `BlenderCopilotHost.exe` -> Blender 5.3 main thread).
- **Models & Drafts Live in 3D (Watch & Fast Modes):**
  - **Architectural 3D & 2D CAD commands:** `PLINE`, `LINE`, `RECTANG`, `OFFSET`, `BREAK`, `TRIM`, `FILLET`, `ARC`, `CIRCLE`, `TEXT`, `DIMLINEAR`, `DIMCONTINUE`, `-LAYER`, `-HATCH`, `-ARRAY`, `COPY`, `MOVE`, `ERASE` automatically create **3D architectural walls, openings with lintels, 3D doors with swing arcs, 3D glazed windows, 3D stairs, 3D room text, 3D dimensions, collections, and Principled BSDF materials** in Blender 5.3.
  - **Native 3D Blender commands:** `WALL_3D`, `SLAB_3D`, `DOOR_3D`, `WINDOW_3D`, `STAIR_3D`, `ROOF_3D`, `FURNITURE_3D`, `MESH_CUBE`, `MESH_CYLINDER`, `MESH_SPHERE`, `MESH_PLANE`, `MESH_CONE`, `MESH_TORUS`, `MESH_MONKEY`, `MATERIAL`, `MODIFIER`, `LIGHT`, `CAMERA`, and `BPY` (safe Python `bpy` / `bmesh` / `mathutils` execution).
  - **Visual self-checking (`capture_view`):** Renders the live Blender 5.3 3D Viewport to a PNG preview shown inline in the chat's activity card ("Looked at the drawing") and returned to the AI after each stage.
  - **Plan validation & accuracy checking:** Full support for `validate_plan`, `check_drawing`, `apply_drawing_template`, `export_view_png`, `create_checkpoint`, `undo`, **Chat Tabs**, **Learn from me**, **Voice Input (`Ctrl+Space`)**, **Point Picker (`📍 Point`)**, **Live Selection Chip**, and **Quick Prompts (`⚡`)**.

## Build & Install

```powershell
cd c:\Users\pc\Desktop\AUTOCAD\blender-copilot
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

Once installed, open **Blender 5.3** from your Desktop shortcut — the AI Copilot panel and Quick Ask button start automatically.
