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

The other 24 gallery programs are not ported as programs:

- **HtmlView (excluded from the catalog).** `html` and `game`.
- **Over the limits.** `route` exceeds the 4,096 expanded nodes limit and also uses OIU-only `@functions`.
- **OIU-only `@functions`.** These programs use upstream functions beyond the lang-core built-ins, such as `@Format`, `@Range`, `@Clamp`, `@Includes`, `@Toggle` and `@MixColor`: `heatmap`, `web-analytics`, `recipe`, `bill`, `monty`, `savings`, `gdp-explained`, `clt`, `bicycle`, `bike-repair`, `roadtrip`, `room`, `origami`, `garden`, `wardrobe`, `drones`, `example-list` and `example-steps`.
- **OIU-only `@functions` plus demo data.** `dash-sales`, `dash-support` and `example-dashboard` also need `Query` with upstream demo-data tools.

The validator rejects all of these, so they never reach the client.
