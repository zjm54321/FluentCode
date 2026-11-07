# Native OpenUI Lang v1

The `native_ui` tool in the **opencode-qol native-ui lane** accepts `{source, title?}`. Server validation uses official MIT `@openuidev/lang-core` **1.1.0**, with a Zod catalog preserving OpenIntelligentUI's names and positional prop order. Success is a short acknowledgement; the program remains in tool `state.input` for history replay.

The always-on description is a compact grammar/positional component index with two examples. Primary agents can call `native_ui_reference({components:["Chart","Table"]})` for complete prop schemas, native behavior and examples; validation diagnostics direct them there. Both tools are denied to non-primary agents. The generated catalog is `fluentcode-native-ui@0.2.0`: all 54 upstream names except HtmlView. Measured with o200k_base: native_ui **1,042 tokens**, reference tool description **45**, combined always-on **1,087**; full expanded reference **5,219**.

## Contract

`src/FluentCode/Services/NativeUi/catalog.json` is the embedded byte-identical copy of QoL's generated manifest (`kernel/plugins/native-ui/scripts/generate-catalog.ts`). It supplies component/prop order, JSON Schema types, reactive flags and resource limits. The server's focused Bun test checks equality between the two lanes.

The C# port implements the complete lexer/Pratt expression grammar, assignments, references/forward references, root/fallback entry, schema-aware lowering, partial-input auto-close, comment/fence handling and the streaming completed-statement watermark. The shared corpus compares complete parse JSON, evaluated defaults and streaming snapshots against lang-core 1.1.0. Duplicate definitions, unresolved/cyclic references and schema errors follow the official parser.

The local runtime has `$variables`, reactive `$value` assignments, ordered `Action([...])`, `@Set`/`@Reset`, arithmetic/comparison/boolean operators, ternaries, member/index access and the core built-ins (including Count, Sum, Round and Each). Each captures iterator values for deferred actions. Query/Mutation/defaults are parsed; no tools or network are invoked. Their cards/actions say **未支持**. @Run stops an unsupported action sequence. @OpenUrl and Link allow only absolute http(s) URLs and show the **entire URL** in a confirmation flyout before the system browser opens. Cancelling stops the remaining ordered actions. Native flyouts suppress terminal airspace while open.

Limits in both lanes: **65,536 source characters, 4,096 nodes, depth 64, 16,384 string characters**. Expansion and evaluation are bounded as well as lexing. URL data is restricted to http(s). Cards have no script/reflection/HTML/file capability. Images never fetch automatically: their full HTTPS URL and a **点击加载** button appear first. Explicit loads are bounded to 8 MB/30 seconds/5 redirects, with HTTPS checked at every redirect. The reused Markdown presenter has an explicit sandbox policy: no inherited transcript image scope, link/attachment chips or Mermaid browser rendering.

## Native UI and composer

The original fourteen components retain their visuals in `Pages/OpenCode/NativeUi/NativeUiTheme.cs`; new companion renderers use its tokens plus `NativeUiTheme.Components.cs`. Structural-path reconciliation retains input/caret and slider control identity during state updates. Ask **@designer** with the relevant renderer/theme file and visual goal for layout/styling changes.

- Chart supports bar/line/area/pie/donut/scatter and number/currency/percent formatting, including stacked bar/area, via LiveCharts2 2.0.5 (MIT). Metrics compose Card/Title/Caption; upstream has no separate Stat/Metric component.
- Table uses WinUI.TableView 1.5.1 (MIT), with native sortable/resizable columns and arbitrary component cells. SegmentedControl and radio pills use CommunityToolkit Segmented 8.2.251219 (MIT).
- Forms include TextArea, radio variants, CalendarDatePicker, timer (pauses on unload), IconButton and keyboard-accessible Pressable. Box/Grid/Form/Spacer/Carousel/Popover use native panels, scrolling and flyouts.
- List/Quote/Code/Flashcard/Celebration and Canvas/XAML drawing primitives are native. Math intentionally displays formula source in v1. Icon names map to Segoe Fluent Icons (unknown names use the information glyph).
- Map/MapMarker are static location cards, with selected state and confirmed OpenStreetMap external links; no Azure dependency. SearchImage and YouTube are static native cards plus confirmed external search/video links, without automatic thumbnails or embedded players.

`native_ui` tool parts are answer cards outside generic collapsed tool groups. `tool.input.delta` retains a placeholder even when a JSON prefix already looks complete; the final `input.ended` marker permits rendering. Parse/validation failures are compact error cards. Tool progress/success, transcript virtualization and reopening the same session retain variables, form values and flashcard face **during the same app run**, keyed by globally unique message part ID and exact source. The app-run store holds plain values only, not controls/timers/host callbacks. Restarts return to declared defaults; nothing is written to disk.

@ToAssistant is only a user intent. The page verifies the live transcript/source/session and clean composer, preserves existing drafts, reconciles the ordinary editor, then invokes **RequestComposerSubmission**. The normal shared interaction lease/send workflow remains the only transport route. An agent working receives **queue** delivery; unavailable queue, permission/protection, stale origin, shell mode or existing draft blocks the intent. Command-leading `!`/`/` messages cannot turn a card into code execution. Context/form-state data accompanies the ordinary prompt as labeled JSON, not a separate RPC call.

## Focused verification and demos

1. In FluentCode: `bun tests/native-ui/generate-expected.ts /home/ming/opencode-qol-lanes/native-ui` regenerates the official shared oracle.
2. In QoL: `bun test kernel/tests/qol-native-ui.test.ts kernel/tests/qol-tui-defaults.test.ts`.
3. In Windows PowerShell with .NET 10 on PATH: `tests/native-ui/run.ps1` (also registered in `tests/run-suite.py`; do not run the full suite for this slice).
4. `scripts/build-debug.ps1` builds the lane client. Its ignored prepared browser runtime is copied read-only from main; no prepare/download or backend change is needed.
5. `tests/native-ui/capture-demos.ps1 -AppPath <lane published FluentCode.exe> -EvidenceRoot <NEW artifacts/native-ui-v1-UTC>` runs only an isolated **offline showcase**, not the native/full-suite harness. It waits up to 20 minutes for RichEditPrototype to exit, checks every 60 seconds and never sends desktop input. It captures dashboard/form/mixed-layout in light/dark plus `demos.json`, exercising native form changes, assistant intent and same-part disposal/recreation state. No live backend is contacted. Never commit artifacts.

Remaining phases: OpenIntelligentUI's extra pure functions, query/mutation execution and permissions, typeset math, richer media/search services, cross-restart state and the designer's final visual specification. Percentage Box lengths currently stretch in the native parent rather than implementing CSS percentage measurement.
