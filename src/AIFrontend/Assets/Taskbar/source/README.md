# TB1 circular status badges

`composed/{light,dark}-taskbar/{attention,completed,failed}_{16,20,24,32}.svg`
are the current ICO sources, replacing the former bare Fluent glyphs. Shapes
and colors come directly from the approved v3 `gen_taskbar.py:proposed_badge`:

| Taskbar | Attention / glyph | Completed / glyph | Failed / glyph | Ring |
|---|---|---|---|---|
| Light | #F7B500 / #1A1A1A | #0F7B0F / #FFFFFF | #C42B1C / #FFFFFF | #F3F3F3 |
| Dark | #FCE100 / #1A1A1A | #6CCB5F / #1A1A1A | #FF99A4 / #1A1A1A | #202020 |

The approved circle radii are 7.6 and 6.5 in the 16-unit viewBox: an
approximately one-logical-pixel taskbar-colored separator ring that scales
with DPI. The glyphs are an exclamation mark, check and cross. The surrounding
canvas is transparent; each ICO contains 16 / 20 / 24 / 32 px RGBA frames.
Working remains native progress with no overlay. Paths, state mapping and
DPI/theme selection are unchanged.

These are original project artwork, not modified Microsoft SVG paths.
`official/` retains the previous Fluent UI System Icons sources for historical
attribution only; they are no longer inputs to the shipped overlay art.
Their MIT license remains at `licenses/fluentui-system-icons-LICENSE.txt`.

Rebuild using `scripts/generate-app-icons.py` as described in
`Assets/AppIcon/README.md`. The old `docs/taskbar-icons-source.md` records the
superseded bare-glyph delivery, including its old hashes and colors.
