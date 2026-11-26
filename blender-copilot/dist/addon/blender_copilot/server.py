"""
Main-thread RPC bridge, inner workspace dock manager, and Win32 window locator inside Blender 5.3 Desktop.
Splits a dedicated right-hand dock column inside Blender 5.3's workspace (shifting Outliner and Properties
to the left so they remain completely visible) and embeds `BlenderCopilotHost.exe` inside Blender's window
(below Blender's top menu/workspace bar and above Blender's bottom status bar).
"""

from __future__ import annotations
import ctypes
from ctypes import wintypes
import json
import os
import queue
import socket
import subprocess
import threading
import time
from typing import Any, Dict, List, Optional, Tuple

from .engine import BlenderCadEngine
from .units import meters_to_du, format_feet_inches


def find_blender_main_hwnd() -> int:
    """Find the top-level Win32 HWND of this Blender 5.3 process."""
    if os.name != "nt":
        return 0
    pid = os.getpid()
    user32 = ctypes.windll.user32
    found: List[Tuple[int, int]] = []

    WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    class RECT(ctypes.Structure):
        _fields_ = [
            ("left", ctypes.c_long),
            ("top", ctypes.c_long),
            ("right", ctypes.c_long),
            ("bottom", ctypes.c_long),
        ]

    def callback(hwnd: int, _lparam: int) -> bool:
        if not user32.IsWindowVisible(hwnd):
            return True
        win_pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(win_pid))
        if win_pid.value != pid:
            return True
        rect = RECT()
        if user32.GetWindowRect(hwnd, ctypes.byref(rect)):
            area = max(0, rect.right - rect.left) * max(0, rect.bottom - rect.top)
            if area > 40000:
                found.append((area, int(hwnd)))
        return True

    user32.EnumWindows(WNDENUMPROC(callback), 0)
    if not found:
        return 0
    found.sort(reverse=True)
    return found[0][1]


def maximize_blender_window() -> None:
    """Ensure Blender 5.3's main window is maximized (full screen) so the AI Copilot panel docks inside it."""
    if os.name != "nt":
        return
    hwnd = find_blender_main_hwnd()
    if hwnd:
        try:
            ctypes.windll.user32.ShowWindow(hwnd, 3)  # SW_MAXIMIZE = 3
        except Exception:
            pass


class BlenderWorkerServer:
    """TCP localhost worker running inside Blender 5.3, dispatching all work on Blender's main thread."""

    def __init__(self, bpy_mod: Any) -> None:
        self.bpy = bpy_mod
        self.engine = BlenderCadEngine(bpy_mod)
        self.port: int = 0
        self._sock: Optional[socket.socket] = None
        self._running: bool = False
        self._req_queue: "queue.Queue[Tuple[Dict[str, Any], queue.Queue[Dict[str, Any]]]]" = queue.Queue()
        self._host_proc: Optional[subprocess.Popen] = None

        # Inner workspace dock state (0 = not split yet, 1 = vertical split done, 2 = middle Outliner/Props ready)
        self._dock_layout_stage: int = 0
        self._mid_col_area: Optional[Any] = None
        self._panel_visible: bool = True

        # Step-by-step Watch-mode state
        self._active_watch_job: Optional[Dict[str, Any]] = None

        # "Learn from me" recorder state
        self.recorder_state: str = "off"  # "off" | "recording" | "paused"
        self.recorded_actions: List[Dict[str, Any]] = []
        self._last_op_count: int = 0
        self._known_handles: set[str] = set()
        self._copilot_busy: bool = False

        # Pending UI command from Blender's N-panel / header to the WPF host
        self.pending_ui_events: List[str] = []

    def start(self) -> int:
        if self._running:
            return self.port
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._sock.bind(("127.0.0.1", 0))
        self._sock.listen(8)
        self.port = self._sock.getsockname()[1]
        self._running = True

        t = threading.Thread(target=self._accept_loop, name="BlenderCopilotWorkerAccept", daemon=True)
        t.start()
        self._write_worker_file()
        return self.port

    def stop(self) -> None:
        self._running = False
        if self._sock is not None:
            try:
                self._sock.close()
            except Exception:
                pass
            self._sock = None
        if self._host_proc is not None and self._host_proc.poll() is None:
            try:
                self._host_proc.terminate()
            except Exception:
                pass

    def _home_dir(self) -> str:
        override = os.environ.get("BLENDER_COPILOT_HOME") or os.environ.get("ACADCOPILOT_HOME")
        if override:
            os.makedirs(override, exist_ok=True)
            return override
        local_app = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
        path = os.path.join(local_app, "BlenderCopilot")
        os.makedirs(path, exist_ok=True)
        return path

    def _write_worker_file(self) -> str:
        home = self._home_dir()
        workers_dir = os.path.join(home, "workers")
        os.makedirs(workers_dir, exist_ok=True)
        filepath = self.bpy.data.filepath or ""
        filename = os.path.basename(filepath) if filepath else "Untitled.blend"
        info = {
            "pid": os.getpid(),
            "port": self.port,
            "hwnd": find_blender_main_hwnd(),
            "blenderVersion": self.bpy.app.version_string,
            "documentTitle": filename,
        }
        worker_file = os.path.join(workers_dir, f"blender-{os.getpid()}.json")
        with open(worker_file, "w", encoding="utf-8") as f:
            json.dump(info, f, indent=2)
        return worker_file

    def ensure_inner_dock_layout(self) -> None:
        """
        Reserve a dedicated rightmost column inside Blender's workspace for the AI Copilot panel
        by shifting Outliner and Properties into a middle-right column next to the 3D Viewport.
        """
        if self.bpy.app.background or not self._panel_visible:
            return
        try:
            wm = self.bpy.context.window_manager
            if not wm or not wm.windows:
                return
            win = wm.windows[0]
            scr = win.screen
            if not scr or not scr.areas:
                return

            if self._dock_layout_stage == 0:
                maximize_blender_window()
                v3ds = [a for a in scr.areas if a.type == "VIEW_3D"]
                if not v3ds:
                    return
                v3d = max(v3ds, key=lambda a: a.width * a.height)
                if v3d.width < 800:
                    return
                before = set(scr.areas)
                with self.bpy.context.temp_override(window=win, screen=scr, area=v3d):
                    self.bpy.ops.screen.area_split(direction="VERTICAL", factor=0.77)
                added = list(set(scr.areas) - before)
                if added:
                    self._mid_col_area = added[0]
                    self._dock_layout_stage = 1
                else:
                    self._dock_layout_stage = 2
                return

            if self._dock_layout_stage == 1:
                mid = self._mid_col_area
                if mid is None or mid not in list(scr.areas):
                    self._dock_layout_stage = 2
                    return
                if mid.height < 150:
                    return  # wait for Blender to compute layout dimensions
                before = set(scr.areas)
                with self.bpy.context.temp_override(window=win, screen=scr, area=mid):
                    self.bpy.ops.screen.area_split(direction="HORIZONTAL", factor=0.74)
                added = list(set(scr.areas) - before)
                mid.type = "PROPERTIES"
                if added:
                    added[0].type = "OUTLINER"
                self._dock_layout_stage = 2
        except Exception as ex:
            print(f"[AI Copilot] Layout split note: {ex}")
            self._dock_layout_stage = 2

    def compute_layout_rects(self) -> Tuple[Optional[Dict[str, int]], Optional[Dict[str, int]]]:
        """
        Return (dockRect, view3dRect) in Blender client-area pixels (origin bottom-left of client area).
        - dockRect is the rightmost column inside Blender's window (between TOPBAR and STATUSBAR).
        - view3dRect is the main 3D Viewport area (so Quick Ask's AI button sits inside the 3D Viewport).
        """
        if self.bpy.app.background:
            return None, None
        try:
            wm = self.bpy.context.window_manager
            if not wm or not wm.windows:
                return None, None
            win = wm.windows[0]
            scr = win.screen
            if not scr or not scr.areas or win.width <= 0 or win.height <= 0:
                return None, None

            areas = list(scr.areas)
            min_y = min(a.y for a in areas)
            max_top = max(a.y + a.height for a in areas)

            # Find the rightmost column areas
            max_right = max(a.x + a.width for a in areas)
            right_areas = [a for a in areas if (a.x + a.width) >= max_right - 8 and a.width < win.width * 0.5]
            if right_areas:
                dock_x = min(a.x for a in right_areas)
                dock_w = max_right - dock_x
            else:
                dock_w = min(430, int(win.width * 0.25))
                dock_x = win.width - dock_w

            dock_rect = {
                "x": int(dock_x),
                "y": int(min_y),
                "width": int(max(340, dock_w)),
                "height": int(max(300, max_top - min_y)),
                "winWidth": int(win.width),
                "winHeight": int(win.height),
            }

            v3ds = [a for a in areas if a.type == "VIEW_3D"]
            v3d_rect = None
            if v3ds:
                v3d = max(v3ds, key=lambda a: a.width * a.height)
                v3d_rect = {
                    "x": int(v3d.x),
                    "y": int(v3d.y),
                    "width": int(v3d.width),
                    "height": int(v3d.height),
                    "winWidth": int(win.width),
                    "winHeight": int(win.height),
                }
            return dock_rect, v3d_rect
        except Exception:
            return None, None

    def launch_wpf_host_if_needed(self) -> bool:
        """Start BlenderCopilotHost.exe embedded inside Blender 5.3 when running in desktop GUI mode."""
        if self.bpy.app.background:
            return False
        if os.environ.get("BLENDER_COPILOT_NO_GUI") == "1":
            return False
        if self._host_proc is not None and self._host_proc.poll() is None:
            return True

        host_exe = self._find_host_exe()
        if not host_exe or not os.path.exists(host_exe):
            return False

        maximize_blender_window()
        hwnd = find_blender_main_hwnd()
        env = os.environ.copy()
        env["ACADCOPILOT_HOME"] = self._home_dir()
        dotnet_root = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "dotnet")
        if os.path.isdir(dotnet_root):
            env["DOTNET_ROOT"] = dotnet_root
            env["PATH"] = dotnet_root + os.pathsep + env.get("PATH", "")

        dotnet_exe = os.path.join(dotnet_root, "dotnet.exe")
        host_dll = os.path.join(os.path.dirname(host_exe), "BlenderCopilotHost.dll")
        if os.path.isfile(dotnet_exe) and os.path.isfile(host_dll):
            args = [
                dotnet_exe,
                host_dll,
                "--blender-pid", str(os.getpid()),
                "--worker-port", str(self.port),
                "--blender-hwnd", str(hwnd),
            ]
        else:
            args = [
                host_exe,
                "--blender-pid", str(os.getpid()),
                "--worker-port", str(self.port),
                "--blender-hwnd", str(hwnd),
            ]
        try:
            creation_flags = 0x08000000 if os.name == "nt" else 0  # CREATE_NO_WINDOW
            self._host_proc = subprocess.Popen(
                args,
                env=env,
                creationflags=creation_flags,
            )
            return True
        except Exception as ex:
            print(f"[AI Copilot] Failed to launch BlenderCopilotHost.exe: {ex}")
            return False

    def _find_host_exe(self) -> Optional[str]:
        candidates = [
            os.environ.get("BLENDER_COPILOT_HOST_EXE", ""),
            os.path.join(self._home_dir(), "bin", "BlenderCopilotHost.exe"),
            os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "dist", "bin", "BlenderCopilotHost.exe")),
            os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "src", "BlenderCopilot.Host", "bin", "Release", "net10.0-windows10.0.19041.0", "BlenderCopilotHost.exe")),
        ]
        for c in candidates:
            if c and os.path.isfile(c):
                return c
        return None

    # ------------------------------------------------------------------ TCP server threads

    def _accept_loop(self) -> None:
        while self._running and self._sock is not None:
            try:
                conn, _ = self._sock.accept()
                t = threading.Thread(target=self._client_loop, args=(conn,), daemon=True)
                t.start()
            except Exception:
                if not self._running:
                    break

    def _client_loop(self, conn: socket.socket) -> None:
        with conn:
            buf = b""
            while self._running:
                try:
                    chunk = conn.recv(65536)
                    if not chunk:
                        break
                    buf += chunk
                    while b"\n" in buf:
                        line, buf = buf.split(b"\n", 1)
                        line_str = line.decode("utf-8", errors="replace").strip()
                        if not line_str:
                            continue
                        req = json.loads(line_str)
                        reply_q: "queue.Queue[Dict[str, Any]]" = queue.Queue(maxsize=1)
                        self._req_queue.put((req, reply_q))
                        resp = reply_q.get(timeout=300.0)
                        out = (json.dumps(resp) + "\n").encode("utf-8")
                        conn.sendall(out)
                except Exception as ex:
                    try:
                        err_resp = {"success": False, "message": f"Worker error: {ex}", "errors": [str(ex)]}
                        conn.sendall((json.dumps(err_resp) + "\n").encode("utf-8"))
                    except Exception:
                        break

    # ------------------------------------------------------------------ Main-thread timer pump

    def pump_main_thread(self) -> float:
        """Called every 50 ms on Blender's main thread via bpy.app.timers."""
        if not self.bpy.app.background and self._dock_layout_stage < 2:
            self.ensure_inner_dock_layout()

        try:
            self._capture_manual_user_actions()
        except Exception:
            pass

        # If a Watch-mode run_commands job is stepping one command at a time:
        if self._active_watch_job is not None:
            self._step_watch_job()
            return 0.18

        # Process up to 6 queued RPC requests per timer tick
        processed = 0
        while processed < 6:
            try:
                req, reply_q = self._req_queue.get_nowait()
            except queue.Empty:
                break
            processed += 1
            try:
                method = str(req.get("method", ""))
                params = req.get("params") or {}
                if (
                    method == "run_commands"
                    and bool(params.get("watch", False))
                    and not self.bpy.app.background
                    and len(params.get("steps") or []) > 1
                ):
                    self._start_watch_job(params, reply_q)
                    return 0.15
                result = self._dispatch(method, params)
                reply_q.put(result)
            except Exception as ex:
                reply_q.put({
                    "success": False,
                    "message": f"Blender error: {ex}",
                    "errors": [str(ex)],
                    "revision": self.engine.revision,
                })

        return 0.05

    def _start_watch_job(self, params: Dict[str, Any], reply_q: "queue.Queue[Dict[str, Any]]") -> None:
        self._copilot_busy = True
        title = str(params.get("title") or "Blender commands")
        steps = list(params.get("steps") or [])
        try:
            self.bpy.ops.ed.undo_push(message=f"Before AI Copilot: {title}")
        except Exception:
            pass
        self._active_watch_job = {
            "title": title,
            "steps": steps,
            "index": 0,
            "step_reports": [],
            "created_handles": [],
            "modified_handles": [],
            "erased_handles": [],
            "created_summaries": [],
            "warnings": [],
            "state": {
                "last_dim_pt": (0.0, 0.0, 0.0),
                "last_dim_line": (0.0, 0.0, 0.0),
                "last_dim_axis": "H",
            },
            "reply_q": reply_q,
        }

    def _step_watch_job(self) -> None:
        job = self._active_watch_job
        if job is None:
            return
        steps = job["steps"]
        idx = job["index"]
        if idx >= len(steps):
            self._finish_watch_job(job)
            return

        step = steps[idx]
        job["index"] = idx + 1
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
            self.engine._exec_single_step(
                cmd, inputs, step_created, step_modified, step_erased, auto_fixed, job["state"]
            )
        except Exception as ex:
            step_error = str(ex)

        for obj in step_created:
            h = self.engine.get_handle(obj)
            if h not in job["created_handles"]:
                job["created_handles"].append(h)
            job["created_summaries"].append(self.engine.summarize_object(obj))
        for obj in step_modified:
            h = self.engine.get_handle(obj)
            if h not in job["modified_handles"]:
                job["modified_handles"].append(h)
        for h in step_erased:
            if h not in job["erased_handles"]:
                job["erased_handles"].append(h)

        job["step_reports"].append({
            "step": idx + 1,
            "command": cmd,
            "inputs": inputs,
            "note": note,
            "status": "failed" if step_error else "finished",
            "prompts": [f"{cmd}: {', '.join(inputs[:4])}"],
            "error": step_error,
            "autoFixed": auto_fixed if auto_fixed else None,
            "createdCount": len(step_created),
            "modifiedCount": len(step_modified),
            "erasedCount": len(step_erased),
        })
        self.engine._redraw_viewport()

        if job["index"] >= len(steps):
            self._finish_watch_job(job)

    def _finish_watch_job(self, job: Dict[str, Any]) -> None:
        self._active_watch_job = None
        title = job["title"]
        steps = job["steps"]
        step_reports = job["step_reports"]
        created_handles = job["created_handles"]
        modified_handles = job["modified_handles"]
        erased_handles = job["erased_handles"]

        self.engine._undo_stack.append(list(created_handles))
        self.engine.bump_revision()
        self.engine._redraw_viewport()
        try:
            self.bpy.ops.ed.undo_push(message=f"AI Copilot: {title}")
        except Exception:
            pass
        self._sync_known_handles()
        self._copilot_busy = False

        failed_steps = [s for s in step_reports if s["status"] == "failed"]
        outcome = "completed" if not failed_steps else "partial"
        msg = (
            f"{title}: {len(steps)} step(s) executed in Blender 5.3 "
            f"({len(created_handles)} created, {len(modified_handles)} modified, {len(erased_handles)} erased)."
        )
        report_data = {
            "title": title,
            "outcome": outcome,
            "stepCount": len(steps),
            "completedSteps": len(steps) - len(failed_steps),
            "createdCount": len(created_handles),
            "modifiedCount": len(modified_handles),
            "erasedCount": len(erased_handles),
            "steps": step_reports,
            "created": job["created_summaries"][:60],
            "warnings": job["warnings"],
            "revision": self.engine.revision,
        }
        job["reply_q"].put({
            "success": len(failed_steps) < len(steps) or len(steps) == 0,
            "message": msg,
            "data": report_data,
            "createdIds": created_handles,
            "modifiedIds": modified_handles,
            "deletedIds": erased_handles,
            "warnings": job["warnings"],
            "errors": [s["error"] for s in failed_steps if s.get("error")],
            "revision": self.engine.revision,
        })

    # ------------------------------------------------------------------ RPC dispatch

    def _dispatch(self, method: str, params: Dict[str, Any]) -> Dict[str, Any]:
        self._copilot_busy = True
        try:
            if method == "ping":
                filepath = self.bpy.data.filepath or ""
                filename = os.path.basename(filepath) if filepath else "Untitled.blend"
                cur = self.bpy.context.scene.cursor.location
                cx_du, cy_du, cz_du = meters_to_du(cur.x), meters_to_du(cur.y), meters_to_du(cur.z)
                sel = self.engine.get_selection()["data"]
                dock_rect, view3d_rect = self.compute_layout_rects()
                events = list(self.pending_ui_events)
                self.pending_ui_events.clear()
                return {
                    "success": True,
                    "data": {
                        "pid": os.getpid(),
                        "hwnd": find_blender_main_hwnd(),
                        "documentTitle": filename,
                        "documentPath": filepath if filepath else None,
                        "revision": self.engine.revision,
                        "dockRect": dock_rect,
                        "view3dRect": view3d_rect,
                        "cursor": {
                            "x": round(cx_du, 2),
                            "y": round(cy_du, 2),
                            "z": round(cz_du, 2),
                            "xText": format_feet_inches(cx_du),
                            "yText": format_feet_inches(cy_du),
                        },
                        "selection": sel,
                        "recorder": {
                            "state": self.recorder_state,
                            "actionCount": len(self.recorded_actions),
                        },
                        "uiEvents": events,
                    },
                    "revision": self.engine.revision,
                }

            if method == "get_drawing_info":
                return self.engine.get_drawing_info()

            if method == "run_commands":
                self.engine.watch_mode = bool(params.get("watch", False))
                res = self.engine.run_commands(
                    title=str(params.get("title") or "Blender commands"),
                    steps=list(params.get("steps") or []),
                )
                self._sync_known_handles()
                return res

            if method == "capture_view":
                w = int(params.get("width") or 960)
                h = int(params.get("height") or 640)
                return self.engine.capture_view(width=w, height=h)

            if method == "export_view_png":
                name = params.get("name")
                w = int(params.get("width") or 1920)
                h = int(params.get("height") or 1280)
                return self.engine.export_view_png(name=name, width=w, height=h)

            if method == "list_layers":
                return self.engine.list_layers()

            if method == "list_entities":
                return self.engine.list_entities(params)

            if method == "get_entity":
                return self.engine.get_entity(str(params.get("handle") or ""))

            if method == "get_sysvars":
                names = params.get("names") or []
                return self.engine.get_sysvars(list(names))

            if method == "apply_drawing_template":
                return self.engine.apply_drawing_template()

            if method == "undo":
                cnt = int(params.get("count") or 1)
                return self.engine.undo(cnt)

            if method == "cancel_command":
                return {
                    "success": True,
                    "message": "Cancelled active operation in Blender 5.3.",
                    "revision": self.engine.revision,
                }

            if method == "get_selection":
                return self.engine.get_selection()

            if method == "select_objects":
                return self.engine.select_objects(list(params.get("handles") or []))

            if method == "pick_point":
                cur = self.bpy.context.scene.cursor.location
                cx_du, cy_du, cz_du = meters_to_du(cur.x), meters_to_du(cur.y), meters_to_du(cur.z)
                pt = {
                    "x": round(cx_du, 2),
                    "y": round(cy_du, 2),
                    "z": round(cz_du, 2),
                    "xText": format_feet_inches(cx_du),
                    "yText": format_feet_inches(cy_du),
                }
                return {
                    "success": True,
                    "message": f"Picked 3D Cursor point ({pt['xText']}, {pt['yText']}) in Blender 5.3.",
                    "data": {"points": [pt]},
                    "revision": self.engine.revision,
                }

            if method == "get_drawing_snapshot":
                return {
                    "success": True,
                    "message": "Snapshot read.",
                    "data": self.engine.get_drawing_snapshot(),
                    "revision": self.engine.revision,
                }

            if method == "save_checkpoint_blend":
                target_path = str(params.get("path") or "")
                if target_path:
                    os.makedirs(os.path.dirname(os.path.abspath(target_path)), exist_ok=True)
                    self.bpy.ops.wm.save_as_mainfile(filepath=target_path, copy=True)
                return {"success": True, "message": f"Saved checkpoint to {target_path}.", "revision": self.engine.revision}

            if method == "recorder_control":
                action = str(params.get("action") or "")
                if action == "start":
                    self.recorder_state = "recording"
                    self.recorded_actions.clear()
                    self._sync_known_handles()
                elif action == "pause":
                    self.recorder_state = "paused"
                elif action == "resume":
                    self.recorder_state = "recording"
                elif action == "discard":
                    self.recorder_state = "off"
                    self.recorded_actions.clear()
                elif action == "stop":
                    self.recorder_state = "off"
                return {
                    "success": True,
                    "data": {
                        "state": self.recorder_state,
                        "actionCount": len(self.recorded_actions),
                        "actions": list(self.recorded_actions),
                    },
                    "revision": self.engine.revision,
                }

            return {
                "success": False,
                "message": f"Unknown Blender worker method '{method}'.",
                "errors": [f"Unknown method '{method}'."],
                "revision": self.engine.revision,
            }
        finally:
            self._copilot_busy = False

    # ------------------------------------------------------------------ "Learn from me" manual action recorder

    def _sync_known_handles(self) -> None:
        self._known_handles = {self.engine.get_handle(o) for o in self.bpy.context.scene.objects}
        try:
            self._last_op_count = len(self.bpy.context.window_manager.operators)
        except Exception:
            self._last_op_count = 0

    def _capture_manual_user_actions(self) -> None:
        if self.recorder_state != "recording" or self._copilot_busy:
            return
        for obj in self.bpy.context.scene.objects:
            h = self.engine.get_handle(obj)
            if h not in self._known_handles:
                self._known_handles.add(h)
                s = self.engine.summarize_object(obj)
                self.recorded_actions.append({
                    "kind": "object_created",
                    "command": f"ADD_{obj.type}",
                    "layer": s["layer"],
                    "summary": f"Created {obj.name} ({s['type']}) on {s['layer']}",
                })
