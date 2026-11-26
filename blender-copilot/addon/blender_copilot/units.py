"""
Feet-inch, metric, and relative coordinate parsing & formatting for AI Copilot in Blender 5.3.
Maintains full compatibility with AutoCAD architectural units (1 drawing unit = 1 inch,
displayed as feet-inches like 12'-6") while converting smoothly to Blender 3D coordinates
(1 Blender meter = 39.37007874 inches, or 1 inch = 0.0254 meters).
"""

from __future__ import annotations
import math
import re
from typing import Tuple, Optional

INCHES_TO_METERS = 0.0254
METERS_TO_INCHES = 1.0 / INCHES_TO_METERS


def du_to_meters(du_inches: float) -> float:
    """Convert drawing units (inches) to Blender meters."""
    return float(du_inches) * INCHES_TO_METERS


def meters_to_du(meters: float) -> float:
    """Convert Blender meters to drawing units (inches)."""
    return float(meters) * METERS_TO_INCHES


def format_feet_inches(inches: float) -> str:
    """Format drawing units (inches) as architectural feet-inches, e.g. 150 -> 12'-6\"."""
    if not math.isfinite(inches):
        return "0\""
    sign = "-" if inches < -1e-6 else ""
    val = abs(inches)
    whole_quarters = round(val * 4.0)
    feet = int(whole_quarters // 48)
    rem_quarters = int(whole_quarters % 48)
    whole_inches = rem_quarters // 4
    frac_quarters = rem_quarters % 4

    frac_str = ""
    if frac_quarters == 1:
        frac_str = " 1/4"
    elif frac_quarters == 2:
        frac_str = " 1/2"
    elif frac_quarters == 3:
        frac_str = " 3/4"

    if feet == 0:
        if whole_inches == 0 and frac_str:
            return f"{sign}{frac_str.strip()}\""
        return f"{sign}{whole_inches}{frac_str}\""
    return f"{sign}{feet}'-{whole_inches}{frac_str}\""


_FRAC_RE = re.compile(r"^([+-]?\d+)\s*[- ]\s*(\d+)\s*/\s*(\d+)$")
_ONLY_FRAC_RE = re.compile(r"^([+-]?\d+)\s*/\s*(\d+)$")


def _parse_inch_part(text: str) -> float:
    s = text.strip().rstrip('"').strip()
    if not s:
        return 0.0
    m = _FRAC_RE.match(s)
    if m:
        whole = float(m.group(1))
        num = float(m.group(2))
        den = float(m.group(3))
        if den != 0:
            return whole + (math.copysign(num / den, whole if whole != 0 else 1.0))
    m2 = _ONLY_FRAC_RE.match(s)
    if m2:
        num = float(m2.group(1))
        den = float(m2.group(2))
        return num / den if den != 0 else 0.0
    return float(s)


def parse_length(text: str | float | int, default_is_inches: bool = True) -> float:
    """
    Parse a length string into drawing units (inches).
    Accepts:
      - "30'" -> 360.0
      - "12'-6\"" or "12'6" or "12'-6 1/2\"" -> 150.0 / 150.5
      - "9\"" or "9" -> 9.0
      - "4-1/2" or "4.5" -> 4.5
      - "3.5m" -> 137.795 (meters suffix)
      - "300mm" -> 11.811 (mm suffix)
      - "100cm" -> 39.37 (cm suffix)
    """
    if isinstance(text, (int, float)):
        return float(text)
    s = str(text or "").strip()
    if not s:
        return 0.0
    # Normalize unicode quotes/dashes
    s = (
        s.replace("\u2019", "'")
        .replace("\u2018", "'")
        .replace("\u2032", "'")
        .replace("\u201d", '"')
        .replace("\u201c", '"')
        .replace("\u2033", '"')
        .replace("\u2013", "-")
        .replace("\u2014", "-")
        .replace("\u2212", "-")
    )

    low = s.lower()
    if low.endswith("mm"):
        return float(low[:-2].strip()) / 25.4
    if low.endswith("cm"):
        return float(low[:-2].strip()) / 2.54
    if low.endswith("m") and not low.endswith("in"):
        try:
            return float(low[:-1].strip()) * METERS_TO_INCHES
        except ValueError:
            pass
    if low.endswith("ft"):
        return float(low[:-2].strip()) * 12.0
    if low.endswith("in"):
        return _parse_inch_part(low[:-2].strip())

    if "'" in s:
        sign = -1.0 if s.startswith("-") else 1.0
        clean = s[1:].strip() if s[0] in "+-" else s
        parts = clean.split("'", 1)
        feet = float(parts[0].strip() or "0")
        rest = parts[1].strip()
        if rest.startswith("-"):
            rest = rest[1:].strip()
        inches = _parse_inch_part(rest) if rest else 0.0
        return sign * (feet * 12.0 + inches)

    if s.endswith('"'):
        return _parse_inch_part(s[:-1])

    val = _parse_inch_part(s)
    return val if default_is_inches else val * METERS_TO_INCHES


def parse_point(
    text: str,
    last_point: Tuple[float, float, float] = (0.0, 0.0, 0.0),
) -> Optional[Tuple[float, float, float]]:
    """
    Parse a 2D or 3D point in drawing units (inches):
      - "10',20'" or "120,240" or "10',20',10'6"
      - "@26',0" (relative cartesian)
      - "@30'<90" (relative polar in degrees CCW from +X)
    Returns (x, y, z) in drawing units (inches), or None if not a valid point.
    """
    if not text:
        return None
    s = str(text).strip()
    if s.startswith("(") and s.endswith(")"):
        s = s[1:-1].strip()
    if not s:
        return None

    relative = False
    if s.startswith("@"):
        relative = True
        s = s[1:].strip()

    try:
        if "<" in s:
            dist_s, ang_s = s.split("<", 1)
            dist = parse_length(dist_s.strip())
            ang_deg = float(ang_s.strip().rstrip("°dD"))
            rad = math.radians(ang_deg)
            dx = dist * math.cos(rad)
            dy = dist * math.sin(rad)
            if relative:
                return (last_point[0] + dx, last_point[1] + dy, last_point[2])
            return (dx, dy, 0.0)

        parts = [p.strip() for p in s.split(",")]
        if len(parts) < 2 or len(parts) > 3:
            return None
        x = parse_length(parts[0])
        y = parse_length(parts[1])
        z = parse_length(parts[2]) if len(parts) == 3 else (0.0 if relative else last_point[2])
        if relative:
            return (last_point[0] + x, last_point[1] + y, last_point[2] + z)
        return (x, y, z if len(parts) == 3 else 0.0)
    except (ValueError, IndexError):
        return None
