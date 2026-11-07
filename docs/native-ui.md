# Native OpenUI Lang slice

The `native_ui` tool in the **opencode-qol native-ui lane** accepts `{source, title?}`. Server validation uses official MIT `@openuidev/lang-core` **1.1.0**, with a Zod catalog preserving OpenIntelligentUI's names and positional prop order. Success is a short acknowledgement; the program remains in tool `state.input` for history replay.

## Contract

`src/FluentCode/Services/NativeUi/catalog.json` is the embedded byte-identical copy of QoL's generated manifest (`kernel/plugins/native-ui/scripts/generate-catalog.ts`). It supplies component/prop order, JSON Schema types, reactive flags and resource limits. The server's focused Bun test checks equality between the two lanes.

The C# port implements the complete lexer/Pratt expression grammar, assignments, references/forward references, root/fallback entry, schema-aware lowering, partial-input auto-close, comment/fence handling and the streaming completed-statement watermark. The shared corpus compares complete parse JSON, evaluated defaults and streaming snapshots against lang-core 1.1.0. Duplicate definitions, unresolved/cyclic references and schema errors follow the official parser.

The local runtime has `$variables`, reactive `$value` assignments, ordered `Action([...])`, `@Set`/`@Reset`, arithmetic/comparison/boolean operators, ternaries, member/index access and the core built-ins (including Count, Sum, Round and Each). Each captures iterator values for deferred actions. Query/Mutation/defaults are parsed; no tools or network are invoked. Their cards/actions say **未支持**. @Run stops an unsupported action sequence. @OpenUrl is disabled in this slice.

Limits in both lanes: **65,536 source characters, 4,096 nodes, depth 64, 16,384 string characters**. Expansion and evaluation are bounded as well as lexing. URL data is restricted to http(s). Cards have no script/reflection/HTML/file/network capability. The reused Markdown presenter has an explicit sandbox policy: no inherited transcript image scope, link/attachment chips or Mermaid browser rendering.

## Native UI and composer

Col, Row, Card, Divider, Title, Text, Caption, Badge, Markdown, Button, Input, Slider, Select and Checkbox render as WinUI controls. Structural-path reconciliation retains input/caret and slider control identity during state updates. All theme/styling decisions are in `Pages/OpenCode/NativeUi/NativeUiTheme.cs`; ask **@designer** to replace this file for the final Fluent light/dark visual specification.

`native_ui` tool parts are answer cards outside generic collapsed tool groups. `tool.input.delta` retains a placeholder even when a JSON prefix already looks complete; the final `input.ended` marker permits rendering. Parse/validation failures are compact error cards. Tool progress/success updates retain local state; a history reload starts again from declared defaults (persisted card state is phase 2).

@ToAssistant is only a user intent. The page verifies the live transcript/source/session and clean composer, preserves existing drafts, reconciles the ordinary editor, then invokes **RequestComposerSubmission**. The normal shared interaction lease/send workflow remains the only transport route. An agent working receives **queue** delivery; unavailable queue, permission/protection, stale origin, shell mode or existing draft blocks the intent. Command-leading `!`/`/` messages cannot turn a card into code execution. Context/form-state data accompanies the ordinary prompt as labeled JSON, not a separate RPC call.

## Focused verification and demos

1. In FluentCode: `bun tests/native-ui/generate-expected.ts /home/ming/opencode-qol-lanes/native-ui` regenerates the official shared oracle.
2. In QoL: `bun test kernel/tests/qol-native-ui.test.ts kernel/tests/qol-tui-defaults.test.ts`.
3. In Windows PowerShell with .NET 10 on PATH: `tests/native-ui/run.ps1` (also registered in `tests/run-suite.py`; do not run the full suite for this slice).
4. `scripts/build-debug.ps1` builds the lane client. Its ignored prepared browser runtime is copied read-only from main; no prepare/download or backend change is needed.
5. `tests/native-ui/capture-demos.ps1 -AppPath <lane published FluentCode.exe> -EvidenceRoot <NEW artifacts/native-ui-slice-UTC>` runs only an isolated **offline showcase**, not the native/full-suite harness. It captures six real WinUI PNGs and `demos.json`, exercising memory/arithmetic, slider/numeric input/checkbox and selection/assistant intent. The showcase does not contact a live AI: its assistant intent is recorded as an offline queue draft.

Remaining phases: full catalog, OpenIntelligentUI's extra pure functions, query/mutation execution and permissions, external-link confirmation, persisted card state and the designer's final visual specification.
