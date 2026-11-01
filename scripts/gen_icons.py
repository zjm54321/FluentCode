#!/usr/bin/env python3
"""Regenerate the approved FluentCode A-glass assets using rsvg-convert and ImageMagick.

Per-size SVG geometry comes from the approved 2026-11-01 icon proposal.
Taskbar status badges are separate assets and are intentionally unchanged.
"""
from pathlib import Path
import subprocess

ASSETS = Path(__file__).resolve().parents[1] / "src/FluentCode/Assets/AppIcon"
SIZES = (16, 20, 24, 32, 48, 256)

def rr(x0, y0, x1, y1, r):
    """Rounded-rect path (clockwise)."""
    return (f"M{x0+r},{y0} H{x1-r} A{r},{r} 0 0 1 {x1},{y0+r} V{y1-r} A{r},{r} 0 0 1 {x1-r},{y1} "
            f"H{x0+r} A{r},{r} 0 0 1 {x0},{y1-r} V{y0+r} A{r},{r} 0 0 1 {x0+r},{y0} Z")


# Frame geometry per grid: outer rect, radius, stroke thickness, inner radius, block rect, block radius.
# Proportions follow the OpenCode mark (tall rounded outline + filled lower block), squared up slightly
# so the shape fills a Windows 11 icon square without looking like a phone.
G = {
    16: dict(o=(2, 1, 14, 15), r=3, t=2, ir=1, b=(5, 8, 11, 12), br=0.75, sh=False, hl=False),
    20: dict(o=(3, 1, 17, 19), r=3.5, t=2, ir=1.5, b=(6, 10, 14, 16), br=1, sh=False, hl=True),
    24: dict(o=(3, 1, 21, 23), r=4, t=2.5, ir=1.5, b=(7, 12, 17, 19.5), br=1.25, sh=True, hl=True),
    32: dict(o=(5, 2, 27, 30), r=5.5, t=3, ir=2.5, b=(10, 15, 22, 25.5), br=1.5, sh=True, hl=True),
    48: dict(o=(7, 3, 41, 45), r=8, t=4.5, ir=3.5, b=(15, 22, 33, 37.5), br=2.5, sh=True, hl=True),
}

def frame_path(g):
    x0, y0, x1, y1 = g["o"]; t = g["t"]
    return rr(x0, y0, x1, y1, g["r"]) + " " + rr(x0 + t, y0 + t, x1 - t, y1 - t, g["ir"])


def svg_glass(n):
    """A · Glass (recommended): Fluent blue frame, frosted white page, deep-blue code block."""
    g = G[n]; x0, y0, x1, y1 = g["o"]; t = g["t"]; bx0, by0, bx1, by1 = g["b"]
    ix0, iy0, ix1, iy1 = x0 + t, y0 + t, x1 - t, y1 - t
    sw = max(0.5, n / 48)  # hairline in grid units ≈ 1px at 48, 0.5px floor
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{n}" height="{n}" viewBox="0 0 {n} {n}">', "<defs>",
             f'<linearGradient id="fr" x1="{x0}" y1="{y0}" x2="{x1}" y2="{y1}" gradientUnits="userSpaceOnUse">'
             '<stop offset="0" stop-color="#62CDFF"/><stop offset=".45" stop-color="#1C86DE"/><stop offset="1" stop-color="#0B3F8F"/></linearGradient>',
             f'<linearGradient id="pg" x1="0" y1="{iy0}" x2="0" y2="{iy1}" gradientUnits="userSpaceOnUse">'
             '<stop offset="0" stop-color="#FFFFFF"/><stop offset="1" stop-color="#DCEAF8"/></linearGradient>',
             f'<linearGradient id="bk" x1="{bx0}" y1="{by0}" x2="{bx1}" y2="{by1}" gradientUnits="userSpaceOnUse">'
             '<stop offset="0" stop-color="#2FA6F2"/><stop offset="1" stop-color="#0A4DA8"/></linearGradient>',
             f'<linearGradient id="hl" x1="0" y1="{y0}" x2="0" y2="{(y0+y1)/2}" gradientUnits="userSpaceOnUse">'
             '<stop offset="0" stop-color="#fff" stop-opacity=".55"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient>',
             f'<linearGradient id="is" x1="0" y1="{iy0}" x2="0" y2="{iy0 + max(1.5, t * .8)}" gradientUnits="userSpaceOnUse">'
             '<stop offset="0" stop-color="#0B3F8F" stop-opacity=".22"/><stop offset="1" stop-color="#0B3F8F" stop-opacity="0"/></linearGradient>',
             f'<filter id="sd" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="{max(0.5, n / 40)}"/></filter>',
             "</defs>"]
    if g["sh"]:
        parts.append(f'<path d="{rr(x0, y0 + n / 40, x1, y1 + n / 40, g["r"])}" fill="#001B44" opacity=".30" filter="url(#sd)"/>')
    parts.append(f'<path d="{rr(ix0 - .01, iy0 - .01, ix1 + .01, iy1 + .01, g["ir"])}" fill="url(#pg)"/>')
    if n >= 24:
        parts.append(f'<path d="{rr(ix0, iy0, ix1, iy1, g["ir"])}" fill="url(#is)"/>')
    parts.append(f'<path d="{frame_path(g)}" fill="url(#fr)" fill-rule="evenodd"/>')
    if g["hl"]:
        h = sw / 2
        parts.append(f'<path d="{rr(x0 + h, y0 + h, x1 - h, y1 - h, g["r"] - h)}" fill="none" stroke="url(#hl)" stroke-width="{sw}"/>')
    parts.append(f'<path d="{rr(bx0, by0, bx1, by1, g["br"])}" fill="url(#bk)"/>')
    if n >= 24:
        h = sw / 2
        parts.append(f'<path d="{rr(bx0 + h, by0 + h, bx1 - h, by1 - h, max(.1, g["br"] - h))}" fill="none" stroke="#fff" stroke-opacity=".16" stroke-width="{sw}"/>')
    parts.append("</svg>")
    return "\n".join(parts)


def main() -> None:
    """Render grid-fitted SVGs, PNGs, and the multi-resolution Windows icon."""
    ASSETS.mkdir(parents=True, exist_ok=True)
    for size in SIZES:
        source = svg_glass(min(size, 48))
        if size > 48:
            source = source.replace('width="48" height="48"', f'width="{size}" height="{size}"', 1)
        svg = ASSETS / f"app-{size}.svg"
        svg.write_text(source, encoding="utf-8")
        subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), str(svg), "-o", str(svg.with_suffix(".png"))], check=True)
    (ASSETS / "app.svg").write_text(svg_glass(48).replace('width="48" height="48"', 'width="256" height="256"', 1), encoding="utf-8")
    subprocess.run(["magick", *(str(ASSETS / f"app-{size}.png") for size in SIZES), str(ASSETS / "app.ico")], check=True)


if __name__ == "__main__":
    main()
