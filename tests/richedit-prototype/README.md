# RichEditBox capability prototype — phase 1

> Historical phase-1 report below. The production migration now uses validated edit receipts instead of live-range identity. Run `build-run.ps1 -LogRoot <directory> -Adapter` for tests linked to the real production adapter; add `-Manual` for interactive checks. See `docs/composer.md`. This harness is still standalone and is not registered in run-suite.

**Recommendation: GO for phase 2 (document model behind the existing TextBox); NO-GO for phase 4 (RichEditBox integration) yet.**

The native live-range identity assumption fails at a chip's leading boundary. Do not infer identity from labels, especially with duplicate labels. Resolve and validate explicit transaction/anchor rebinding before phase 4. The prototype stops short of implementing a production adapter.

## Scope and reproducibility

- Standalone unpackaged WinUI 3 scratch app. No product references, no `src/FluentCode` changes, no suite registration, installation, commit or push. Only the prototype window was launched.
- Uses the repository's `global.json` (.NET SDK 10.0.401), Windows App SDK **1.8.260921001**, SDK BuildTools **10.0.26100.9169**, and a package lock. Restore uses only the existing global NuGet cache with audit disabled. Own `obj/` and `bin/`.
- Observed runtime: Windows **10.0.26200**, .NET **10.0.14**, actual `RasterizationScale=1`. Minimum target remains **10.0.19044**; this host does not validate Windows 10 behavior.
- Automated mode briefly foregrounds the scratch window, positions it at (100,100), and makes it topmost to prevent mouse occlusion. SendInput requires prototype foreground ownership; mouse injection additionally verifies that the target belongs to this process. It modifies the clipboard with synthetic test data.

From WSL at the repository root:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File tests/richedit-prototype/build-run.ps1 \
  -LogRoot "$(wslpath -w /tmp/opencode)" \
  > /tmp/opencode/rep1-build-run.log 2>&1
tail -n 160 /tmp/opencode/rep1-runtime.log
```

Add `-Manual` to leave the prototype open, or `-BuildOnly` to build without launching. Do not run the solution or the product build scripts for this harness. The build script's 90-second timeout applies only to its own automatically launched prototype process.

## Per-item results (2026-11-07)

Evidence: `/tmp/opencode/rep1-runtime.log` (latest automated run, about 120 lines), `/tmp/opencode/rep1-uia.log`, `/tmp/opencode/rep1-msbuild.log`, and `/tmp/opencode/rep1-build-run.log`. The latest automated run has exactly **2 failing assertions**: leading-boundary mapping and native-undo identity. Exit **1** reflects these capability failures; the project build succeeds.

| Item | Result | Evidence |
| --- | --- | --- |
| 1a. Chip insertion / rendered bitmap densities | PASS | `bitmap-100%/150%/200%` render icon + label to 132×32, 198×48, 264×64 pixels; all `InsertImage-*` calls use 132×32 DIP and read back one U+FFFC with alt text `same label`. Actual monitor 150/200% remains manual. |
| 1b. Stable range → TokenId identity | **FAIL** | Duplicate/adjacent objects, insertion away from the boundary, trailing-boundary insertion and cross-chip replacement pass. Exact leading-boundary insertion changes the first object's range from `[1,2]` to `[1,6]`, containing `left` + U+FFFC, instead of `[5,6]`. Native undo leaves the restored object's range `[1,1]` and its neighbor `[1,3]` over two objects. |
| 2. One-character caret / selection / deletion | PASS | Native Right: `1→2`; Left: `2→1`; Shift+Right and Shift+Left: `[1,2]`; Backspace/Delete yield `AB\r`. Home=`0`, End=`3`. Mouse clicks just outside the image's left/right edges yield caret `1`/`2`. No navigation or caret-snapping handler is installed. No Tab, @ or ! hook was added. |
| 3. Text readback / position mapping | PASS | `UseObjectText-alt`, `trailing-EOP`, `newline-normalization`, `surrogate-UTF16` pass. Logs enumerate each native position, including final CR, user trailing space and the surrogate pair; details below. |
| 4. Microsoft Pinyin composition ordering | **FAIL / manual pending** | Unicode SendInput commits `你好` successfully but is not composition. Physical `nihao` + Enter twice produced literal `nihao\rnihao\r` and **0** composition starts/ends in the selected IME's English mode (`HKL=08040804`). Real TSF composition ordering cannot be accepted from this run. All composition event hooks and low-priority drain logging are present. |
| 5. Plain-only paste / raw-value copy | PASS | Real Ctrl+V triggers `Paste`, sets `Handled=true` before any await and inserts only Unicode text despite HTML/RTF poison formats. Pasted `@agent [Image 1]` creates zero tokens. Real Ctrl+C produces `Ahttps://example.test/rawB`, not alt text. CRLF/surrogate paste and surrogate copy also pass. |
| 6. Native undo off / model undo | PASS | `UndoLimit=0`, `CanUndo=False`. PreviewKeyDown intercepts Ctrl+Z before native handling. Model snapshot restore preserves token GUID and selection `[1,2]`; direct model undo also preserves two adjacent duplicate-label GUIDs and selection `[1,3]`. A model-owned context menu is supplied. Native undo's identity failure above is deliberately retained as a failing probe. |
| 7. Accessibility / Narrator prerequisite | PASS for UIA; speech manual | Out-of-process UIA TextPattern returns **`Asame labelB`**. Embedded child count **1**, type **Image**, name **`same label`**. Narrator's actual spoken output still needs listening verification. |
| 8. Inline-image height | PASS | After asynchronous native layout, `DesiredSize.Height`: plain **41**, 96-DIP image **95**, vertical scrolling disabled **116**, `ActualHeight=116`; object rect **132×103**. Use a completed layout pass; immediate measurement after InsertImage was stale in the initial probe. |

### Position contract observed on this SDK

- Native `GetText(None)` returns U+FFFC for each image and includes the final undeletable `\r` EOP. An image occupies exactly **one native UTF-16 position**.
- `GetText(UseObjectText)` expands each object to its readable alt text and **omits the final EOP in the observed output**. It is not a caret-coordinate string or the authoritative send serializer.
- For input `a\r\nb\nc\r😀z ` with a chip after `a`, the native story is `a<OBJ>\rb\rc\r😀z \r`, `StoryLength=12`, `.Length=12`. CRLF and LF normalize to one CR. User trailing spaces and user-entered final newlines must survive; remove only the one native EOP, never `Trim()`.
- The emoji occupies native offsets **7 and 8**. Retrieving either single-unit range `[7,8]` or `[8,9]` returns the **whole surrogate pair**, so concatenating per-character `GetText` results duplicates it. Retrieve whole text-run segments instead. The prototype's copy/model snapshot code does so.
- Clipboard insertion normalizes CRLF/LF before calculating the resulting native caret offset. Input `a\r\n😀\r\n` becomes `a\r😀\r` plus native EOP, with caret **5**.
- TextChanging runs synchronously with the document already updated; after SetText returns only TextChanging has occurred. TextChanged follows asynchronously. Composition-relative ordering remains unverified until the manual IME check.

## Manual acceptance checklist

Launch using `-Manual`; this does not launch FluentCode. Use **Reset manual fixture** before each case. It chooses the closest cached 100/150/200% bitmap density to the current actual RasterizationScale. Its two labels are identical but raw URLs and GUIDs differ.

1. **IME (required):** select Chinese Microsoft Pinyin and explicitly choose Chinese input mode. Type `nihao`, choose a candidate with Enter, then immediately perform a second composition. Repeat adjacent to and replacing a selected chip. Record actual Started/Changed/Ended and TextChanging/TextChanged order, each new composition version, and the low-priority drain after Ended. Verify no document writes or caret snapping while composing. Use **Log manual checkpoint** after completion, not during candidate selection.
2. **Candidate Enter:** observe that candidate acceptance commits text, not an editor newline. The harness has no send command; product send-key/composition integration remains a phase-4 acceptance case, rather than something this scratch app can prove.
3. **Narrator:** enable Narrator, focus the editor, read through both image chips and select each one. Confirm `same label` is spoken intelligibly and navigation does not trap focus. UIA already exposes the alt text and Image child.
4. **Actual DPI:** set Windows display scaling to 100%, 150%, 200% (or move between monitors configured that way), reset the fixture, and verify logged RasterizationScale, 132×32 DIP visual size, icon/text sharpness, baseline alignment and clicks beside chips. This prototype does not change system display settings automatically.
5. **Windows 10 19044:** run this isolated harness on the minimum supported OS to establish downlevel behavior. Current results cover only the Windows 11 host.

The manual undo button restores its fixture checkpoint; it is a capability demonstration, not a complete edit/undo/redo adapter. The custom context menu and keyboard undo share that model restore path. Manual clipboard tests overwrite clipboard content.

## Phase decision

- **Phase 2 GO:** the immutable document model is needed regardless of native editor choice and can ship behind the existing TextBox. Keep its ownership, revision/ack and composition-drain contracts unchanged.
- **Phase 4 NO-GO now:** an unnormalized native `ITextRange` is not a reliable one-object identity anchor. Specify an explicit, fail-closed transaction/anchor map and validate duplicate labels, exact-boundary edits, spanning replacement and model undo against it before integrating RichEditBox. Never guess IDs from labels. Actual IME, Narrator speech and real monitor DPI acceptance also remain open.
