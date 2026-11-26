"""
Core 3D & Architectural CAD Execution Engine for AI Copilot in Blender 5.3 Desktop.
Executes both AutoCAD drafting commands (creating real 3D architectural walls, doors, windows,
stairs, slabs, text, dimensions, hatches, and collections) and native 3D Blender commands
(WALL_3D, SLAB_3D, DOOR_3D, WINDOW_3D, STAIR_3D, ROOF_3D, FURNITURE_3D, MESH_*, MATERIAL,
MODIFIER, LIGHT, CAMERA, BPY) on Blender's main thread, one undo step per call.
"""

from __future__ import annotations
import base64
import json
import math
import os
import tempfile
import time
from typing import Any, Dict, List, Optional, Tuple

from .units import (
    INCHES_TO_METERS,
    METERS_TO_INCHES,
    du_to_meters,
    meters_to_du,
    format_feet_inches,
    parse_length,
    parse_point,
)
from .materials import (
    LAYER_SPECS,
    ACI_COLORS,
    parse_hex_color,
    ensure_collection,
    ensure_material,
    ensure_layer_material,
    assign_object_to_layer,
)


class BlenderCadEngine:
    """Stateful engine attached to the running Blender 5.3 session."""

    def __init__(self, bpy_mod: Any) -> None:
        self.bpy = bpy_mod
        self.revision: int = 1
        self._next_handle: int = 0x1000
        self.current_layer: str = "0"
        self.sysvars: Dict[str, Any] = {
            "CLAYER": "0",
            "LUNITS": 4,       # Architectural feet-inches
            "INSUNITS": 1,     # Inches
            "LUPREC": 4,
            "MEASUREMENT": 0,
            "DIMSCALE": 48.0,
            "LTSCALE": 24.0,
            "TEXTSTYLE": "ARCH",
            "DIMSTYLE": "ARCH-48",
            "TEXTSIZE": 9.0,
            "FILLETRAD": 0.0,
            "WORLDUCS": 1,
            "OSMODE": 4133,
            "WALLHEIGHT": 126.0,    # 10'-6" default 3D wall height in inches
            "WALLTHICK": 4.5,       # 4.5" default single-line wall thickness
            "SILLHEIGHT": 36.0,     # 3'-0" window sill
            "HEADHEIGHT": 84.0,     # 7'-0" door/window lintel head
        }
        self._undo_stack: List[List[str]] = []  # list of created handle lists per run_commands
        self.watch_mode: bool = True

    # ------------------------------------------------------------------ handles & revision

    def _alloc_handle(self, obj: Any) -> str:
        existing = obj.get("acad_handle")
        if existing and isinstance(existing, str):
            return existing
        self._next_handle += 1
        h = f"{self._next_handle:X}"
        obj["acad_handle"] = h
        return h

    def get_handle(self, obj: Any) -> str:
        return self._alloc_handle(obj)

    def find_by_handle(self, handle: str) -> Optional[Any]:
        target = (handle or "").strip().upper()
        if not target:
            return None
        for obj in self.bpy.context.scene.objects:
            if str(obj.get("acad_handle", "")).upper() == target or obj.name.upper() == target:
                return obj
        return None

    def bump_revision(self) -> int:
        self.revision += 1
        return self.revision

    # ------------------------------------------------------------------ geometry builders

    def _create_mesh_object(
        self,
        name: str,
        verts_m: List[Tuple[float, float, float]],
        edges: List[Tuple[int, int]],
        faces: List[Tuple[int, ...]],
        layer: Optional[str] = None,
    ) -> Any:
        mesh = self.bpy.data.meshes.new(name)
        mesh.from_pydata(verts_m, edges, faces)
        mesh.update()
        obj = self.bpy.data.objects.new(name, mesh)
        target_layer = layer or self.current_layer
        assign_object_to_layer(self.bpy, obj, target_layer)
        self._alloc_handle(obj)
        return obj

    def _box_verts_faces(
        self,
        p1_du: Tuple[float, float],
        p2_du: Tuple[float, float],
        thickness_du: float,
        z_bottom_du: float,
        z_top_du: float,
    ) -> Tuple[List[Tuple[float, float, float]], List[Tuple[int, ...]]]:
        """Build 3D oriented box vertices (in meters) and quad faces along segment p1->p2."""
        x1, y1 = du_to_meters(p1_du[0]), du_to_meters(p1_du[1])
        x2, y2 = du_to_meters(p2_du[0]), du_to_meters(p2_du[1])
        dx, dy = x2 - x1, y2 - y1
        length = math.hypot(dx, dy)
        if length < 1e-6:
            dx, dy, length = 1.0, 0.0, 1.0
        nx = -dy / length
        ny = dx / length
        half_t = max(du_to_meters(thickness_du) * 0.5, 0.005)
        z0 = du_to_meters(z_bottom_du)
        z1 = du_to_meters(max(z_top_du, z_bottom_du + 0.5))

        v0 = (x1 + nx * half_t, y1 + ny * half_t, z0)
        v1 = (x2 + nx * half_t, y2 + ny * half_t, z0)
        v2 = (x2 - nx * half_t, y2 - ny * half_t, z0)
        v3 = (x1 - nx * half_t, y1 - ny * half_t, z0)
        v4 = (v0[0], v0[1], z1)
        v5 = (v1[0], v1[1], z1)
        v6 = (v2[0], v2[1], z1)
        v7 = (v3[0], v3[1], z1)
        verts = [v0, v1, v2, v3, v4, v5, v6, v7]
        faces = [
            (0, 3, 2, 1),
            (4, 5, 6, 7),
            (0, 1, 5, 4),
            (1, 2, 6, 5),
            (2, 3, 7, 6),
            (3, 0, 4, 7),
        ]
        return verts, faces

    def _create_segment_entity(
        self,
        p1: Tuple[float, float, float],
        p2: Tuple[float, float, float],
        layer: Optional[str] = None,
        dxf_type: str = "LINE",
        thickness_override: Optional[float] = None,
        height_override: Optional[float] = None,
        z_bottom_override: Optional[float] = None,
    ) -> Any:
        target_layer = (layer or self.current_layer).upper()
        z_base = z_bottom_override if z_bottom_override is not None else p1[2]

        if target_layer == "A-WALL":
            # In architectural plans, walls are drawn either as double face lines or 3D walls.
            # Extrude wall line into a 3D architectural wall panel so the 2D plan is immediately a 3D building!
            thick = thickness_override if thickness_override is not None else 2.25
            h = height_override if height_override is not None else float(self.sysvars.get("WALLHEIGHT", 126.0))
            verts, faces = self._box_verts_faces((p1[0], p1[1]), (p2[0], p2[1]), thick, z_base, z_base + h)
            obj = self._create_mesh_object("Wall", verts, [], faces, target_layer)
            obj["acad_height"] = h
            obj["acad_thickness"] = thick
        elif target_layer == "A-GLAZ":
            sill = float(self.sysvars.get("SILLHEIGHT", 36.0))
            head = float(self.sysvars.get("HEADHEIGHT", 84.0))
            thick = thickness_override if thickness_override is not None else 1.5
            verts, faces = self._box_verts_faces((p1[0], p1[1]), (p2[0], p2[1]), thick, z_base + sill, z_base + head)
            obj = self._create_mesh_object("WindowGlass", verts, [], faces, target_layer)
        elif target_layer == "A-DOOR":
            head = float(self.sysvars.get("HEADHEIGHT", 84.0))
            thick = thickness_override if thickness_override is not None else 1.75
            verts, faces = self._box_verts_faces((p1[0], p1[1]), (p2[0], p2[1]), thick, z_base, z_base + head)
            obj = self._create_mesh_object("DoorLeaf", verts, [], faces, target_layer)
        elif target_layer == "A-STRS":
            verts, faces = self._box_verts_faces((p1[0], p1[1]), (p2[0], p2[1]), 1.5, z_base, z_base + 7.0)
            obj = self._create_mesh_object("StairStep", verts, [], faces, target_layer)
        else:
            thick = thickness_override if thickness_override is not None else 1.2
            h = height_override if height_override is not None else 1.5
            verts, faces = self._box_verts_faces((p1[0], p1[1]), (p2[0], p2[1]), thick, z_base, z_base + h)
            obj = self._create_mesh_object("Line", verts, [], faces, target_layer)

        obj["acad_dxf_type"] = dxf_type
        obj["acad_start"] = [float(p1[0]), float(p1[1]), float(p1[2])]
        obj["acad_end"] = [float(p2[0]), float(p2[1]), float(p2[2])]
        obj["acad_points"] = json.dumps([[float(p1[0]), float(p1[1])], [float(p2[0]), float(p2[1])]])
        return obj

    def _create_polyline_entity(
        self,
        points: List[Tuple[float, float, float]],
        closed: bool,
        layer: Optional[str] = None,
    ) -> Any:
        target_layer = (layer or self.current_layer).upper()
        pts = list(points)
        if len(pts) < 2:
            pts.append((pts[0][0] + 12.0, pts[0][1], pts[0][2]))

        seg_count = len(pts) if closed else len(pts) - 1
        all_verts: List[Tuple[float, float, float]] = []
        all_faces: List[Tuple[int, ...]] = []

        if target_layer == "A-WALL":
            thick = 2.25
            h = float(self.sysvars.get("WALLHEIGHT", 126.0))
        elif target_layer in ("A-SITE", "A-PLOT"):
            thick = 3.0
            h = 4.0
        elif target_layer == "A-FURN":
            thick = 1.5
            h = 30.0
        elif target_layer == "A-STRS":
            thick = 1.5
            h = 8.0
        else:
            thick = 1.5
            h = 2.0

        for i in range(seg_count):
            a = pts[i]
            b = pts[(i + 1) % len(pts)]
            if math.hypot(b[0] - a[0], b[1] - a[1]) < 1e-4:
                continue
            v, f = self._box_verts_faces((a[0], a[1]), (b[0], b[1]), thick, a[2], a[2] + h)
            base_idx = len(all_verts)
            all_verts.extend(v)
            all_faces.extend([tuple(idx + base_idx for idx in face) for face in f])

        # Also add a floor plate when a closed room/plot/furniture rectangle is drawn on A-SITE or A-FURN
        if closed and len(pts) >= 3 and target_layer in ("A-FURN", "A-HATCH"):
            z_top = du_to_meters(pts[0][2] + (28.0 if target_layer == "A-FURN" else 0.5))
            base_idx = len(all_verts)
            for p in pts:
                all_verts.append((du_to_meters(p[0]), du_to_meters(p[1]), z_top))
            all_faces.append(tuple(base_idx + j for j in range(len(pts))))

        obj = self._create_mesh_object("Polyline", all_verts, [], all_faces, target_layer)
        obj["acad_dxf_type"] = "LWPOLYLINE"
        obj["acad_closed"] = bool(closed)
        obj["acad_points"] = json.dumps([[float(p[0]), float(p[1])] for p in pts])
        return obj

    def _create_arc_entity(
        self,
        center: Tuple[float, float, float],
        radius: float,
        start_deg: float,
        end_deg: float,
        layer: Optional[str] = None,
        is_circle: bool = False,
    ) -> Any:
        target_layer = (layer or self.current_layer).upper()
        steps = 28 if is_circle else 16
        sweep = 360.0 if is_circle else (end_deg - start_deg)
        if not is_circle and abs(sweep) < 1e-3:
            sweep = 90.0

        pts: List[Tuple[float, float, float]] = []
        for i in range(steps + 1):
            ang = math.radians(start_deg + (sweep * i / steps))
            pts.append((center[0] + radius * math.cos(ang), center[1] + radius * math.sin(ang), center[2]))

        all_verts: List[Tuple[float, float, float]] = []
        all_faces: List[Tuple[int, ...]] = []
        h = 24.0 if (target_layer == "A-FURN" and is_circle) else 1.2
        thick = 1.0
        for i in range(len(pts) - 1):
            v, f = self._box_verts_faces((pts[i][0], pts[i][1]), (pts[i + 1][0], pts[i + 1][1]), thick, center[2], center[2] + h)
            base_idx = len(all_verts)
            all_verts.extend(v)
            all_faces.extend([tuple(idx + base_idx for idx in face) for face in f])

        obj = self._create_mesh_object("Circle" if is_circle else "Arc", all_verts, [], all_faces, target_layer)
        obj["acad_dxf_type"] = "CIRCLE" if is_circle else "ARC"
        obj["acad_center"] = [float(center[0]), float(center[1]), float(center[2])]
        obj["acad_radius"] = float(radius)
        obj["acad_start_angle"] = float(start_deg % 360.0)
        obj["acad_end_angle"] = float((start_deg + sweep) % 360.0)
        return obj

    def _create_text_entity(
        self,
        text_value: str,
        position: Tuple[float, float, float],
        height_du: float,
        rotation_deg: float = 0.0,
        justify: str = "MC",
        layer: Optional[str] = None,
    ) -> Any:
        target_layer = (layer or self.current_layer).upper()
        clean_text = (text_value or "").replace("%%u", "").replace("%%U", "").strip() or "TEXT"
        curve = self.bpy.data.curves.new(name="TextCurve", type="FONT")
        curve.body = clean_text
        curve.size = max(du_to_meters(height_du), 0.05)
        curve.extrude = 0.008
        if "C" in justify.upper():
            curve.align_x = "CENTER"
        elif "R" in justify.upper():
            curve.align_x = "RIGHT"
        else:
            curve.align_x = "LEFT"
        if "M" in justify.upper():
            curve.align_y = "CENTER"
        else:
            curve.align_y = "BOTTOM"

        obj = self.bpy.data.objects.new(f"Text_{clean_text[:18]}", curve)
        obj.location = (du_to_meters(position[0]), du_to_meters(position[1]), du_to_meters(position[2] + 1.0))
        obj.rotation_euler = (0.0, 0.0, math.radians(rotation_deg))
        assign_object_to_layer(self.bpy, obj, target_layer)
        self._alloc_handle(obj)
        obj["acad_dxf_type"] = "TEXT"
        obj["acad_text"] = text_value
        obj["acad_position"] = [float(position[0]), float(position[1]), float(position[2])]
        obj["acad_height"] = float(height_du)
        obj["acad_rotation"] = float(rotation_deg)
        return obj

    def _create_dimension_entity(
        self,
        p1: Tuple[float, float, float],
        p2: Tuple[float, float, float],
        dim_line_pt: Tuple[float, float, float],
        axis: str = "A",
        layer: Optional[str] = None,
    ) -> Any:
        target_layer = (layer or "A-ANNO-DIMS").upper()
        ax = (axis or "A").upper()
        if ax.startswith("H") or (ax == "A" and abs(p2[0] - p1[0]) >= abs(p2[1] - p1[1])):
            d1 = (p1[0], dim_line_pt[1], 0.0)
            d2 = (p2[0], dim_line_pt[1], 0.0)
            measurement = abs(p2[0] - p1[0])
        elif ax.startswith("V") or (ax == "A" and abs(p2[1] - p1[1]) > abs(p2[0] - p1[0])):
            d1 = (dim_line_pt[0], p1[1], 0.0)
            d2 = (dim_line_pt[0], p2[1], 0.0)
            measurement = abs(p2[1] - p1[1])
        else:
            d1 = p1
            d2 = p2
            measurement = math.hypot(p2[0] - p1[0], p2[1] - p1[1])

        verts, faces = self._box_verts_faces((d1[0], d1[1]), (d2[0], d2[1]), 0.8, 0.0, 1.0)
        # Extension lines
        for src, dst in ((p1, d1), (p2, d2)):
            if math.hypot(dst[0] - src[0], dst[1] - src[1]) > 1.0:
                ev, ef = self._box_verts_faces((src[0], src[1]), (dst[0], dst[1]), 0.5, 0.0, 0.8)
                base_idx = len(verts)
                verts.extend(ev)
                faces.extend([tuple(idx + base_idx for idx in face) for face in ef])

        obj = self._create_mesh_object("Dimension", verts, [], faces, target_layer)
        obj["acad_dxf_type"] = "DIMENSION"
        obj["acad_p1"] = [float(p1[0]), float(p1[1]), 0.0]
        obj["acad_p2"] = [float(p2[0]), float(p2[1]), 0.0]
        obj["acad_dim_line"] = [float(d1[0]), float(d1[1]), 0.0]
        obj["acad_measurement"] = float(measurement)
        obj["acad_text"] = format_feet_inches(measurement)
        return obj

    # ------------------------------------------------------------------ 3D architectural & mesh primitives

    def _create_3d_box_mesh(
        self,
        name: str,
        center_du: Tuple[float, float, float],
        size_du: Tuple[float, float, float],
        angle_deg: float = 0.0,
        layer: Optional[str] = None,
        color_hex: Optional[str] = None,
    ) -> Any:
        cx, cy, cz = du_to_meters(center_du[0]), du_to_meters(center_du[1]), du_to_meters(center_du[2])
        hx = max(du_to_meters(size_du[0]) * 0.5, 0.005)
        hy = max(du_to_meters(size_du[1]) * 0.5, 0.005)
        hz = max(du_to_meters(size_du[2]) * 0.5, 0.005)
        local_verts = [
            (-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
            (-hx, -hy, hz),  (hx, -hy, hz),  (hx, hy, hz),  (-hx, hy, hz),
        ]
        rad = math.radians(angle_deg)
        c, s = math.cos(rad), math.sin(rad)
        verts = [
            (cx + vx * c - vy * s, cy + vx * s + vy * c, cz + vz)
            for (vx, vy, vz) in local_verts
        ]
        faces = [
            (0, 3, 2, 1), (4, 5, 6, 7),
            (0, 1, 5, 4), (1, 2, 6, 5),
            (2, 3, 7, 6), (3, 0, 4, 7),
        ]
        obj = self._create_mesh_object(name, verts, [], faces, layer)
        if color_hex:
            rgba = parse_hex_color(color_hex)
            mat = ensure_material(self.bpy, f"Mat_{name}_{color_hex}", rgba=rgba)
            if obj.data.materials:
                obj.data.materials[0] = mat
            else:
                obj.data.materials.append(mat)
            obj.color = rgba
        obj["acad_dxf_type"] = "3DSOLID"
        obj["acad_position"] = [float(center_du[0]), float(center_du[1]), float(center_du[2])]
        obj["acad_size"] = [float(size_du[0]), float(size_du[1]), float(size_du[2])]
        return obj

    # ------------------------------------------------------------------ run_commands dispatcher

    def run_commands(
        self,
        title: str,
        steps: List[Dict[str, Any]],
        step_callback: Optional[Any] = None,
    ) -> Dict[str, Any]:
        """
        Execute a stage of 1..30 commands as ONE Blender undo step.
        Returns a RunCommandsReport-compatible result dictionary.
        """
        try:
            self.bpy.ops.ed.undo_push(message=f"Before AI Copilot: {title}")
        except Exception:
            pass

        step_reports: List[Dict[str, Any]] = []
        created_handles: List[str] = []
        modified_handles: List[str] = []
        erased_handles: List[str] = []
        created_summaries: List[Dict[str, Any]] = []
        warnings: List[str] = []
        last_dim_pt: Tuple[float, float, float] = (0.0, 0.0, 0.0)
        last_dim_line: Tuple[float, float, float] = (0.0, 0.0, 0.0)
        last_dim_axis: str = "H"

        for idx, step in enumerate(steps, start=1):
            raw_cmd = str(step.get("command", "") or "").strip()
            cmd = raw_cmd.upper().lstrip("_.")
            inputs = [str(x) for x in (step.get("inputs") or [])]
            note = step.get("note")

            step_created: List[Any] = []
            step_modified: List[Any] = []
            step_erased: List[str] = []
            step_error: Optional[str] = None
            auto_fixed: List[str] = []

            if cmd in ("LAYER", "HATCH", "STYLE", "DIMSTYLE", "INSERT", "ARRAY", "LINETYPE"):
                auto_fixed.append(f"typed -{cmd} instead of {cmd}")
                cmd = "-" + cmd

            try:
                self._exec_single_step(
                    cmd,
                    inputs,
                    step_created,
                    step_modified,
                    step_erased,
                    auto_fixed,
                    state={
                        "last_dim_pt": last_dim_pt,
                        "last_dim_line": last_dim_line,
                        "last_dim_axis": last_dim_axis,
                    },
                )
            except Exception as ex:
                step_error = str(ex)

            for obj in step_created:
                h = self.get_handle(obj)
                if h not in created_handles:
                    created_handles.append(h)
                created_summaries.append(self.summarize_object(obj))

            for obj in step_modified:
                h = self.get_handle(obj)
                if h not in modified_handles:
                    modified_handles.append(h)

            for h in step_erased:
                if h not in erased_handles:
                    erased_handles.append(h)

            step_reports.append({
                "step": idx,
                "command": cmd,
                "inputs": inputs,
                "note": note,
                "status": "failed" if step_error else "finished",
                "transcript": [f"{cmd}: {', '.join(inputs[:4])}"],
                "prompts": [f"{cmd}: {', '.join(inputs[:4])}"],
                "error": step_error,
                "autoFixed": [auto_fixed] if auto_fixed else None,
                "createdCount": len(step_created),
                "modifiedCount": len(step_modified),
                "erasedCount": len(step_erased),
            })

            if step_callback is not None:
                try:
                    step_callback(idx, len(steps), cmd)
                except Exception:
                    pass

            if self.watch_mode:
                self._redraw_viewport()

        self._undo_stack.append(list(created_handles))
        self.bump_revision()
        self._redraw_viewport()

        try:
            self.bpy.ops.ed.undo_push(message=f"AI Copilot: {title}")
        except Exception:
            pass

        failed_steps = [s for s in step_reports if s["status"] == "failed"]
        outcome = "completed" if not failed_steps else ("failed" if len(failed_steps) == len(steps) else "stopped")
        msg = (
            f"{title}: {len(steps)} step(s) executed in Blender 5.3 "
            f"({len(created_handles)} created, {len(modified_handles)} modified, {len(erased_handles)} erased)."
        )

        report_data = {
            "title": title,
            "outcome": outcome,
            "pace": "watch" if self.watch_mode else "fast",
            "stepsTotal": len(steps),
            "stepsRun": len(steps),
            "stepCount": len(steps),
            "completedSteps": len(steps) - len(failed_steps),
            "createdCount": len(created_handles),
            "modifiedCount": len(modified_handles),
            "erasedCount": len(erased_handles),
            "oneUndoStep": True,
            "steps": step_reports,
            "created": created_summaries[:60],
            "warnings": warnings,
            "revision": self.revision,
        }
        return {
            "success": len(failed_steps) < len(steps) or len(steps) == 0,
            "message": msg,
            "data": report_data,
            "createdIds": created_handles,
            "modifiedIds": modified_handles,
            "deletedIds": erased_handles,
            "warnings": warnings,
            "errors": [s["error"] for s in failed_steps if s.get("error")],
            "revision": self.revision,
        }

    def _exec_single_step(
        self,
        cmd: str,
        inputs: List[str],
        created: List[Any],
        modified: List[Any],
        erased: List[str],
        auto_fixed: List[str],
        state: Dict[str, Any],
    ) -> None:
        # 1. SETVAR
        if cmd == "SETVAR":
            if len(inputs) >= 2:
                var_name = inputs[0].strip().upper()
                val_str = inputs[1].strip()
                if var_name == "CLAYER":
                    self.current_layer = val_str
                    ensure_collection(self.bpy, val_str)
                try:
                    self.sysvars[var_name] = float(val_str) if "." in val_str else int(val_str)
                except ValueError:
                    self.sysvars[var_name] = val_str
            return

        # 2. -LAYER
        if cmd in ("-LAYER", "-LA"):
            i = 0
            while i < len(inputs):
                opt = inputs[i].strip().upper()
                if opt in ("M", "MAKE", "S", "SET") and i + 1 < len(inputs):
                    lname = inputs[i + 1].strip()
                    if lname:
                        ensure_collection(self.bpy, lname)
                        self.current_layer = lname
                        self.sysvars["CLAYER"] = lname
                    i += 2
                elif opt in ("N", "NEW") and i + 1 < len(inputs):
                    for lname in inputs[i + 1].split(","):
                        if lname.strip():
                            ensure_collection(self.bpy, lname.strip())
                    i += 2
                elif opt in ("C", "COLOR") and i + 2 < len(inputs):
                    rgba = parse_hex_color(inputs[i + 1].strip())
                    for lname in inputs[i + 2].split(","):
                        if lname.strip():
                            ensure_material(self.bpy, f"Mat_{lname.strip().upper()}", rgba=rgba)
                    i += 3
                else:
                    i += 1
            return

        # 3. LINE
        if cmd in ("LINE", "L"):
            pts: List[Tuple[float, float, float]] = []
            last = (0.0, 0.0, 0.0)
            for inp in inputs:
                token = inp.strip()
                if not token:
                    break
                if token.upper() == "C" and len(pts) >= 2:
                    pts.append(pts[0])
                    break
                pt = parse_point(token, last)
                if pt is not None:
                    pts.append(pt)
                    last = pt
            for j in range(len(pts) - 1):
                if math.hypot(pts[j + 1][0] - pts[j][0], pts[j + 1][1] - pts[j][1]) > 1e-4:
                    created.append(self._create_segment_entity(pts[j], pts[j + 1]))
            return

        # 4. PLINE
        if cmd in ("PLINE", "PL", "POLYLINE"):
            pts = []
            last = (0.0, 0.0, 0.0)
            closed = False
            i = 0
            while i < len(inputs):
                token = inputs[i].strip()
                if not token:
                    i += 1
                    continue
                if token.upper() in ("W", "WIDTH"):
                    i += 3
                    continue
                if token.upper() in ("C", "CLOSE"):
                    closed = True
                    break
                pt = parse_point(token, last)
                if pt is not None:
                    pts.append(pt)
                    last = pt
                i += 1
            if len(pts) >= 2:
                created.append(self._create_polyline_entity(pts, closed))
            return

        # 5. RECTANG
        if cmd in ("RECTANG", "REC", "RECTANGLE"):
            pts = []
            last = (0.0, 0.0, 0.0)
            for inp in inputs:
                pt = parse_point(inp.strip(), last)
                if pt is not None:
                    pts.append(pt)
                    last = pt
            if len(pts) >= 2:
                x1, y1, z1 = pts[0]
                x2, y2, _ = pts[1]
                corners = [(x1, y1, z1), (x2, y1, z1), (x2, y2, z1), (x1, y2, z1)]
                created.append(self._create_polyline_entity(corners, closed=True))
            return

        # 6. CIRCLE
        if cmd in ("CIRCLE", "C"):
            if len(inputs) >= 2:
                center = parse_point(inputs[0]) or (0.0, 0.0, 0.0)
                if inputs[1].strip().upper() in ("D", "DIAMETER") and len(inputs) >= 3:
                    rad = parse_length(inputs[2]) * 0.5
                else:
                    rad = parse_length(inputs[1])
                created.append(self._create_arc_entity(center, max(rad, 0.5), 0.0, 360.0, is_circle=True))
            return

        # 7. ARC
        if cmd in ("ARC", "A"):
            # Supports ["C", center, startPt, "A", sweepAngle] or [p1, p2, p3]
            if len(inputs) >= 3 and inputs[0].strip().upper() == "C":
                center = parse_point(inputs[1]) or (0.0, 0.0, 0.0)
                start_pt = parse_point(inputs[2], center) or (center[0] + 36.0, center[1], center[2])
                rad = math.hypot(start_pt[0] - center[0], start_pt[1] - center[1])
                start_ang = math.degrees(math.atan2(start_pt[1] - center[1], start_pt[0] - center[0]))
                sweep = 90.0
                for j in range(3, len(inputs)):
                    if inputs[j].strip().upper() == "A" and j + 1 < len(inputs):
                        try:
                            sweep = float(inputs[j + 1].strip())
                        except ValueError:
                            sweep = 90.0
                        break
                created.append(self._create_arc_entity(center, max(rad, 1.0), start_ang, start_ang + sweep))
            elif len(inputs) >= 3:
                p1 = parse_point(inputs[0]) or (0.0, 0.0, 0.0)
                p2 = parse_point(inputs[1], p1) or (p1[0] + 18.0, p1[1] + 18.0, p1[2])
                rad = math.hypot(p2[0] - p1[0], p2[1] - p1[1])
                ang = math.degrees(math.atan2(p2[1] - p1[1], p2[0] - p1[0]))
                created.append(self._create_arc_entity(p1, max(rad, 1.0), ang, ang + 90.0))
            return

        # 8. OFFSET
        if cmd in ("OFFSET", "O"):
            if len(inputs) >= 3:
                dist = parse_length(inputs[0])
                pick_pt = parse_point(inputs[1])
                side_pt = parse_point(inputs[2], pick_pt or (0.0, 0.0, 0.0))
                target = self._find_nearest_entity(pick_pt) if pick_pt else self._last_created_entity()
                if target is not None and side_pt is not None:
                    new_obj = self._offset_entity(target, dist, side_pt)
                    if new_obj is not None:
                        created.append(new_obj)
            return

        # 9. EXPLODE
        if cmd in ("EXPLODE", "X"):
            for inp in inputs:
                pt = parse_point(inp.strip())
                target = self._find_nearest_entity(pt) if pt else (self._last_created_entity() if inp.strip().upper() == "L" else None)
                if target is not None and target.get("acad_dxf_type") == "LWPOLYLINE":
                    raw_pts = json.loads(target.get("acad_points", "[]"))
                    closed = bool(target.get("acad_closed", False))
                    layer = target.get("acad_layer", self.current_layer)
                    seg_count = len(raw_pts) if closed else max(0, len(raw_pts) - 1)
                    for j in range(seg_count):
                        a = (raw_pts[j][0], raw_pts[j][1], 0.0)
                        b = (raw_pts[(j + 1) % len(raw_pts)][0], raw_pts[(j + 1) % len(raw_pts)][1], 0.0)
                        created.append(self._create_segment_entity(a, b, layer=layer))
                    erased.append(self.get_handle(target))
                    self.bpy.data.objects.remove(target, do_unlink=True)
            return

        # 10. BREAK
        if cmd in ("BREAK", "BR"):
            if len(inputs) >= 2:
                pick_pt = parse_point(inputs[0])
                if len(inputs) >= 4 and inputs[1].strip().upper() == "F":
                    bp1 = parse_point(inputs[2])
                    bp2 = parse_point(inputs[3], bp1 or (0.0, 0.0, 0.0))
                else:
                    bp1 = pick_pt
                    bp2 = parse_point(inputs[1], bp1 or (0.0, 0.0, 0.0))
                target = self._find_nearest_line(pick_pt or bp1)
                if target is not None and bp1 is not None and bp2 is not None:
                    self._break_line_entity(target, bp1, bp2, created, erased)
            return

        # 11. TEXT
        if cmd in ("TEXT", "-MTEXT", "DTEXT"):
            # Supports ["J", "MC", centerPt, height, rot, text, ""] or [pt, height, rot, text]
            idx = 0
            justify = "BL"
            if len(inputs) >= 2 and inputs[0].strip().upper() in ("J", "JUSTIFY"):
                justify = inputs[1].strip().upper()
                idx = 2
            elif len(inputs) >= 1 and inputs[0].strip().upper() in ("MC", "ML", "MR", "C", "M", "R", "TL", "TC", "TR", "BL", "BC", "BR"):
                justify = inputs[0].strip().upper()
                idx = 1
            pos = parse_point(inputs[idx]) if idx < len(inputs) else (0.0, 0.0, 0.0)
            pos = pos or (0.0, 0.0, 0.0)
            height = parse_length(inputs[idx + 1]) if idx + 1 < len(inputs) else 9.0
            try:
                rot = float(inputs[idx + 2].strip()) if idx + 2 < len(inputs) else 0.0
            except ValueError:
                rot = 0.0
            txt = inputs[idx + 3] if idx + 3 < len(inputs) else (inputs[-1] if inputs else "ROOM")
            if not txt and idx + 2 < len(inputs):
                txt = inputs[idx + 2]
            created.append(self._create_text_entity(txt, pos, max(height, 2.0), rot, justify))
            return

        # 12. DIMLINEAR / DIMALIGNED
        if cmd in ("DIMLINEAR", "DLI", "DIMALIGNED", "DAL"):
            if len(inputs) >= 3:
                p1 = parse_point(inputs[0]) or (0.0, 0.0, 0.0)
                p2 = parse_point(inputs[1], p1) or (p1[0] + 120.0, p1[1], 0.0)
                axis = "A"
                dim_idx = 2
                if inputs[2].strip().upper() in ("H", "V", "HORIZONTAL", "VERTICAL"):
                    axis = inputs[2].strip().upper()[0]
                    dim_idx = 3
                dim_pt = parse_point(inputs[dim_idx], p2) if dim_idx < len(inputs) else (p2[0], p2[1] - 42.0, 0.0)
                dim_pt = dim_pt or (p2[0], p2[1] - 42.0, 0.0)
                obj = self._create_dimension_entity(p1, p2, dim_pt, axis)
                created.append(obj)
                state["last_dim_pt"] = p2
                state["last_dim_line"] = dim_pt
                state["last_dim_axis"] = axis
            return

        # 13. DIMCONTINUE
        if cmd in ("DIMCONTINUE", "DCO"):
            prev = state.get("last_dim_pt", (0.0, 0.0, 0.0))
            dim_line = state.get("last_dim_line", (0.0, -42.0, 0.0))
            axis = state.get("last_dim_axis", "H")
            for inp in inputs:
                if not inp.strip():
                    break
                nxt = parse_point(inp.strip(), prev)
                if nxt is not None:
                    created.append(self._create_dimension_entity(prev, nxt, dim_line, axis))
                    prev = nxt
            state["last_dim_pt"] = prev
            return

        # 14. -ARRAY
        if cmd == "-ARRAY":
            # Rectangular array of last object
            target = self._last_created_entity()
            if target is not None:
                rows, cols, spacing = 1, 1, 10.0
                for j, tok in enumerate(inputs):
                    if tok.strip().upper() == "R" and j + 3 < len(inputs):
                        try:
                            rows = max(1, int(float(inputs[j + 1])))
                            cols = max(1, int(float(inputs[j + 2])))
                            spacing = parse_length(inputs[j + 3])
                        except ValueError:
                            pass
                        break
                start = target.get("acad_start", [0.0, 0.0, 0.0])
                end = target.get("acad_end", [36.0, 0.0, 0.0])
                layer = target.get("acad_layer", self.current_layer)
                count = max(rows, cols)
                step_dx = spacing if cols > rows else 0.0
                step_dy = spacing if rows >= cols else 0.0
                for k in range(1, count):
                    p1 = (start[0] + step_dx * k, start[1] + step_dy * k, k * 6.5 if layer == "A-STRS" else start[2])
                    p2 = (end[0] + step_dx * k, end[1] + step_dy * k, k * 6.5 if layer == "A-STRS" else end[2])
                    created.append(self._create_segment_entity(p1, p2, layer=layer))
            return

        # 15. -HATCH
        if cmd in ("-HATCH", "-BHATCH"):
            pattern = inputs[1].strip() if len(inputs) > 1 else "SOLID"
            pt = None
            for inp in reversed(inputs):
                pt = parse_point(inp.strip())
                if pt is not None:
                    break
            pt = pt or (60.0, 60.0, 0.0)
            obj = self._create_3d_box_mesh(f"Hatch_{pattern}", (pt[0], pt[1], 0.25), (48.0, 48.0, 0.5), layer="A-HATCH")
            obj["acad_dxf_type"] = "HATCH"
            obj["acad_pattern"] = pattern
            created.append(obj)
            return

        # 16. ERASE
        if cmd in ("ERASE", "E"):
            for inp in inputs:
                tok = inp.strip().upper()
                if not tok:
                    continue
                if tok == "ALL":
                    for obj in list(self.bpy.context.scene.objects):
                        if obj.type in ("MESH", "FONT", "CURVE"):
                            erased.append(self.get_handle(obj))
                            self.bpy.data.objects.remove(obj, do_unlink=True)
                elif tok == "L":
                    last_obj = self._last_created_entity()
                    if last_obj is not None:
                        erased.append(self.get_handle(last_obj))
                        self.bpy.data.objects.remove(last_obj, do_unlink=True)
                else:
                    obj = self.find_by_handle(tok) or self._find_nearest_entity(parse_point(tok))
                    if obj is not None:
                        erased.append(self.get_handle(obj))
                        self.bpy.data.objects.remove(obj, do_unlink=True)
            return

        # 17. ZOOM / UCS / FILLET / TRIM / COPY / MOVE
        if cmd in ("ZOOM", "Z", "UCS", "REGEN", "PAN"):
            return

        if cmd in ("FILLET", "F", "TRIM", "TR"):
            return

        if cmd in ("COPY", "CP", "CO", "MOVE", "M"):
            if len(inputs) >= 2:
                p_from = parse_point(inputs[-2])
                p_to = parse_point(inputs[-1], p_from or (0.0, 0.0, 0.0))
                target = self._last_created_entity()
                if target is not None and p_from is not None and p_to is not None:
                    dx = du_to_meters(p_to[0] - p_from[0])
                    dy = du_to_meters(p_to[1] - p_from[1])
                    dz = du_to_meters(p_to[2] - p_from[2])
                    if cmd.startswith("M"):
                        target.location = (target.location.x + dx, target.location.y + dy, target.location.z + dz)
                        modified.append(target)
                    else:
                        dup = target.copy()
                        if target.data:
                            dup.data = target.data.copy()
                        dup.location = (target.location.x + dx, target.location.y + dy, target.location.z + dz)
                        assign_object_to_layer(self.bpy, dup, target.get("acad_layer", self.current_layer))
                        dup["acad_handle"] = ""
                        self._alloc_handle(dup)
                        created.append(dup)
            return

        # ==================================================================
        # NATIVE 3D BLENDER COMMANDS
        # ==================================================================

        # 18. WALL_3D: ["fromX,fromY", "toX,toY", "thickness?", "height?", "name?"]
        if cmd == "WALL_3D":
            p1 = parse_point(inputs[0]) if len(inputs) >= 1 else (0.0, 0.0, 0.0)
            p2 = parse_point(inputs[1], p1 or (0.0, 0.0, 0.0)) if len(inputs) >= 2 else (120.0, 0.0, 0.0)
            p1 = p1 or (0.0, 0.0, 0.0)
            p2 = p2 or (120.0, 0.0, 0.0)
            thick = parse_length(inputs[2]) if len(inputs) >= 3 else 9.0
            height = parse_length(inputs[3]) if len(inputs) >= 4 else float(self.sysvars.get("WALLHEIGHT", 126.0))
            obj = self._create_segment_entity(
                p1, p2, layer="A-WALL", dxf_type="LINE",
                thickness_override=max(thick, 1.0), height_override=max(height, 12.0),
            )
            if len(inputs) >= 5 and inputs[4].strip():
                obj.name = inputs[4].strip()
            created.append(obj)
            return

        # 19. SLAB_3D: ["x,y", "width,depth", "thickness?", "elevation?", "name?"]
        if cmd == "SLAB_3D":
            origin = parse_point(inputs[0]) if len(inputs) >= 1 else (0.0, 0.0, 0.0)
            size = parse_point(inputs[1]) if len(inputs) >= 2 else (360.0, 480.0, 0.0)
            origin = origin or (0.0, 0.0, 0.0)
            size = size or (360.0, 480.0, 0.0)
            thick = parse_length(inputs[2]) if len(inputs) >= 3 else 6.0
            elev = parse_length(inputs[3]) if len(inputs) >= 4 else origin[2]
            name = inputs[4].strip() if len(inputs) >= 5 and inputs[4].strip() else "FloorSlab"
            center = (origin[0] + size[0] * 0.5, origin[1] + size[1] * 0.5, elev - thick * 0.5)
            obj = self._create_3d_box_mesh(name, center, (size[0], size[1], thick), layer="A-SLAB")
            created.append(obj)
            return

        # 20. DOOR_3D: ["x,y", "width?", "height?", "angle?", "swing?"]
        if cmd == "DOOR_3D":
            pos = parse_point(inputs[0]) if len(inputs) >= 1 else (0.0, 0.0, 0.0)
            pos = pos or (0.0, 0.0, 0.0)
            width = parse_length(inputs[1]) if len(inputs) >= 2 else 36.0
            height = parse_length(inputs[2]) if len(inputs) >= 3 else 84.0
            try:
                angle = float(inputs[3].strip()) if len(inputs) >= 4 else 0.0
            except ValueError:
                angle = 0.0
            rad = math.radians(angle)
            end_pt = (pos[0] + width * math.cos(rad), pos[1] + width * math.sin(rad), pos[2])
            leaf = self._create_segment_entity(pos, end_pt, layer="A-DOOR", thickness_override=2.0, height_override=height)
            arc = self._create_arc_entity(pos, width, angle, angle + 90.0, layer="A-DOOR")
            created.extend([leaf, arc])
            return

        # 21. WINDOW_3D: ["x,y", "width?", "sill?", "head?", "angle?"]
        if cmd == "WINDOW_3D":
            pos = parse_point(inputs[0]) if len(inputs) >= 1 else (0.0, 0.0, 0.0)
            pos = pos or (0.0, 0.0, 0.0)
            width = parse_length(inputs[1]) if len(inputs) >= 2 else 48.0
            sill = parse_length(inputs[2]) if len(inputs) >= 3 else 36.0
            head = parse_length(inputs[3]) if len(inputs) >= 4 else 84.0
            try:
                angle = float(inputs[4].strip()) if len(inputs) >= 5 else 0.0
            except ValueError:
                angle = 0.0
            rad = math.radians(angle)
            end_pt = (pos[0] + width * math.cos(rad), pos[1] + width * math.sin(rad), pos[2])
            win = self._create_segment_entity(
                pos, end_pt, layer="A-GLAZ",
                thickness_override=2.0, height_override=max(head - sill, 12.0), z_bottom_override=pos[2] + sill,
            )
            created.append(win)
            return

        # 22. STAIR_3D: ["x,y", "width,depth", "floorHeight?", "risers?"]
        if cmd == "STAIR_3D":
            pos = parse_point(inputs[0]) if len(inputs) >= 1 else (0.0, 0.0, 0.0)
            size = parse_point(inputs[1]) if len(inputs) >= 2 else (90.0, 132.0, 0.0)
            pos = pos or (0.0, 0.0, 0.0)
            size = size or (90.0, 132.0, 0.0)
            fl_h = parse_length(inputs[2]) if len(inputs) >= 3 else 126.0
            try:
                risers = max(4, int(float(inputs[3]))) if len(inputs) >= 4 else 18
            except ValueError:
                risers = 18
            half = risers // 2
            riser_h = fl_h / risers
            flight_w = size[0] * 0.46
            tread_d = (size[1] - flight_w) / max(half, 1)
            for k in range(half):
                step_z = pos[2] + (k + 0.5) * riser_h
                step_y = pos[1] + (k + 0.5) * tread_d
                created.append(self._create_3d_box_mesh(
                    f"StairFlight1_{k+1}",
                    (pos[0] + flight_w * 0.5, step_y, step_z),
                    (flight_w, tread_d, riser_h),
                    layer="A-STRS",
                ))
            # Mid-landing
            landing_z = pos[2] + half * riser_h
            created.append(self._create_3d_box_mesh(
                "StairLanding",
                (pos[0] + size[0] * 0.5, pos[1] + size[1] - flight_w * 0.5, landing_z - riser_h * 0.5),
                (size[0], flight_w, riser_h),
                layer="A-STRS",
            ))
            for k in range(risers - half):
                step_z = landing_z + (k + 0.5) * riser_h
                step_y = pos[1] + size[1] - flight_w - (k + 0.5) * tread_d
                created.append(self._create_3d_box_mesh(
                    f"StairFlight2_{k+1}",
                    (pos[0] + size[0] - flight_w * 0.5, step_y, step_z),
                    (flight_w, tread_d, riser_h),
                    layer="A-STRS",
                ))
            return

        # 23. ROOF_3D: ["x,y", "width,depth", "elevation?", "kind?", "parapet?"]
        if cmd == "ROOF_3D":
            pos = parse_point(inputs[0]) if len(inputs) >= 1 else (0.0, 0.0, 0.0)
            size = parse_point(inputs[1]) if len(inputs) >= 2 else (312.0, 408.0, 0.0)
            pos = pos or (0.0, 0.0, 0.0)
            size = size or (312.0, 408.0, 0.0)
            elev = parse_length(inputs[2]) if len(inputs) >= 3 else 126.0
            parapet = parse_length(inputs[4]) if len(inputs) >= 5 else 36.0
            slab = self._create_3d_box_mesh(
                "RoofSlab",
                (pos[0] + size[0] * 0.5, pos[1] + size[1] * 0.5, elev + 3.0),
                (size[0] + 24.0, size[1] + 24.0, 6.0),
                layer="A-ROOF",
            )
            created.append(slab)
            if parapet > 0:
                corners = [
                    (pos[0], pos[1], elev + 6.0),
                    (pos[0] + size[0], pos[1], elev + 6.0),
                    (pos[0] + size[0], pos[1] + size[1], elev + 6.0),
                    (pos[0], pos[1] + size[1], elev + 6.0),
                ]
                for j in range(4):
                    a = corners[j]
                    b = corners[(j + 1) % 4]
                    created.append(self._create_segment_entity(
                        a, b, layer="A-ROOF", thickness_override=4.5, height_override=parapet, z_bottom_override=elev + 6.0
                    ))
            return

        # 24. FURNITURE_3D: ["kind", "x,y", "width,depth?", "angle?"]
        if cmd == "FURNITURE_3D":
            kind = (inputs[0].strip().lower() if len(inputs) >= 1 else "bed")
            pos = parse_point(inputs[1]) if len(inputs) >= 2 else (60.0, 60.0, 0.0)
            pos = pos or (60.0, 60.0, 0.0)
            default_sizes = {
                "bed": (72.0, 78.0, 24.0),
                "double_bed": (72.0, 78.0, 24.0),
                "single_bed": (42.0, 78.0, 22.0),
                "sofa": (84.0, 36.0, 32.0),
                "dining": (60.0, 36.0, 30.0),
                "kitchen": (96.0, 24.0, 34.0),
                "toilet": (18.0, 27.0, 18.0),
                "wardrobe": (60.0, 24.0, 84.0),
                "car": (72.0, 168.0, 58.0),
                "table": (48.0, 30.0, 30.0),
            }
            def_w, def_d, def_h = default_sizes.get(kind, (48.0, 48.0, 30.0))
            sz = parse_point(inputs[2]) if len(inputs) >= 3 else None
            w = sz[0] if sz else def_w
            d = sz[1] if sz else def_d
            h = sz[2] if (sz and sz[2] > 0) else def_h
            try:
                ang = float(inputs[3].strip()) if len(inputs) >= 4 else 0.0
            except ValueError:
                ang = 0.0
            obj = self._create_3d_box_mesh(
                f"Furn_{kind.capitalize()}",
                (pos[0], pos[1], pos[2] + h * 0.5),
                (w, d, h),
                angle_deg=ang,
                layer="A-FURN",
            )
            obj["acad_dxf_type"] = "INSERT"
            obj["acad_block_name"] = kind.upper()
            created.append(obj)
            return

        # 25. MESH_CUBE / MESH_CYLINDER / MESH_SPHERE / MESH_PLANE / MESH_CONE / MESH_TORUS / MESH_MONKEY
        if cmd.startswith("MESH_"):
            prim = cmd[5:]
            pos = parse_point(inputs[0]) if len(inputs) >= 1 else (0.0, 0.0, 0.0)
            pos = pos or (0.0, 0.0, 0.0)
            sz = parse_point(inputs[1]) if len(inputs) >= 2 else (39.37, 39.37, 39.37)
            sz = sz or (39.37, 39.37, 39.37)
            sx = sz[0] if sz[0] > 0 else 39.37
            sy = sz[1] if sz[1] > 0 else sx
            sz_z = sz[2] if sz[2] > 0 else sx
            name = inputs[2].strip() if len(inputs) >= 3 and inputs[2].strip() else f"Mesh_{prim.capitalize()}"
            color_hex = inputs[3].strip() if len(inputs) >= 4 and inputs[3].strip() else None

            if prim in ("CUBE", "BOX", "PLANE"):
                if prim == "PLANE":
                    sz_z = 0.5
                obj = self._create_3d_box_mesh(name, pos, (sx, sy, sz_z), color_hex=color_hex)
                created.append(obj)
            else:
                loc_m = (du_to_meters(pos[0]), du_to_meters(pos[1]), du_to_meters(pos[2]))
                if prim == "CYLINDER":
                    self.bpy.ops.mesh.primitive_cylinder_add(radius=du_to_meters(sx * 0.5), depth=du_to_meters(sz_z), location=loc_m)
                elif prim in ("SPHERE", "UV_SPHERE"):
                    self.bpy.ops.mesh.primitive_uv_sphere_add(radius=du_to_meters(sx * 0.5), location=loc_m)
                elif prim == "CONE":
                    self.bpy.ops.mesh.primitive_cone_add(radius1=du_to_meters(sx * 0.5), depth=du_to_meters(sz_z), location=loc_m)
                elif prim == "TORUS":
                    self.bpy.ops.mesh.primitive_torus_add(major_radius=du_to_meters(sx * 0.5), minor_radius=du_to_meters(sy * 0.2), location=loc_m)
                elif prim in ("MONKEY", "SUZANNE"):
                    self.bpy.ops.mesh.primitive_monkey_add(size=du_to_meters(sx), location=loc_m)
                else:
                    self.bpy.ops.mesh.primitive_cube_add(size=du_to_meters(sx), location=loc_m)
                obj = self.bpy.context.active_object
                obj.name = name
                assign_object_to_layer(self.bpy, obj, self.current_layer)
                if color_hex:
                    rgba = parse_hex_color(color_hex)
                    mat = ensure_material(self.bpy, f"Mat_{name}", rgba=rgba)
                    if obj.data.materials:
                        obj.data.materials[0] = mat
                    else:
                        obj.data.materials.append(mat)
                    obj.color = rgba
                self._alloc_handle(obj)
                obj["acad_dxf_type"] = "3DSOLID"
                created.append(obj)
            return

        # 26. MATERIAL: ["objectOrLayer", "hexColor", "roughness?", "metallic?"]
        if cmd == "MATERIAL":
            if len(inputs) >= 2:
                target_name = inputs[0].strip()
                rgba = parse_hex_color(inputs[1].strip())
                rough = float(inputs[2]) if len(inputs) >= 3 else 0.5
                metal = float(inputs[3]) if len(inputs) >= 4 else 0.0
                mat = ensure_material(self.bpy, f"Mat_{target_name}", rgba=rgba, roughness=rough, metallic=metal)
                obj = self.find_by_handle(target_name) or self.bpy.data.objects.get(target_name)
                if obj is not None and hasattr(obj.data, "materials") and obj.data is not None:
                    if obj.data.materials:
                        obj.data.materials[0] = mat
                    else:
                        obj.data.materials.append(mat)
                    obj.color = rgba
                    modified.append(obj)
                else:
                    for o in self.bpy.context.scene.objects:
                        if str(o.get("acad_layer", "")).upper() == target_name.upper():
                            if hasattr(o.data, "materials") and o.data is not None:
                                if o.data.materials:
                                    o.data.materials[0] = mat
                                else:
                                    o.data.materials.append(mat)
                                o.color = rgba
                                modified.append(o)
            return

        # 27. MODIFIER: ["objectName", "BEVEL|SUBSURF|SOLIDIFY|ARRAY|WIREFRAME", "param1?", "param2?"]
        if cmd == "MODIFIER":
            if len(inputs) >= 2:
                obj = self.find_by_handle(inputs[0]) or self.bpy.data.objects.get(inputs[0]) or self._last_created_entity()
                mod_kind = inputs[1].strip().upper()
                if obj is not None and obj.type == "MESH":
                    mod = obj.modifiers.new(name=mod_kind, type=mod_kind)
                    if mod_kind == "BEVEL" and len(inputs) >= 3:
                        mod.width = du_to_meters(parse_length(inputs[2]))
                        mod.segments = int(inputs[3]) if len(inputs) >= 4 else 3
                    elif mod_kind == "SUBSURF" and len(inputs) >= 3:
                        mod.levels = int(inputs[2])
                    elif mod_kind == "SOLIDIFY" and len(inputs) >= 3:
                        mod.thickness = du_to_meters(parse_length(inputs[2]))
                    modified.append(obj)
            return

        # 28. LIGHT: ["SUN|POINT|AREA|SPOT", "x,y,z", "energy?", "color?"]
        if cmd == "LIGHT":
            ltype = (inputs[0].strip().upper() if len(inputs) >= 1 else "SUN")
            if ltype not in ("SUN", "POINT", "AREA", "SPOT"):
                ltype = "SUN"
            pos = parse_point(inputs[1]) if len(inputs) >= 2 else (180.0, -180.0, 360.0)
            pos = pos or (180.0, -180.0, 360.0)
            energy = float(inputs[2]) if len(inputs) >= 3 else (4.0 if ltype == "SUN" else 500.0)
            ldata = self.bpy.data.lights.new(name=f"Light_{ltype}", type=ltype)
            ldata.energy = energy
            if len(inputs) >= 4:
                rgba = parse_hex_color(inputs[3])
                ldata.color = (rgba[0], rgba[1], rgba[2])
            lobj = self.bpy.data.objects.new(name=f"Light_{ltype}", object_data=ldata)
            lobj.location = (du_to_meters(pos[0]), du_to_meters(pos[1]), du_to_meters(pos[2] if pos[2] != 0 else 300.0))
            if ltype == "SUN":
                lobj.rotation_euler = (math.radians(48.0), math.radians(15.0), math.radians(32.0))
            assign_object_to_layer(self.bpy, lobj, "0")
            self._alloc_handle(lobj)
            created.append(lobj)
            return

        # 29. CAMERA: ["x,y,z", "targetX,targetY,targetZ", "lens?"]
        if cmd == "CAMERA":
            pos = parse_point(inputs[0]) if len(inputs) >= 1 else (180.0, -320.0, 280.0)
            target = parse_point(inputs[1]) if len(inputs) >= 2 else (180.0, 200.0, 40.0)
            pos = pos or (180.0, -320.0, 280.0)
            target = target or (180.0, 200.0, 40.0)
            cam = self.bpy.context.scene.camera
            if cam is None:
                cdata = self.bpy.data.cameras.new("Camera")
                cam = self.bpy.data.objects.new("Camera", cdata)
                self.bpy.context.scene.collection.objects.link(cam)
                self.bpy.context.scene.camera = cam
            cam.location = (du_to_meters(pos[0]), du_to_meters(pos[1]), du_to_meters(pos[2] if pos[2] != 0 else 280.0))
            if len(inputs) >= 3:
                try:
                    cam.data.lens = float(inputs[2])
                except ValueError:
                    pass
            self._aim_object_at(cam, (du_to_meters(target[0]), du_to_meters(target[1]), du_to_meters(target[2])))
            modified.append(cam)
            return

        # 30. DELETE / REMOVE / CLEAR
        if cmd in ("DELETE", "REMOVE", "CLEAR", "CLEAR_SCENE"):
            if cmd in ("CLEAR", "CLEAR_SCENE") or any(x.strip().upper() == "ALL" for x in inputs):
                for obj in list(self.bpy.context.scene.objects):
                    if obj.type in ("MESH", "FONT", "CURVE", "EMPTY"):
                        erased.append(self.get_handle(obj))
                        self.bpy.data.objects.remove(obj, do_unlink=True)
            else:
                targets = list(inputs) if inputs else [o.name for o in self.bpy.context.selected_objects]
                for tok in targets:
                    t = tok.strip()
                    if not t:
                        continue
                    obj = self.find_by_handle(t) or self.bpy.data.objects.get(t)
                    if obj is not None:
                        erased.append(self.get_handle(obj))
                        self.bpy.data.objects.remove(obj, do_unlink=True)
            return

        # 31. SHADE_SMOOTH
        if cmd in ("SHADE_SMOOTH", "SMOOTH"):
            targets = [self.find_by_handle(x) or self.bpy.data.objects.get(x) for x in inputs if x.strip()]
            if not targets:
                targets = [o for o in self.bpy.context.scene.objects if o.type == "MESH"]
            for obj in targets:
                if obj is not None and obj.type == "MESH" and obj.data is not None:
                    for poly in obj.data.polygons:
                        poly.use_smooth = True
                    modified.append(obj)
            return

        # 32. BPY / PYTHON / direct bpy.* statement execution inside Blender 5.3
        if cmd in ("BPY", "PYTHON", "EXEC", "SCRIPT") or cmd.startswith("BPY.") or "(" in cmd or "=" in cmd:
            if cmd in ("BPY", "PYTHON", "EXEC", "SCRIPT"):
                code = "\n".join(inputs)
            else:
                # The AI put the python statement in command (or command + inputs)
                raw_stmt = cmd if not inputs else (cmd + "\n" + "\n".join(inputs))
                code = raw_stmt
            forbidden = ("os.system", "subprocess", "shutil.rmtree", "wm.quit_blender")
            for f in forbidden:
                if f in code:
                    raise ValueError(f"Blocked unsafe statement '{f}' in BPY command.")
            before_objs = {o.name: o for o in self.bpy.context.scene.objects}
            before_handles = {o.name: self.get_handle(o) for o in self.bpy.context.scene.objects}
            import bmesh
            import mathutils
            env = {
                "bpy": self.bpy,
                "bmesh": bmesh,
                "mathutils": mathutils,
                "Vector": mathutils.Vector,
                "Euler": mathutils.Euler,
                "Matrix": mathutils.Matrix,
                "Quaternion": mathutils.Quaternion,
                "Color": mathutils.Color,
                "C": self.bpy.context,
                "D": self.bpy.data,
                "O": self.bpy.ops,
                "scene": self.bpy.context.scene,
                "math": math,
                "engine": self,
                "ensure_collection": lambda name: ensure_collection(self.bpy, name),
                "ensure_material": lambda name, rgba=(0.8, 0.8, 0.8, 1.0), roughness=0.5, metallic=0.0: ensure_material(self.bpy, name, rgba, roughness, metallic),
                "assign_object_to_layer": lambda obj, layer: assign_object_to_layer(self.bpy, obj, layer),
                "du_to_meters": du_to_meters,
                "meters_to_du": meters_to_du,
                "parse_length": parse_length,
                "parse_point": parse_point,
            }
            exec(code, env, env)
            after_objs = {o.name: o for o in self.bpy.context.scene.objects}
            for name, new_obj in after_objs.items():
                if name not in before_objs:
                    self._alloc_handle(new_obj)
                    if not new_obj.get("acad_layer"):
                        new_obj["acad_layer"] = self.current_layer
                    if not new_obj.get("acad_dxf_type"):
                        new_obj["acad_dxf_type"] = "3DSOLID" if new_obj.type == "MESH" else new_obj.type
                    created.append(new_obj)
            for name, old_handle in before_handles.items():
                if name not in after_objs:
                    erased.append(old_handle)
            return

        # Fallback: create a marker or note if an unknown command was sent
        raise ValueError(f"Unsupported command '{cmd}'. Use BPY (for any Blender Python bpy/bmesh/mathutils script), WALL_3D, SLAB_3D, DOOR_3D, WINDOW_3D, STAIR_3D, ROOF_3D, FURNITURE_3D, MESH_*, MATERIAL, MODIFIER, LIGHT, CAMERA, LINE, PLINE, RECTANG, OFFSET, BREAK, ARC, TEXT, or DIMLINEAR.")

    # ------------------------------------------------------------------ CAD editing helpers

    def _last_created_entity(self) -> Optional[Any]:
        objs = [o for o in self.bpy.context.scene.objects if o.get("acad_handle")]
        return objs[-1] if objs else None

    def _find_nearest_entity(self, pt: Optional[Tuple[float, float, float]]) -> Optional[Any]:
        if pt is None:
            return self._last_created_entity()
        best_obj = None
        best_dist = float("inf")
        px, py = pt[0], pt[1]
        for obj in self.bpy.context.scene.objects:
            if not obj.get("acad_handle"):
                continue
            d = self._distance_to_entity_2d(obj, px, py)
            if d < best_dist:
                best_dist = d
                best_obj = obj
        return best_obj

    def _find_nearest_line(self, pt: Optional[Tuple[float, float, float]]) -> Optional[Any]:
        if pt is None:
            return self._last_created_entity()
        best_obj = None
        best_dist = float("inf")
        px, py = pt[0], pt[1]
        for obj in self.bpy.context.scene.objects:
            if obj.get("acad_dxf_type") not in ("LINE", "LWPOLYLINE"):
                continue
            d = self._distance_to_entity_2d(obj, px, py)
            if d < best_dist:
                best_dist = d
                best_obj = obj
        return best_obj

    def _distance_to_entity_2d(self, obj: Any, px: float, py: float) -> float:
        dtype = obj.get("acad_dxf_type")
        if dtype == "LINE":
            s = obj.get("acad_start", [0.0, 0.0, 0.0])
            e = obj.get("acad_end", [0.0, 0.0, 0.0])
            return self._point_segment_dist(px, py, s[0], s[1], e[0], e[1])
        if dtype == "LWPOLYLINE":
            pts = json.loads(obj.get("acad_points", "[]"))
            closed = bool(obj.get("acad_closed", False))
            if not pts:
                return 1e9
            best = 1e9
            cnt = len(pts) if closed else max(0, len(pts) - 1)
            for i in range(cnt):
                a = pts[i]
                b = pts[(i + 1) % len(pts)]
                best = min(best, self._point_segment_dist(px, py, a[0], a[1], b[0], b[1]))
            return best
        pos = obj.get("acad_position") or obj.get("acad_center")
        if pos:
            return math.hypot(px - pos[0], py - pos[1])
        return math.hypot(px - meters_to_du(obj.location.x), py - meters_to_du(obj.location.y))

    @staticmethod
    def _point_segment_dist(px: float, py: float, x1: float, y1: float, x2: float, y2: float) -> float:
        dx, dy = x2 - x1, y2 - y1
        l2 = dx * dx + dy * dy
        if l2 < 1e-9:
            return math.hypot(px - x1, py - y1)
        t = max(0.0, min(1.0, ((px - x1) * dx + (py - y1) * dy) / l2))
        proj_x = x1 + t * dx
        proj_y = y1 + t * dy
        return math.hypot(px - proj_x, py - proj_y)

    def _offset_entity(self, target: Any, dist: float, side_pt: Tuple[float, float, float]) -> Optional[Any]:
        dtype = target.get("acad_dxf_type")
        layer = target.get("acad_layer", self.current_layer)
        if dtype == "LINE":
            s = target.get("acad_start", [0.0, 0.0, 0.0])
            e = target.get("acad_end", [0.0, 0.0, 0.0])
            dx, dy = e[0] - s[0], e[1] - s[1]
            length = math.hypot(dx, dy)
            if length < 1e-6:
                return None
            nx, ny = -dy / length, dx / length
            dot = (side_pt[0] - s[0]) * nx + (side_pt[1] - s[1]) * ny
            sign = 1.0 if dot >= 0 else -1.0
            ox, oy = nx * dist * sign, ny * dist * sign
            return self._create_segment_entity((s[0] + ox, s[1] + oy, s[2]), (e[0] + ox, e[1] + oy, e[2]), layer=layer)

        if dtype == "LWPOLYLINE":
            pts = json.loads(target.get("acad_points", "[]"))
            closed = bool(target.get("acad_closed", False))
            if len(pts) < 2:
                return None
            xs = [p[0] for p in pts]
            ys = [p[1] for p in pts]
            min_x, max_x = min(xs), max(xs)
            min_y, max_y = min(ys), max(ys)
            cx, cy = (min_x + max_x) * 0.5, (min_y + max_y) * 0.5
            inside = (min_x < side_pt[0] < max_x) and (min_y < side_pt[1] < max_y)
            sign = -1.0 if inside else 1.0
            new_pts: List[Tuple[float, float, float]] = []
            for p in pts:
                sx = 1.0 if p[0] >= cx else -1.0
                sy = 1.0 if p[1] >= cy else -1.0
                new_pts.append((p[0] + sx * dist * sign, p[1] + sy * dist * sign, 0.0))
            return self._create_polyline_entity(new_pts, closed, layer=layer)
        return None

    def _break_line_entity(
        self,
        target: Any,
        bp1: Tuple[float, float, float],
        bp2: Tuple[float, float, float],
        created: List[Any],
        erased: List[str],
    ) -> None:
        if target.get("acad_dxf_type") != "LINE":
            return
        s = target.get("acad_start", [0.0, 0.0, 0.0])
        e = target.get("acad_end", [0.0, 0.0, 0.0])
        layer = target.get("acad_layer", self.current_layer)
        dx, dy = e[0] - s[0], e[1] - s[1]
        l2 = dx * dx + dy * dy
        if l2 < 1e-6:
            return
        t1 = max(0.0, min(1.0, ((bp1[0] - s[0]) * dx + (bp1[1] - s[1]) * dy) / l2))
        t2 = max(0.0, min(1.0, ((bp2[0] - s[0]) * dx + (bp2[1] - s[1]) * dy) / l2))
        ta, tb = min(t1, t2), max(t1, t2)
        p_a = (s[0] + ta * dx, s[1] + ta * dy, s[2])
        p_b = (s[0] + tb * dx, s[1] + tb * dy, s[2])

        if math.hypot(p_a[0] - s[0], p_a[1] - s[1]) > 0.5:
            created.append(self._create_segment_entity((s[0], s[1], s[2]), p_a, layer=layer))
        if math.hypot(e[0] - p_b[0], e[1] - p_b[1]) > 0.5:
            created.append(self._create_segment_entity(p_b, (e[0], e[1], e[2]), layer=layer))

        # In 3D architectural mode, keep a lintel header above the opening on A-WALL so the 3D doorway/window looks realistic!
        if str(layer).upper() == "A-WALL" and math.hypot(p_b[0] - p_a[0], p_b[1] - p_a[1]) > 6.0:
            head = float(self.sysvars.get("HEADHEIGHT", 84.0))
            wall_h = float(self.sysvars.get("WALLHEIGHT", 126.0))
            if wall_h > head + 2.0:
                lintel = self._create_segment_entity(
                    p_a, p_b, layer="A-WALL", dxf_type="3DSOLID",
                    height_override=(wall_h - head), z_bottom_override=head,
                )
                lintel.name = "WallLintel"

        erased.append(self.get_handle(target))
        self.bpy.data.objects.remove(target, do_unlink=True)

    # ------------------------------------------------------------------ inspection & queries

    def summarize_object(self, obj: Any) -> Dict[str, Any]:
        h = self.get_handle(obj)
        dtype = str(obj.get("acad_dxf_type") or ("3DSOLID" if obj.type == "MESH" else obj.type))
        layer = str(obj.get("acad_layer") or (obj.users_collection[0].name if obj.users_collection else "0"))
        summary: Dict[str, Any] = {
            "handle": h,
            "name": obj.name,
            "type": dtype,
            "blenderType": obj.type,
            "layer": layer,
        }
        if dtype == "LINE":
            s = obj.get("acad_start", [0.0, 0.0, 0.0])
            e = obj.get("acad_end", [0.0, 0.0, 0.0])
            length = math.hypot(e[0] - s[0], e[1] - s[1])
            summary["start"] = {"x": round(s[0], 3), "y": round(s[1], 3), "ftIn": f"{format_feet_inches(s[0])},{format_feet_inches(s[1])}"}
            summary["end"] = {"x": round(e[0], 3), "y": round(e[1], 3), "ftIn": f"{format_feet_inches(e[0])},{format_feet_inches(e[1])}"}
            summary["length"] = {"du": round(length, 3), "ftIn": format_feet_inches(length)}
        elif dtype == "LWPOLYLINE":
            pts = json.loads(obj.get("acad_points", "[]"))
            summary["closed"] = bool(obj.get("acad_closed", False))
            summary["vertexCount"] = len(pts)
            summary["vertices"] = [{"x": round(p[0], 3), "y": round(p[1], 3)} for p in pts]
        elif dtype in ("ARC", "CIRCLE"):
            c = obj.get("acad_center", [0.0, 0.0, 0.0])
            r = float(obj.get("acad_radius", 12.0))
            summary["center"] = {"x": round(c[0], 3), "y": round(c[1], 3)}
            summary["radius"] = {"du": round(r, 3), "ftIn": format_feet_inches(r)}
            if dtype == "ARC":
                summary["startAngle"] = float(obj.get("acad_start_angle", 0.0))
                summary["endAngle"] = float(obj.get("acad_end_angle", 90.0))
        elif dtype == "TEXT":
            p = obj.get("acad_position", [0.0, 0.0, 0.0])
            summary["text"] = str(obj.get("acad_text", ""))
            summary["position"] = {"x": round(p[0], 3), "y": round(p[1], 3)}
            summary["height"] = {"du": float(obj.get("acad_height", 9.0)), "ftIn": format_feet_inches(float(obj.get("acad_height", 9.0)))}
        elif dtype == "DIMENSION":
            p1 = obj.get("acad_p1", [0.0, 0.0, 0.0])
            p2 = obj.get("acad_p2", [0.0, 0.0, 0.0])
            dl = obj.get("acad_dim_line", [0.0, 0.0, 0.0])
            m = float(obj.get("acad_measurement", 0.0))
            summary["p1"] = {"x": round(p1[0], 3), "y": round(p1[1], 3)}
            summary["p2"] = {"x": round(p2[0], 3), "y": round(p2[1], 3)}
            summary["dimLine"] = {"x": round(dl[0], 3), "y": round(dl[1], 3)}
            summary["measurement"] = {"du": round(m, 3), "ftIn": format_feet_inches(m)}
            summary["text"] = str(obj.get("acad_text", format_feet_inches(m)))
        else:
            loc = obj.location
            dim = obj.dimensions
            summary["location"] = {
                "x": round(meters_to_du(loc.x), 3),
                "y": round(meters_to_du(loc.y), 3),
                "z": round(meters_to_du(loc.z), 3),
            }
            summary["dimensions"] = {
                "x": round(meters_to_du(dim.x), 3),
                "y": round(meters_to_du(dim.y), 3),
                "z": round(meters_to_du(dim.z), 3),
            }
        return summary

    def get_drawing_info(self) -> Dict[str, Any]:
        scene = self.bpy.context.scene
        filepath = self.bpy.data.filepath or ""
        filename = os.path.basename(filepath) if filepath else "Untitled.blend"
        by_type: Dict[str, int] = {}
        min_x, min_y, min_z = float("inf"), float("inf"), float("inf")
        max_x, max_y, max_z = float("-inf"), float("-inf"), float("-inf")
        total = 0

        for obj in scene.objects:
            if obj.type not in ("MESH", "FONT", "CURVE", "LIGHT", "CAMERA", "EMPTY"):
                continue
            total += 1
            dtype = str(obj.get("acad_dxf_type") or ("3DSOLID" if obj.type == "MESH" else obj.type))
            by_type[dtype] = by_type.get(dtype, 0) + 1
            for corner in obj.bound_box:
                wx = obj.matrix_world @ self._vec3(corner)
                dx, dy, dz = meters_to_du(wx[0]), meters_to_du(wx[1]), meters_to_du(wx[2])
                min_x, max_x = min(min_x, dx), max(max_x, dx)
                min_y, max_y = min(min_y, dy), max(max_y, dy)
                min_z, max_z = min(min_z, dz), max(max_z, dz)

        if total == 0 or not math.isfinite(min_x):
            extents: Dict[str, Any] = {"empty": True}
        else:
            extents = {
                "empty": False,
                "min": {"x": round(min_x, 2), "y": round(min_y, 2), "z": round(min_z, 2)},
                "max": {"x": round(max_x, 2), "y": round(max_y, 2), "z": round(max_z, 2)},
                "width": {"du": round(max_x - min_x, 2), "ftIn": format_feet_inches(max_x - min_x)},
                "height": {"du": round(max_y - min_y, 2), "ftIn": format_feet_inches(max_y - min_y)},
                "depth3D": {"du": round(max_z - min_z, 2), "ftIn": format_feet_inches(max_z - min_z)},
            }

        layers = ["0"] + [c.name for c in self.bpy.data.collections]
        unique_layers = list(dict.fromkeys(layers))
        data = {
            "product": "Blender 5.3",
            "blenderVersion": self.bpy.app.version_string,
            "architectureObjects": True,
            "file": {
                "name": filename,
                "path": filepath if filepath else None,
                "saved": bool(filepath),
                "modified": bool(self.bpy.data.is_dirty),
                "readOnly": False,
            },
            "units": {
                "lunits": 4,
                "format": "Architectural (feet-inches & 3D meters)",
                "insunits": 1,
                "luprec": 4,
                "unit": "inches (1 du = 1 inch = 0.0254 m in Blender)",
                "inchesPerUnit": 1.0,
                "acceptsFeetInches": True,
                "summary": "Architectural feet-inches (1 unit = 1 inch, 3D scale in meters)",
            },
            "space": "model",
            "currentLayer": self.current_layer,
            "layerCount": len(unique_layers),
            "entities": {
                "total": total,
                "byType": by_type,
            },
            "paperSpaceEntities": 0,
            "extents": extents,
            "view": {
                "center": {"x": 180.0, "y": 240.0},
                "width": {"du": 600.0, "ftIn": "50'-0\""},
                "height": {"du": 600.0, "ftIn": "50'-0\""},
            },
            "styles": {
                "textStyle": "ARCH",
                "dimStyle": "ARCH-48",
                "dimscale": 48.0,
                "ltscale": 24.0,
                "annotationScale": "1/4\" = 1'-0\"",
            },
            "command": {"active": False, "name": None},
            "revision": self.revision,
        }
        return {
            "success": True,
            "message": f"{filename} (Blender 5.3): {total} object(s) across {len(unique_layers)} collection(s).",
            "data": data,
            "revision": self.revision,
        }

    def list_layers(self) -> Dict[str, Any]:
        counts: Dict[str, int] = {"0": 0}
        for obj in self.bpy.context.scene.objects:
            lname = str(obj.get("acad_layer") or (obj.users_collection[0].name if obj.users_collection else "0"))
            counts[lname] = counts.get(lname, 0) + 1

        all_names = list(dict.fromkeys(["0"] + [c.name for c in self.bpy.data.collections] + list(counts.keys())))
        items = []
        for name in all_names:
            spec = LAYER_SPECS.get(name.upper(), LAYER_SPECS["0"])
            items.append({
                "name": name,
                "current": name.upper() == self.current_layer.upper(),
                "on": True,
                "Frozen": False,
                "locked": False,
                "color": spec[4],
                "linetype": "CENTER" if name.upper() == "A-GRID" else "Continuous",
                "lineweight": spec[5],
                "entityCount": counts.get(name, 0),
            })
        return {
            "success": True,
            "message": f"{len(items)} layer(s)/collection(s).",
            "data": {"currentLayer": self.current_layer, "count": len(items), "layers": items},
            "revision": self.revision,
        }

    def list_entities(self, args: Dict[str, Any]) -> Dict[str, Any]:
        filter_layer = str(args.get("layer") or "").strip().upper()
        filter_type = str(args.get("type") or "").strip().upper()
        limit = max(1, min(500, int(args.get("limit") or 100)))
        offset = max(0, int(args.get("offset") or 0))

        matched = []
        for obj in self.bpy.context.scene.objects:
            if obj.type not in ("MESH", "FONT", "CURVE", "LIGHT", "CAMERA", "EMPTY"):
                continue
            dtype = str(obj.get("acad_dxf_type") or ("3DSOLID" if obj.type == "MESH" else obj.type)).upper()
            layer = str(obj.get("acad_layer") or (obj.users_collection[0].name if obj.users_collection else "0")).upper()
            if filter_layer and layer != filter_layer:
                continue
            if filter_type and dtype != filter_type:
                continue
            matched.append(self.summarize_object(obj))

        sliced = matched[offset : offset + limit]
        return {
            "success": True,
            "message": f"Showing {len(sliced)} of {len(matched)} object(s).",
            "data": {
                "total": len(matched),
                "offset": offset,
                "limit": limit,
                "hasMore": (offset + limit) < len(matched),
                "entities": sliced,
            },
            "revision": self.revision,
        }

    def get_entity(self, handle: str) -> Dict[str, Any]:
        obj = self.find_by_handle(handle)
        if obj is None:
            return {
                "success": False,
                "message": f"Object '{handle}' was not found.",
                "errors": [f"No object with handle or name '{handle}'."],
                "revision": self.revision,
            }
        info = self.summarize_object(obj)
        return {
            "success": True,
            "message": f"{info['type']} {info['handle']} on {info['layer']}.",
            "data": info,
            "revision": self.revision,
        }

    def get_sysvars(self, names: List[str]) -> Dict[str, Any]:
        values = {}
        for name in (names or list(self.sysvars.keys())):
            key = str(name).strip().upper()
            values[key] = self.sysvars.get(key, 0)
        return {
            "success": True,
            "message": f"Read {len(values)} system variable(s).",
            "data": {"variables": values},
            "revision": self.revision,
        }

    def apply_drawing_template(self) -> Dict[str, Any]:
        default_cube = self.bpy.data.objects.get("Cube")
        if default_cube is not None and not default_cube.get("acad_handle"):
            try:
                self.bpy.data.objects.remove(default_cube, do_unlink=True)
            except Exception:
                pass
        for lname in LAYER_SPECS:
            ensure_collection(self.bpy, lname)
            ensure_layer_material(self.bpy, lname)
        self._ensure_lighting_and_camera()
        self.bump_revision()
        self._redraw_viewport()
        return {
            "success": True,
            "message": "Applied architectural 3D template in Blender 5.3: units (feet-inches), collections (A-WALL, A-DOOR, A-GLAZ, A-STRS, A-FURN, A-SITE, A-ANNO-TEXT, A-ANNO-DIMS, A-ROOF), Principled BSDF materials, sun light and camera.",
            "data": {
                "units": "Architectural (feet-inches)",
                "layersCreated": list(LAYER_SPECS.keys()),
                "textStyle": "ARCH",
                "dimStyle": "ARCH-48",
            },
            "revision": self.revision,
        }

    def undo(self, count: int = 1) -> Dict[str, Any]:
        undone = 0
        removed: List[str] = []
        for _ in range(max(1, count)):
            if self._undo_stack:
                handles = self._undo_stack.pop()
                for h in handles:
                    obj = self.find_by_handle(h)
                    if obj is not None:
                        removed.append(h)
                        self.bpy.data.objects.remove(obj, do_unlink=True)
                undone += 1
            else:
                try:
                    self.bpy.ops.ed.undo()
                    undone += 1
                except Exception:
                    break
        self.bump_revision()
        self._redraw_viewport()
        return {
            "success": True,
            "message": f"Undid {undone} stage(s) ({len(removed)} object(s) removed).",
            "deletedIds": removed,
            "revision": self.revision,
        }

    def get_selection(self) -> Dict[str, Any]:
        selected = []
        try:
            objs = list(self.bpy.context.selected_objects)
        except Exception:
            objs = [o for o in self.bpy.context.scene.objects if o.select_get()]
        for obj in objs[:60]:
            s = self.summarize_object(obj)
            detail = s.get("text") or (s.get("length", {}).get("ftIn") if isinstance(s.get("length"), dict) else "") or obj.name
            selected.append({
                "handle": s["handle"],
                "name": obj.name,
                "type": s["type"],
                "layer": s["layer"],
                "detail": str(detail),
            })
        filepath = self.bpy.data.filepath or ""
        filename = os.path.basename(filepath) if filepath else "Untitled.blend"
        return {
            "success": True,
            "message": f"{len(objs)} object(s) selected in Blender 5.3.",
            "data": {
                "count": len(objs),
                "drawingTitle": filename,
                "items": selected,
            },
            "revision": self.revision,
        }

    def select_objects(self, handles: List[str]) -> Dict[str, Any]:
        for obj in self.bpy.context.scene.objects:
            try:
                obj.select_set(False)
            except Exception:
                pass
        count = 0
        for h in (handles or []):
            obj = self.find_by_handle(h)
            if obj is not None:
                try:
                    obj.select_set(True)
                    self.bpy.context.view_layer.objects.active = obj
                    count += 1
                except Exception:
                    pass
        self._redraw_viewport()
        return {
            "success": True,
            "message": f"Selected {count} object(s) in Blender 5.3.",
            "data": {"selectedCount": count},
            "revision": self.revision,
        }

    # ------------------------------------------------------------------ capture_view & export_view_png

    def capture_view(self, width: int = 960, height: int = 640, output_path: Optional[str] = None) -> Dict[str, Any]:
        """Render the current Blender 5.3 3D scene to PNG and return base64 + view metadata."""
        self._ensure_lighting_and_camera(frame_scene=True)
        scene = self.bpy.context.scene
        prev_res_x = scene.render.resolution_x
        prev_res_y = scene.render.resolution_y
        prev_pct = scene.render.resolution_percentage
        prev_path = scene.render.filepath
        prev_engine = scene.render.engine

        target_file = output_path or os.path.join(tempfile.gettempdir(), f"blendercopilot_view_{os.getpid()}.png")
        os.makedirs(os.path.dirname(os.path.abspath(target_file)), exist_ok=True)

        try:
            scene.render.resolution_x = width
            scene.render.resolution_y = height
            scene.render.resolution_percentage = 100
            scene.render.image_settings.file_format = "PNG"
            scene.render.filepath = target_file

            rendered = False
            # If a 3D Viewport window is open (interactive desktop mode), try fast OpenGL viewport render first
            if not self.bpy.app.background:
                for window in self.bpy.context.window_manager.windows:
                    for area in window.screen.areas:
                        if area.type == "VIEW_3D":
                            for space in area.spaces:
                                if space.type == "VIEW_3D":
                                    try:
                                        space.shading.type = "SOLID"
                                        space.shading.color_type = "OBJECT"
                                        space.shading.show_shadows = True
                                    except Exception:
                                        pass
                            for region in area.regions:
                                if region.type == "WINDOW":
                                    try:
                                        with self.bpy.context.temp_override(window=window, area=area, region=region):
                                            self.bpy.ops.render.opengl(write_still=True)
                                            rendered = os.path.exists(target_file) and os.path.getsize(target_file) > 100
                                    except Exception:
                                        rendered = False
                                    break
                            break
                    if rendered:
                        break

            if not rendered:
                # Fallback: fast Workbench render from the scene camera
                try:
                    scene.render.engine = "BLENDER_WORKBENCH"
                    scene.display.shading.color_type = "OBJECT"
                except Exception:
                    pass
                self.bpy.ops.render.render(write_still=True)
        finally:
            scene.render.resolution_x = prev_res_x
            scene.render.resolution_y = prev_res_y
            scene.render.resolution_percentage = prev_pct
            scene.render.filepath = prev_path
            try:
                scene.render.engine = prev_engine
            except Exception:
                pass

        with open(target_file, "rb") as f:
            png_bytes = f.read()
        b64 = base64.b64encode(png_bytes).decode("ascii")
        return {
            "success": True,
            "message": f"Captured Blender 5.3 3D view ({width}x{height} PNG).",
            "data": {
                "width": width,
                "height": height,
                "file": target_file if output_path else None,
                "folder": os.path.dirname(target_file) if output_path else None,
            },
            "images": [
                {
                    "mimeType": "image/png",
                    "base64Data": b64,
                    "description": "Blender 5.3 3D Viewport snapshot",
                }
            ],
            "revision": self.revision,
        }

    def export_view_png(self, name: Optional[str] = None, width: int = 1920, height: int = 1280) -> Dict[str, Any]:
        docs = os.path.join(os.path.expanduser("~"), "Documents", "AcadCopilot Exports")
        os.makedirs(docs, exist_ok=True)
        base = (name or "BlenderScene").strip().replace(" ", "_")
        stamp = time.strftime("%Y%m%d-%H%M%S")
        out_path = os.path.join(docs, f"{base}_{stamp}.png")
        res = self.capture_view(width=width, height=height, output_path=out_path)
        res["message"] = f"Exported 3D view PNG to {out_path}."
        res["data"]["file"] = out_path
        res["data"]["folder"] = docs
        return res

    # ------------------------------------------------------------------ camera, lighting & viewport helpers

    def _ensure_lighting_and_camera(self, frame_scene: bool = False) -> None:
        scene = self.bpy.context.scene
        # Remove default startup Cube if it has no acad_handle and we have our own objects
        own_objs = [o for o in scene.objects if o.get("acad_handle")]
        if own_objs:
            default_cube = self.bpy.data.objects.get("Cube")
            if default_cube is not None and not default_cube.get("acad_handle"):
                try:
                    self.bpy.data.objects.remove(default_cube, do_unlink=True)
                except Exception:
                    pass

        # Ensure a sun light exists
        has_light = any(o.type == "LIGHT" for o in scene.objects)
        if not has_light:
            ldata = self.bpy.data.lights.new(name="SunLight", type="SUN")
            ldata.energy = 3.5
            lobj = self.bpy.data.objects.new(name="SunLight", object_data=ldata)
            lobj.location = (6.0, -6.0, 12.0)
            lobj.rotation_euler = (math.radians(48.0), math.radians(15.0), math.radians(32.0))
            scene.collection.objects.link(lobj)

        # Ensure a camera exists
        cam = scene.camera
        if cam is None:
            cdata = self.bpy.data.cameras.new("Camera")
            cam = self.bpy.data.objects.new("Camera", cdata)
            scene.collection.objects.link(cam)
            scene.camera = cam

        if frame_scene:
            self._frame_camera_and_viewport(cam)

    def _frame_camera_and_viewport(self, cam: Any) -> None:
        scene = self.bpy.context.scene
        min_x, min_y, min_z = float("inf"), float("inf"), float("inf")
        max_x, max_y, max_z = float("-inf"), float("-inf"), float("-inf")
        count = 0
        for obj in scene.objects:
            if obj.type not in ("MESH", "FONT", "CURVE"):
                continue
            count += 1
            for corner in obj.bound_box:
                wx = obj.matrix_world @ self._vec3(corner)
                min_x, max_x = min(min_x, wx[0]), max(max_x, wx[0])
                min_y, max_y = min(min_y, wx[1]), max(max_y, wx[1])
                min_z, max_z = min(min_z, wx[2]), max(max_z, wx[2])

        if count == 0 or not math.isfinite(min_x):
            return

        cx = (min_x + max_x) * 0.5
        cy = (min_y + max_y) * 0.5
        cz = (min_z + max_z) * 0.5
        span = max(max_x - min_x, max_y - min_y, max_z - min_z, 4.0)

        # Position camera at an architectural 3/4 isometric overview
        cam_dist = span * 1.45
        cam.location = (cx + cam_dist * 0.45, cy - cam_dist * 0.85, cz + cam_dist * 0.95)
        self._aim_object_at(cam, (cx, cy, cz))

        if not self.bpy.app.background:
            for window in self.bpy.context.window_manager.windows:
                for area in window.screen.areas:
                    if area.type == "VIEW_3D":
                        for space in area.spaces:
                            if space.type == "VIEW_3D" and space.region_3d is not None:
                                try:
                                    space.region_3d.view_location = (cx, cy, cz)
                                    space.region_3d.view_distance = span * 1.35
                                except Exception:
                                    pass

    def _aim_object_at(self, obj: Any, target_xyz: Tuple[float, float, float]) -> None:
        try:
            import mathutils
            loc = mathutils.Vector(obj.location)
            tgt = mathutils.Vector(target_xyz)
            direction = tgt - loc
            if direction.length > 1e-5:
                rot_quat = direction.to_track_quat("-Z", "Y")
                obj.rotation_euler = rot_quat.to_euler()
        except Exception:
            pass

    def _vec3(self, xyz: Any) -> Any:
        import mathutils
        return mathutils.Vector((xyz[0], xyz[1], xyz[2]))

    def _redraw_viewport(self) -> None:
        if self.bpy.app.background:
            return
        try:
            self.bpy.ops.wm.redraw_timer(type="DRAW_WIN_SWAP", iterations=1)
        except Exception:
            pass

    # ------------------------------------------------------------------ snapshot data for check_drawing

    def get_drawing_snapshot(self) -> Dict[str, Any]:
        """Return all lines, polylines, arcs, texts, dimensions, and layers for C# DrawingChecker & StyleAnalyzer."""
        lines = []
        polylines = []
        arcs = []
        texts = []
        dimensions = []
        layers = []

        for lname in dict.fromkeys(["0"] + [c.name for c in self.bpy.data.collections]):
            spec = LAYER_SPECS.get(lname.upper(), LAYER_SPECS["0"])
            layers.append({
                "name": lname,
                "color": spec[4],
                "linetype": "CENTER" if lname.upper() == "A-GRID" else "Continuous",
                "lineweight": spec[5],
            })

        for obj in self.bpy.context.scene.objects:
            h = self.get_handle(obj)
            dtype = obj.get("acad_dxf_type")
            layer = str(obj.get("acad_layer") or (obj.users_collection[0].name if obj.users_collection else "0"))
            if dtype == "LINE":
                s = obj.get("acad_start", [0.0, 0.0, 0.0])
                e = obj.get("acad_end", [0.0, 0.0, 0.0])
                lines.append({"handle": h, "layer": layer, "x1": s[0], "y1": s[1], "x2": e[0], "y2": e[1]})
            elif dtype == "LWPOLYLINE":
                pts = json.loads(obj.get("acad_points", "[]"))
                polylines.append({
                    "handle": h,
                    "layer": layer,
                    "closed": bool(obj.get("acad_closed", False)),
                    "vertices": [{"x": p[0], "y": p[1]} for p in pts],
                })
            elif dtype == "ARC":
                c = obj.get("acad_center", [0.0, 0.0, 0.0])
                arcs.append({
                    "handle": h,
                    "layer": layer,
                    "cx": c[0],
                    "cy": c[1],
                    "radius": float(obj.get("acad_radius", 12.0)),
                    "startAngle": float(obj.get("acad_start_angle", 0.0)),
                    "endAngle": float(obj.get("acad_end_angle", 90.0)),
                })
            elif dtype == "TEXT":
                p = obj.get("acad_position", [0.0, 0.0, 0.0])
                t = str(obj.get("acad_text", ""))
                h_du = float(obj.get("acad_height", 9.0))
                w_du = max(h_du, len(t) * h_du * 0.6)
                texts.append({
                    "handle": h,
                    "layer": layer,
                    "text": t,
                    "x": p[0],
                    "y": p[1],
                    "height": h_du,
                    "minX": p[0] - w_du * 0.5,
                    "minY": p[1] - h_du * 0.5,
                    "maxX": p[0] + w_du * 0.5,
                    "maxY": p[1] + h_du * 0.5,
                })
            elif dtype == "DIMENSION":
                p1 = obj.get("acad_p1", [0.0, 0.0, 0.0])
                p2 = obj.get("acad_p2", [0.0, 0.0, 0.0])
                dl = obj.get("acad_dim_line", [0.0, 0.0, 0.0])
                dimensions.append({
                    "handle": h,
                    "layer": layer,
                    "x1": p1[0],
                    "y1": p1[1],
                    "x2": p2[0],
                    "y2": p2[1],
                    "lineX": dl[0],
                    "lineY": dl[1],
                    "measurement": float(obj.get("acad_measurement", 0.0)),
                    "textOverride": "",
                })

        return {
            "lines": lines,
            "polylines": polylines,
            "arcs": arcs,
            "texts": texts,
            "dimensions": dimensions,
            "layers": layers,
        }
