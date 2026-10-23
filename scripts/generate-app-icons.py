#!/usr/bin/env python3
"""Rebuild TB1 icon assets with rsvg-convert and ImageMagick, without UI/build tools."""
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/AIFrontend/Assets"
APP_SIZES = (16, 20, 24, 32, 40, 48, 64, 256)
BADGE_SIZES = (16, 20, 24, 32)
COLORS = {
    "light": ("#F3F3F3", {"attention": ("#F7B500", "#1A1A1A"), "completed": ("#0F7B0F", "#FFFFFF"), "failed": ("#C42B1C", "#FFFFFF")}),
    "dark": ("#202020", {"attention": ("#FCE100", "#1A1A1A"), "completed": ("#6CCB5F", "#1A1A1A"), "failed": ("#FF99A4", "#1A1A1A")}),
}


def small_app(size):
    """Grid-fit the approved tile/bubble edges; retain the exact sparkle and colors."""
    scale = size / 48
    snap = lambda n: int(n * scale + .5)
    tree = ET.parse(ASSETS / "AppIcon/app.svg")
    svg = tree.getroot()
    svg.set("viewBox", f"0 0 {size} {size}")
    svg.set("width", str(size))
    svg.set("height", str(size))
    tile, highlight, bubble, sparkle = list(svg)[1:]
    edge, radius = snap(3), snap(10)
    tile.attrib.update(x=str(edge), y=str(edge), width=str(size - 2 * edge), height=str(size - 2 * edge), rx=str(radius))
    highlight.attrib.update(x=str(edge + .5), y=str(edge + .5), width=str(size - 2 * edge - 1), height=str(size - 2 * edge - 1), rx=str(radius - .5))
    x0, x1, x2, x3, tail = map(snap, (13, 17, 31, 35, 22.5))
    y0, y1, y2, y3, y4 = map(snap, (11.5, 15.5, 25.5, 29.5, 34.5))
    r = snap(4)
    bubble.set("d", f"M{x0} {y1}A{r} {r} 0 0 1 {x1} {y0}H{x2}A{r} {r} 0 0 1 {x3} {y1}V{y2}A{r} {r} 0 0 1 {x2} {y3}H{tail}L{x1} {y4}V{y3}A{r} {r} 0 0 1 {x0} {y2}Z")
    sparkle.set("transform", f"scale({scale})")
    ET.register_namespace("", "http://www.w3.org/2000/svg")
    path = ASSETS / f"AppIcon/app-{size}.svg"
    tree.write(path, encoding="unicode")
    return path


def badge(size, status, theme):
    # Geometry/colors verbatim from v3/gen_taskbar.py proposed_badge (TB1).
    ring, colors = COLORS[theme]
    fill, glyph = colors[status]
    if status == "completed":
        mark = f'<path d="M5.2 8.3l2 2 3.9-4.3" fill="none" stroke="{glyph}" stroke-width="1.612" stroke-linecap="round" stroke-linejoin="round"/>'
    elif status == "failed":
        mark = f'<path d="M5.6 5.6l4.8 4.8M10.4 5.6l-4.8 4.8" stroke="{glyph}" stroke-width="1.56" stroke-linecap="round"/>'
    else:
        mark = f'<path d="M8 4.4v4.4" stroke="{glyph}" stroke-width="1.612" stroke-linecap="round"/><circle cx="8" cy="11.3" r="1.05" fill="{glyph}"/>'
    path = ASSETS / f"Taskbar/source/composed/{theme}-taskbar/{status}_{size}.svg"
    path.write_text(f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 16 16"><circle cx="8" cy="8" r="7.6" fill="{ring}"/><circle cx="8" cy="8" r="6.5" fill="{fill}"/>{mark}</svg>\n')
    return path


def pack(sources, output, temp):
    frames = []
    for size, svg in sources:
        png = temp / f"frame-{size}.png"
        subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), "-o", str(png), str(svg)], check=True)
        frames.append(str(png))
    subprocess.run(["magick", *frames, "-background", "none", "-alpha", "on", "-depth", "8", str(output)], check=True)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="aif-icons-") as temp:
        temp = Path(temp)
        pack([(size, small_app(size) if size <= 32 else ASSETS / "AppIcon/app.svg") for size in APP_SIZES], ASSETS / "AppIcon/app.ico", temp)
        for theme in COLORS:
            for status in COLORS[theme][1]:
                pack([(size, badge(size, status, theme)) for size in BADGE_SIZES], ASSETS / f"Taskbar/{theme}-taskbar/taskbar-{status}.ico", temp)
