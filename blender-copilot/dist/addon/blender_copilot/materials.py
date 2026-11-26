"""
Layer / Collection and Principled BSDF Material management in Blender 5.3.
Maps standard architectural layers (A-WALL, A-DOOR, A-GLAZ, A-STRS, A-FURN, A-SITE,
A-ANNO-TEXT, A-ANNO-DIMS, A-ELEV, A-HATCH, A-ROOF, E-*, P-*) to Blender Collections
and rich Principled BSDF materials so every 3D architectural and modeling step looks crisp
in Blender's Solid, Material Preview, and Rendered viewport shading modes.
"""

from __future__ import annotations
from typing import Dict, Tuple, Any

# Standard architectural layer specs:
# name -> (RGBA tuple 0..1, roughness, metallic, transmission_alpha, auto_color_index, lineweight_mm)
LAYER_SPECS: Dict[str, Tuple[Tuple[float, float, float, float], float, float, float, int, float]] = {
    "0": ((0.85, 0.85, 0.85, 1.0), 0.6, 0.0, 1.0, 7, 0.25),
    "A-WALL": ((0.93, 0.91, 0.86, 1.0), 0.75, 0.0, 1.0, 7, 0.50),       # Warm architectural plaster wall
    "A-SLAB": ((0.74, 0.73, 0.71, 1.0), 0.80, 0.0, 1.0, 8, 0.35),       # Concrete floor slab
    "A-ROOF": ((0.68, 0.42, 0.32, 1.0), 0.65, 0.0, 1.0, 30, 0.35),      # Terracotta / roof slab
    "A-DOOR": ((0.55, 0.34, 0.19, 1.0), 0.40, 0.05, 1.0, 4, 0.25),      # Teak wood door
    "A-GLAZ": ((0.45, 0.78, 0.95, 0.38), 0.08, 0.10, 0.38, 5, 0.25),    # Architectural glass
    "A-STRS": ((0.80, 0.77, 0.72, 1.0), 0.65, 0.0, 1.0, 4, 0.25),       # Stone / concrete staircase
    "A-FURN": ((0.64, 0.52, 0.40, 1.0), 0.50, 0.05, 1.0, 6, 0.18),      # Warm furniture wood/fabric
    "A-ANNO-TEXT": ((0.98, 0.98, 0.98, 1.0), 0.30, 0.0, 1.0, 7, 0.25),  # Crisp white room text
    "A-ANNO-DIMS": ((0.28, 0.85, 0.45, 1.0), 0.30, 0.0, 1.0, 3, 0.18),  # Architectural green dimensions
    "A-SITE": ((0.48, 0.58, 0.42, 1.0), 0.85, 0.0, 1.0, 2, 0.35),       # Site / plot boundary
    "A-PLOT": ((0.55, 0.60, 0.65, 1.0), 0.80, 0.0, 1.0, 8, 0.25),       # Plot boundary line
    "A-GRID": ((0.85, 0.30, 0.30, 1.0), 0.50, 0.0, 1.0, 1, 0.18),       # Grid lines
    "A-ELEV": ((0.88, 0.87, 0.84, 1.0), 0.70, 0.0, 1.0, 7, 0.35),       # Elevation lines
    "A-HATCH": ((0.76, 0.74, 0.70, 1.0), 0.75, 0.0, 1.0, 8, 0.13),      # Hatching / floor tile fill
    "A-TTLB": ((0.92, 0.92, 0.92, 1.0), 0.40, 0.0, 1.0, 7, 0.35),       # Title block
    "A-VPRT": ((0.50, 0.50, 0.50, 1.0), 0.50, 0.0, 1.0, 8, 0.13),       # Viewports
    "E-LITE": ((1.00, 0.85, 0.25, 1.0), 0.25, 0.1, 1.0, 2, 0.25),       # Electrical lighting
    "E-POWR": ((0.95, 0.35, 0.25, 1.0), 0.35, 0.1, 1.0, 1, 0.25),       # Electrical power
    "E-SWCH": ((0.30, 0.85, 0.95, 1.0), 0.35, 0.1, 1.0, 4, 0.25),       # Electrical switches
    "E-ANNO": ((0.95, 0.90, 0.60, 1.0), 0.35, 0.0, 1.0, 2, 0.18),       # Electrical notes
    "P-FIXT": ((0.92, 0.95, 0.98, 1.0), 0.20, 0.05, 1.0, 5, 0.25),      # Plumbing ceramic fixtures
    "P-WATR": ((0.20, 0.55, 0.95, 1.0), 0.35, 0.2, 1.0, 5, 0.25),       # Water supply pipes
    "P-DRAN": ((0.40, 0.75, 0.35, 1.0), 0.45, 0.1, 1.0, 3, 0.35),       # Drainage pipes
    "P-ANNO": ((0.70, 0.90, 0.98, 1.0), 0.35, 0.0, 1.0, 4, 0.18),       # Plumbing notes
}

# AutoCAD ACI color index (1..9) to RGBA
ACI_COLORS: Dict[int, Tuple[float, float, float, float]] = {
    1: (0.92, 0.25, 0.25, 1.0),  # Red
    2: (0.95, 0.85, 0.25, 1.0),  # Yellow
    3: (0.28, 0.85, 0.45, 1.0),  # Green
    4: (0.30, 0.85, 0.92, 1.0),  # Cyan
    5: (0.35, 0.55, 0.95, 1.0),  # Blue
    6: (0.85, 0.40, 0.88, 1.0),  # Magenta
    7: (0.92, 0.90, 0.86, 1.0),  # White / Architectural Cream
    8: (0.55, 0.57, 0.60, 1.0),  # Grey
    9: (0.75, 0.76, 0.78, 1.0),  # Light Grey
}


def parse_hex_color(text: str) -> Tuple[float, float, float, float]:
    """Parse '#RRGGBB', 'RRGGBB', 'r,g,b', or an ACI index into RGBA 0..1."""
    s = str(text or "").strip().lstrip("#")
    if not s:
        return (0.85, 0.85, 0.85, 1.0)
    if "," in s:
        parts = [float(p.strip()) for p in s.split(",")]
        if len(parts) >= 3:
            scale = 255.0 if any(v > 1.0 for v in parts[:3]) else 1.0
            a = (parts[3] / scale) if len(parts) >= 4 else 1.0
            return (parts[0] / scale, parts[1] / scale, parts[2] / scale, a)
    if s.isdigit():
        aci = int(s)
        return ACI_COLORS.get(aci, (0.85, 0.85, 0.85, 1.0))
    if len(s) == 6:
        try:
            r = int(s[0:2], 16) / 255.0
            g = int(s[2:4], 16) / 255.0
            b = int(s[4:6], 16) / 255.0
            return (r, g, b, 1.0)
        except ValueError:
            pass
    return (0.85, 0.85, 0.85, 1.0)


def ensure_collection(bpy_mod: Any, layer_name: str) -> Any:
    """Ensure a Blender Collection exists for `layer_name` and is linked to the active scene."""
    name = (layer_name or "0").strip() or "0"
    scene = bpy_mod.context.scene
    coll = bpy_mod.data.collections.get(name)
    if coll is None:
        coll = bpy_mod.data.collections.new(name)
        scene.collection.children.link(coll)
        spec = LAYER_SPECS.get(name.upper(), LAYER_SPECS["0"])
        coll["acad_color"] = spec[4]
        coll["acad_lineweight"] = spec[5]
        coll["acad_linetype"] = "CENTER" if name.upper() == "A-GRID" else "Continuous"
    else:
        if coll.name not in scene.collection.children:
            try:
                scene.collection.children.link(coll)
            except RuntimeError:
                pass
    return coll


def ensure_material(
    bpy_mod: Any,
    mat_name: str,
    rgba: Tuple[float, float, float, float] = (0.85, 0.85, 0.85, 1.0),
    roughness: float = 0.6,
    metallic: float = 0.0,
) -> Any:
    """Create or update a Blender 5.3 Principled BSDF material and viewport color."""
    mat = bpy_mod.data.materials.get(mat_name)
    if mat is None:
        mat = bpy_mod.data.materials.new(name=mat_name)
        mat.use_nodes = True

    mat.diffuse_color = rgba
    mat.roughness = roughness
    mat.metallic = metallic

    if mat.use_nodes and mat.node_tree:
        bsdf = None
        for node in mat.node_tree.nodes:
            if node.type == "BSDF_PRINCIPLED":
                bsdf = node
                break
        if bsdf is not None:
            if "Base Color" in bsdf.inputs:
                bsdf.inputs["Base Color"].default_value = rgba
            if "Roughness" in bsdf.inputs:
                bsdf.inputs["Roughness"].default_value = roughness
            if "Metallic" in bsdf.inputs:
                bsdf.inputs["Metallic"].default_value = metallic
            if rgba[3] < 0.99:
                if "Alpha" in bsdf.inputs:
                    bsdf.inputs["Alpha"].default_value = rgba[3]
                if "Transmission Weight" in bsdf.inputs:
                    bsdf.inputs["Transmission Weight"].default_value = 1.0 - rgba[3]
    return mat


def ensure_layer_material(bpy_mod: Any, layer_name: str) -> Any:
    """Get or create the default Principled BSDF material for an architectural layer."""
    key = (layer_name or "0").strip().upper()
    spec = LAYER_SPECS.get(key, LAYER_SPECS["0"])
    rgba, roughness, metallic, _, _, _ = spec
    return ensure_material(bpy_mod, f"Mat_{key}", rgba=rgba, roughness=roughness, metallic=metallic)


def assign_object_to_layer(bpy_mod: Any, obj: Any, layer_name: str) -> None:
    """Place `obj` inside the Collection for `layer_name` and assign its material & viewport color."""
    coll = ensure_collection(bpy_mod, layer_name)
    for existing in list(obj.users_collection):
        existing.objects.unlink(obj)
    coll.objects.link(obj)
    obj["acad_layer"] = (layer_name or "0").strip()

    mat = ensure_layer_material(bpy_mod, layer_name)
    obj.color = mat.diffuse_color
    if hasattr(obj.data, "materials") and obj.data is not None:
        if len(obj.data.materials) == 0:
            obj.data.materials.append(mat)
        else:
            obj.data.materials[0] = mat
