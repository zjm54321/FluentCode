# Showcase provenance

These eight `demo-showcase-*.oui` programs are **FluentCode showcase demos modelled on the OIU component catalog**, not upstream gallery ports.

The component names, positional contracts and composition patterns were checked against the locally cached OpenIntelligentUI sources (MIT, v0.1.1), from https://github.com/thesysdev/open-intelligent-ui. Scratch references: `/tmp/opencode/oiu/` and `/tmp/opencode/nui1-upstream-*.tsx`. All scenario text, data and programs here were newly composed for FluentCode.

| Demo | Coverage |
| --- | --- |
| analytics | Four metric cards, six chart series, rich table cells, period choice, popover and footer action |
| onboarding | Three-step form, descriptions, date and select inputs, radio cards, notes and review summary |
| directory | Filtered list, selectable profiles, detail panel, secondary text, badges and contextual actions |
| explorer | Segmented tabs, expandable question, three-item carousel, destination selection and itinerary |
| journey | Timeline, custom markers, descriptions, owner notes, status badges and release confirmation |
| media | Explicit-load image, image-search and video preview cards, descriptions and safety footers |
| planner | Stateful budget tool, slider/input/radio/checkbox, live donut, table, notes and assistant action |
| learning | Flashcards, timer, native vector primitives, code, math source and celebratory feedback |

## Upstream gallery ports (`upstream/demo-oiu-*.oui`)

These 21 programs are **ports of the upstream OIU `/gallery` programs**, copied byte for byte from OpenIntelligentUI (MIT, package 0.1.1). They come from https://github.com/thesysdev/open-intelligent-ui at commit `9df2e64bc44282f6305a47eb6dbbbd8f28139138` (2026-11-06). Each one passes the official oracle: the lang-core 1.1.0 parser plus the QoL `native_ui` validator, with no diagnostics. `generate-expected.ts` and `Program.cs` cover them along with the parent fixtures. They are named `demo-oiu-*` so that `capture-demos.ps1 -FixturesRoot tests/native-ui/fixtures/upstream` can render them; the default demo capture does not include them.

| Fixture | Upstream source (gallery id) | Change |
| --- | --- | --- |
| answer, text, lists, buttons, layout, gdp, segmented, checklist, pressable, inputs, svg, chart, table, images, map, timer, icons | `src/app/gallery/programs.ts` | `text`: the `Link("Unsafe links render as text", "javascript:alert(1)")` is removed, because the validator rejects every non-http(s) URL before a card is sent. All other programs are unchanged. |
| compare-phones, flashcards, primitives | `src/app/gallery/composition-programs.ts` | unchanged |
| example-trip | `src/lib/prompt-options.ts` (`exampleTrip`) | unchanged |

The other 24 gallery programs cannot be sent as written. The validator rejects them, so they never reach the client:

- **HtmlView (excluded from the catalog).** `html` and `game`. These two have no port.
- **Over the limits.** `route` exceeds the 4,096 expanded nodes limit and also uses OIU-only `@functions`.
- **OIU-only `@functions`.** These programs use upstream functions beyond the lang-core built-ins, such as `@Format`, `@Range`, `@Clamp`, `@Includes`, `@Toggle` and `@MixColor`: `heatmap`, `web-analytics`, `recipe`, `bill`, `monty`, `savings`, `gdp-explained`, `clt`, `bicycle`, `bike-repair`, `roadtrip`, `room`, `origami`, `garden`, `wardrobe`, `drones`, `example-list` and `example-steps`.
- **OIU-only `@functions` plus demo data.** `dash-sales`, `dash-support` and `example-dashboard` also need `Query` with upstream demo-data tools.

## Snapshot ports (`demo-oiu-snap-*.oui`)

The 22 programs above, all except `html` and `game`, are ported as **initial-state snapshots**. Each upstream program was evaluated with the upstream runtime: `@openuidev/lang-core` 1.0.0, the OIU library functions, and the upstream `demoTools` for `Query`. This matches what the OIU `Renderer` does on first paint. The evaluated tree was then printed back to OpenUI Lang with lang-core 1.1.0 `jsonToOpenUI`:

- `$variables` that are bound to inputs are kept with their initial values, so sliders, inputs and segmented controls still work locally.
- Computed values, `@Each`/`@Range` expansions and `Query` results become literals.
- Actions are dropped (`null`). The snapshot shows the first frame and reacts to its own inputs. It does not recompute derived numbers.
- Date-dependent data (`web-analytics`, `dash-*`) is as of 2026-11-08, the same day as the reference renders.

All 22 pass the official oracle (lang-core 1.1.0 parser plus the QoL `native_ui` validator) with no diagnostics. The tool that makes them is kept with the parity evidence: `snapshot.ts` under `artifacts/native-ui-parity-*/tools/`, which is not committed.

| Folder | Fixtures | Status |
| --- | --- | --- |
| `upstream/` | `bill`, `clt`, `dash-sales`, `dash-support`, `drones`, `example-dashboard`, `example-list`, `example-steps`, `gdp-explained`, `monty`, `origami`, `savings` | Parse, runtime and streaming match the oracle |
| `upstream-blocked/` | `bicycle`, `bike-repair`, `garden`, `heatmap`, `recipe`, `roadtrip`, `room`, `route`, `wardrobe`, `web-analytics` | Parse and streaming match the oracle, but FluentCode's runtime **refuses** them (P0, see below) |

**P0, still open: runtime node limit.** `NativeUiSafety.Check` (`src/FluentCode/Services/NativeUi/NativeUiRuntime.cs`) has no case for an ordinary string. Strings therefore fall through to the `IEnumerable` case, and every character is counted as a node. So a card with roughly 3,000 characters of text goes over the 4,096 node limit and shows "Native UI value exceeds node/depth limits.", even though the official validator accepts it. Measured with the runtime's own count: room 4,105, recipe 4,435, bicycle 4,725, bike-repair 4,754, route 5,410, wardrobe 6,010, heatmap 6,251, roadtrip 6,456, garden 7,235 and web-analytics 11,115. The largest accepted port is drones at 3,846.

`Program.cs` requires each blocked fixture to keep failing. When the runtime fix lands, the test will name the fixture to move back to `upstream/`.

## Parity check

The gallery parity check against upstream examples ran on 2026-11-08. Reference renders came from the upstream gallery in headless Chrome, using the pinned upstream packages. The results, including the P0/P1/P2 ratings and which items are fixed, are in `docs/native-ui.md` under "Upstream gallery parity".
