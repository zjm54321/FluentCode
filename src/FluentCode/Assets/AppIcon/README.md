# FluentCode application icon — A · Glass

Approved artwork: `artifacts/fluentcode-design-20261101-033458/icon/A-glass/`.
Blue rounded frame, frosted page and deep-blue lower block; per-size SVG/PNG
assets and the multi-resolution ICO are copied from that approved proposal.
ICO frames: 16, 20, 24, 32, 48 and 256 pixels.

With Python 3, `rsvg-convert` and ImageMagick `magick` installed, rebuild from
the repository root with `python3 scripts/gen_icons.py`. Status badges in
`Assets/Taskbar` are separate and are intentionally unchanged.

The executable resource, AppWindow and tray share `app.ico`; toast registration
and app-logo overrides use `app-48.png`. No application splash/About image
surface currently exists (composer “splash” is placeholder text, not an image).
The 256 PNG remains available. ICO/PNG assets are copied into build/publish output.
