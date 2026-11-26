"""
AI Copilot for Blender 5.3 Desktop (`blender_copilot`).
Adds an "AI Copilot" tab in Blender's 3D Viewport N-Panel sidebar, a header button,
a Quick Ask shortcut (Ctrl+Shift+Space), and automatically launches & docks the
native WPF AI Copilot panel (`BlenderCopilotHost.exe`) on the right of Blender 5.3.
"""

from __future__ import annotations
import bpy
from typing import Optional
from .server import BlenderWorkerServer

bl_info = {
    "name": "AI Copilot for Blender 5.3",
    "author": "CAD Studio / AI Copilot",
    "version": (0, 1, 0),
    "blender": (5, 0, 0),
    "location": "View3D > Sidebar (N) > AI Copilot",
    "description": "Docked AI Copilot chat panel (Claude, ChatGPT, Gemini, no API keys) and Quick Ask that models and drafts live in Blender 5.3 Desktop.",
    "category": "3D View",
}

_WORKER: Optional[BlenderWorkerServer] = None
_KEYMAPS = []


def get_worker() -> Optional[BlenderWorkerServer]:
    return _WORKER


class BLENDERCOPILOT_OT_toggle_panel(bpy.types.Operator):
    bl_idname = "blendercopilot.toggle_panel"
    bl_label = "AI Copilot Panel"
    bl_description = "Show or hide the docked AI Copilot panel on the right of Blender 5.3"

    def execute(self, context):
        worker = get_worker()
        if worker is not None:
            if not worker.launch_wpf_host_if_needed():
                pass
            worker.pending_ui_events.append("toggle_panel")
            self.report({"INFO"}, "AI Copilot panel toggled.")
        return {"FINISHED"}


class BLENDERCOPILOT_OT_quick_ask(bpy.types.Operator):
    bl_idname = "blendercopilot.quick_ask"
    bl_label = "Quick Ask AI (Ctrl+Shift+Space)"
    bl_description = "Open the floating Quick Ask AI box next to the cursor in Blender 5.3"

    def execute(self, context):
        worker = get_worker()
        if worker is not None:
            worker.launch_wpf_host_if_needed()
            worker.pending_ui_events.append("quick_ask")
        return {"FINISHED"}


class BLENDERCOPILOT_OT_undo_stage(bpy.types.Operator):
    bl_idname = "blendercopilot.undo_stage"
    bl_label = "Undo Last AI Stage"
    bl_description = "Undo the last run_commands stage performed by AI Copilot"

    def execute(self, context):
        worker = get_worker()
        if worker is not None:
            res = worker.engine.undo(1)
            self.report({"INFO"}, res.get("message", "Undone."))
        return {"FINISHED"}


class BLENDERCOPILOT_OT_apply_template(bpy.types.Operator):
    bl_idname = "blendercopilot.apply_template"
    bl_label = "Setup Architectural 3D Layers & Materials"
    bl_description = "Create standard architectural collections (A-WALL, A-DOOR, A-GLAZ, A-STRS, A-FURN, A-SITE, A-ROOF), materials, sun light and camera"

    def execute(self, context):
        worker = get_worker()
        if worker is not None:
            res = worker.engine.apply_drawing_template()
            self.report({"INFO"}, res.get("message", "Template applied."))
        return {"FINISHED"}


class VIEW3D_PT_blender_copilot(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "AI Copilot"
    bl_label = "AI Copilot (Blender 5.3)"

    def draw(self, context):
        layout = self.layout
        worker = get_worker()

        box = layout.box()
        if worker is not None and worker.port > 0:
            box.label(text=f"Status: Ready (Port {worker.port})", icon="CHECKMARK")
            box.label(text=f"Revision: {worker.engine.revision} | Layer: {worker.engine.current_layer}")
            box.label(text=f"Objects: {len(context.scene.objects)} | Selected: {len(context.selected_objects)}")
        else:
            box.label(text="Status: Starting...", icon="TIME")

        col = layout.column(align=True)
        col.scale_y = 1.35
        col.operator("blendercopilot.toggle_panel", text="Open / Dock AI Copilot Panel", icon="WINDOW")
        col.operator("blendercopilot.quick_ask", text="Quick Ask AI (Ctrl+Shift+Space)", icon="OUTLINER_OB_LIGHT")

        layout.separator()
        col2 = layout.column(align=True)
        col2.operator("blendercopilot.apply_template", text="Setup 3D Arch Template", icon="HOME")
        col2.operator("blendercopilot.undo_stage", text="Undo Last AI Stage", icon="LOOP_BACK")


def _draw_header_button(self, context):
    layout = self.layout
    row = layout.row(align=True)
    row.operator("blendercopilot.toggle_panel", text="AI Copilot", icon="SPARKLES" if hasattr(bpy.types, "SPARKLES") else "WINDOW")


def _timer_callback() -> Optional[float]:
    if _WORKER is None:
        return None
    return _WORKER.pump_main_thread()


def _delayed_host_launch() -> Optional[float]:
    if _WORKER is not None:
        _WORKER. _write_worker_file()
        _WORKER.launch_wpf_host_if_needed()
    return None


CLASSES = (
    BLENDERCOPILOT_OT_toggle_panel,
    BLENDERCOPILOT_OT_quick_ask,
    BLENDERCOPILOT_OT_undo_stage,
    BLENDERCOPILOT_OT_apply_template,
    VIEW3D_PT_blender_copilot,
)


def register() -> None:
    global _WORKER
    for cls in CLASSES:
        try:
            bpy.utils.register_class(cls)
        except ValueError:
            pass

    try:
        bpy.types.VIEW3D_HT_header.append(_draw_header_button)
    except Exception:
        pass

    if _WORKER is None:
        _WORKER = BlenderWorkerServer(bpy)
        _WORKER.start()

    if not bpy.app.timers.is_registered(_timer_callback):
        bpy.app.timers.register(_timer_callback, first_interval=0.05, persistent=True)

    if not bpy.app.background:
        bpy.app.timers.register(_delayed_host_launch, first_interval=1.0, persistent=False)

    # Register Ctrl+Shift+Space shortcut in 3D View
    wm = bpy.context.window_manager
    kc = wm.keyconfigs.addon if wm else None
    if kc is not None:
        km = kc.keymaps.new(name="3D View", space_type="VIEW_3D")
        kmi = km.keymap_items.new("blendercopilot.quick_ask", "SPACE", "PRESS", ctrl=True, shift=True)
        _KEYMAPS.append((km, kmi))


def unregister() -> None:
    global _WORKER
    for km, kmi in _KEYMAPS:
        try:
            km.keymap_items.remove(kmi)
        except Exception:
            pass
    _KEYMAPS.clear()

    if bpy.app.timers.is_registered(_timer_callback):
        bpy.app.timers.unregister(_timer_callback)

    if _WORKER is not None:
        _WORKER.stop()
        _WORKER = None

    try:
        bpy.types.VIEW3D_HT_header.remove(_draw_header_button)
    except Exception:
        pass

    for cls in reversed(CLASSES):
        try:
            bpy.utils.unregister_class(cls)
        except ValueError:
            pass
