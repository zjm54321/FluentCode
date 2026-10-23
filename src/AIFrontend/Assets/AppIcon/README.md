# AIFrontend application icon (TB1)

Original project artwork adopted from the approved v3 taskbar proposal,
`gen_taskbar.py:proposed_app_icon`. `app.svg` retains its exact geometry and
colors: Fluent rounded gradient tile, inner highlight, white chat bubble and
accent sparkle. No external icon or trademark is used.

`app.ico` contains 16, 20, 24, 32, 40, 48, 64 and 256 px RGBA frames.
The small `app-{16,20,24,32}.svg` sources grid-fit the tile and bubble's
horizontal/vertical boundaries to native pixels and use a one-pixel inner
highlight. The sparkle remains the approved vector shape. Larger frames use
the unmodified master. Every frame is rasterized separately, not resized from
a single bitmap.

Rebuild the app icon and status badges from the repository root:

```sh
nix shell nixpkgs#imagemagick nixpkgs#librsvg --command python3 scripts/generate-app-icons.py
```

The executable resource and `AppWindow.SetIcon` both use `app.ico`; the project
copies it to output and publish directories.
