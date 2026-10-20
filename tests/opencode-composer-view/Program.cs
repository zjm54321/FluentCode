// Offline policy harness for the composer view: compiles ONLY the WinUI-free
// src/AIFrontend/Pages/OpenCode/OpenCodeComposerInputPolicy.cs. No GUI, service, model or file reads.
using System;
using System.Collections.Generic;
using AIFrontend.Pages.OpenCode;
using K = AIFrontend.Pages.OpenCode.OpenCodeComposerKey;
using A = AIFrontend.Pages.OpenCode.OpenCodeComposerKeyAction;

var failures = 0;
var passed = 0;
void Check(bool ok, string name)
{
    if (ok) passed++;
    else { failures++; Console.WriteLine("FAIL " + name); }
}
var far = TimeSpan.FromSeconds(5);
OpenCodeComposerKeyState S(K key, bool shift = false, bool ctrl = false, bool composing = false, TimeSpan? since = null,
    bool latched = false, bool menu = false, bool rows = false, bool busy = false)
    => new(key, shift, ctrl, composing, since ?? far, latched, menu, rows, busy);
OpenCodeComposerKeyDecision D(OpenCodeComposerKeyState s) => OpenCodeComposerInputPolicy.DecideKey(s);

// ---- Enter: legacy matrix preserved ----
Check(D(S(K.Enter)) == new OpenCodeComposerKeyDecision(A.Submit, true, true), "plain Enter submits and latches");
Check(D(S(K.Enter, shift: true)).Action == A.PassThrough && !D(S(K.Enter, shift: true)).Handled, "Shift+Enter newline, not handled");
Check(D(S(K.Enter, composing: true)).Action == A.PassThrough, "IME composing Enter passes");
Check(D(S(K.Enter, since: TimeSpan.FromMilliseconds(79))).Action == A.PassThrough, "Enter within 80ms of composition end passes");
Check(D(S(K.Enter, since: TimeSpan.FromMilliseconds(80))).Action == A.Submit, "Enter at 80ms submits");
Check(D(S(K.Enter, latched: true)) == new OpenCodeComposerKeyDecision(A.Swallow, true, false), "held Enter swallowed");
Check(D(S(K.Enter, busy: true)) == new OpenCodeComposerKeyDecision(A.Swallow, true, false), "busy Enter swallowed, latch untouched");
foreach (var shift in new[] { false, true })
foreach (var composing in new[] { false, true })
foreach (var latched in new[] { false, true })
foreach (var menu in new[] { false, true })
foreach (var rows in new[] { false, true })
foreach (var busy in new[] { false, true })
{
    var d = D(S(K.Enter, shift, false, composing, null, latched, menu, rows, busy));
    Check(d.Action is A.PassThrough or A.Swallow or A.Submit or A.MenuAccept, "Enter never yields Stop/other action");
    if (d.Action == A.Submit) Check(!busy && !latched && !menu && !shift && !composing, "Submit only when eligible");
    if (d.Action == A.MenuAccept) Check(menu && rows && !latched && !busy, "MenuAccept only on fresh Enter over rows");
    if (menu && !shift && !composing) Check(d.Action != A.Submit && d.Handled && d.LatchEnter, "visible menu consumes Enter and latches");
}

// ---- Menu acceptance requires a fresh keypress for sending ----
var accept = D(S(K.Enter, menu: true, rows: true));
Check(accept == new OpenCodeComposerKeyDecision(A.MenuAccept, true, true), "menu Enter accepts and latches");
Check(D(S(K.Enter, latched: true)).Action == A.Swallow, "auto-repeat after acceptance does not send");
Check(D(S(K.Enter, latched: false)).Action == A.Submit, "fresh Enter after KeyUp sends");
Check(D(S(K.Enter, menu: true, rows: false)).Action == A.Swallow, "Loading/empty menu swallows Enter");
Check(D(S(K.Enter, menu: true, rows: true, composing: true)).Action == A.PassThrough, "IME confirmation cannot accept a row");

// ---- Navigation / dismissal / explicit opening ----
Check(D(S(K.Down, menu: true, rows: true)).Action == A.MenuNext, "Down moves");
Check(D(S(K.Up, menu: true, rows: true)).Action == A.MenuPrevious, "Up moves");
Check(D(S(K.Down)).Action == A.PassThrough, "Down without menu is caret movement");
Check(D(S(K.Down, menu: true, rows: true, composing: true)).Action == A.PassThrough, "Down during IME is candidate navigation");
Check(D(S(K.Escape, menu: true)).Action == A.MenuDismiss, "Esc dismisses");
Check(D(S(K.Escape)).Action == A.PassThrough, "Esc without menu passes");
Check(D(S(K.Escape, menu: true, composing: true)).Action == A.PassThrough, "Esc during IME cancels composition only");
Check(D(S(K.Slash)).Action == A.PassThrough, "typing / never opens catalog");
Check(D(S(K.Slash, ctrl: true)) == new OpenCodeComposerKeyDecision(A.OpenCatalog, true, false), "Ctrl+/ opens catalog");
Check(D(S(K.Slash, ctrl: true, composing: true)).Action == A.PassThrough, "Ctrl+/ during IME ignored");
Check(D(S(K.Slash, ctrl: true, busy: true)).Action == A.Swallow, "Ctrl+/ while busy swallowed");
Check(D(S(K.Slash, ctrl: true, shift: true)).Action == A.PassThrough, "Ctrl+Shift+/ not ours");
Check(D(S(K.Other, menu: true, rows: true)).Action == A.PassThrough, "other keys pass");

// ---- Tab completion (wave 3, additive): ours only over a visible menu with rows; never Submit/latch ----
Check(D(S(K.Tab, menu: true, rows: true)) == new OpenCodeComposerKeyDecision(A.MenuComplete, true, false), "Tab over rows completes, no latch");
Check(D(S(K.Tab)).Action == A.PassThrough && !D(S(K.Tab)).Handled, "Tab without menu is focus navigation");
Check(D(S(K.Tab, menu: true, rows: false)).Action == A.PassThrough, "Tab over an empty menu is focus navigation");
Check(D(S(K.Tab, menu: true, rows: true, composing: true)).Action == A.PassThrough, "Tab during IME passes");
Check(D(S(K.Tab, menu: true, rows: true, since: TimeSpan.FromMilliseconds(79))).Action == A.PassThrough, "Tab within IME guard passes");
Check(D(S(K.Tab, menu: true, rows: true, shift: true)).Action == A.PassThrough, "Shift+Tab is reverse focus navigation");
Check(D(S(K.Tab, menu: true, rows: true, ctrl: true)).Action == A.PassThrough, "Ctrl+Tab not ours");
Check(D(S(K.Tab, menu: true, rows: true, busy: true)) == new OpenCodeComposerKeyDecision(A.Swallow, true, false), "busy Tab swallowed");
foreach (var shift in new[] { false, true })
foreach (var composing in new[] { false, true })
foreach (var latched in new[] { false, true })
foreach (var menu in new[] { false, true })
foreach (var rows in new[] { false, true })
foreach (var busy in new[] { false, true })
{
    var d = D(S(K.Tab, shift, false, composing, null, latched, menu, rows, busy));
    Check(d.Action is A.PassThrough or A.Swallow or A.MenuComplete && !d.LatchEnter, "Tab never submits, accepts or latches Enter");
}
Check(OpenCodeComposerInputPolicy.CompletionPrefix(new[] { "redo", "review", "release", "editor" }, "r") == "re", "LCP of prefix matches only");
Check(OpenCodeComposerInputPolicy.CompletionPrefix(new[] { "Review", "revert" }, "r") == "Rev", "case-insensitive LCP, first candidate casing");
Check(OpenCodeComposerInputPolicy.CompletionPrefix(new[] { "review" }, "rev") == "review", "single match completes fully");
Check(OpenCodeComposerInputPolicy.CompletionPrefix(new[] { "redo", "review" }, "re") is null, "diverging right after the filter: no extension");
Check(OpenCodeComposerInputPolicy.CompletionPrefix(new[] { "editor" }, "r") is null, "substring-only matches never complete");
Check(OpenCodeComposerInputPolicy.CompletionPrefix(Array.Empty<string>(), "") is null, "no candidates");

// ---- Slash trigger: only the displayed trigger is consumed ----
var t = OpenCodeComposerInputPolicy.ParseTrigger("/rev fix the bug");
Check(t.Present && t.Filter == "rev" && t.RemainingArguments == "fix the bug", "trigger + args");
t = OpenCodeComposerInputPolicy.ParseTrigger("  /rev\nline2\n  keep");
Check(t.Present && t.Filter == "rev" && t.RemainingArguments == "line2\n  keep", "multi-line args retained verbatim");
t = OpenCodeComposerInputPolicy.ParseTrigger("/");
Check(t.Present && t.Filter == "" && t.RemainingArguments == "", "bare slash");
t = OpenCodeComposerInputPolicy.ParseTrigger("hello /rev");
Check(!t.Present && t.RemainingArguments == "hello /rev", "non-leading slash is not a trigger");
t = OpenCodeComposerInputPolicy.ParseTrigger("/usr/bin/env x");
Check(!t.Present && t.RemainingArguments == "/usr/bin/env x", "paths are not triggers");
t = OpenCodeComposerInputPolicy.ParseTrigger("/" + new string('a', 65));
Check(!t.Present, "overlong trigger rejected");
t = OpenCodeComposerInputPolicy.ParseTrigger(null);
Check(!t.Present && t.RemainingArguments == "", "null text");

// ---- Local filtering ----
var names = new List<string> { "init", "review", "preview", "Revert", "test" };
var f = OpenCodeComposerInputPolicy.Filter(names, r => r, "rev");
Check(string.Join(",", f) == "review,Revert,preview", "prefix first, case-insensitive, then substring");
Check(ReferenceEquals(OpenCodeComposerInputPolicy.Filter(names, r => r, ""), names), "empty filter returns all");
Check(OpenCodeComposerInputPolicy.Filter(names, r => r, "zzz").Count == 0, "no matches");
Check(OpenCodeComposerInputPolicy.MoveHighlight(-1, 3, +1) == 0 && OpenCodeComposerInputPolicy.MoveHighlight(-1, 3, -1) == 2, "highlight entry");
Check(OpenCodeComposerInputPolicy.MoveHighlight(2, 3, +1) == 0 && OpenCodeComposerInputPolicy.MoveHighlight(0, 3, -1) == 2, "highlight wraps");
Check(OpenCodeComposerInputPolicy.MoveHighlight(0, 0, +1) == -1, "no rows no highlight");

// ---- HasDraft / route / Send-Stop matrix ----
Check(!OpenCodeComposerInputPolicy.HasDraft("  \n", false, 0), "whitespace is no draft");
Check(OpenCodeComposerInputPolicy.HasDraft("", true, 0), "argument-free command is a draft");
Check(OpenCodeComposerInputPolicy.HasDraft("", false, 1), "attachment-only is a draft");
Check(OpenCodeComposerInputPolicy.Route(false, 0, "/rev x") == OpenCodeComposerSubmitRoute.LegacyText, "unselected /text stays literal prompt");
Check(OpenCodeComposerInputPolicy.Route(false, 0, " ") == OpenCodeComposerSubmitRoute.None, "nothing to send");
Check(OpenCodeComposerInputPolicy.Route(true, 0, "") == OpenCodeComposerSubmitRoute.Enhanced, "command routes enhanced");
Check(OpenCodeComposerInputPolicy.Route(false, 2, "") == OpenCodeComposerSubmitRoute.Enhanced, "files route enhanced");
foreach (var legacy in new[] { false, true })
foreach (var busy in new[] { false, true })
foreach (var draft in new[] { false, true })
foreach (var working in new[] { false, true })
{
    var b = OpenCodeComposerInputPolicy.Buttons(true, draft, working, legacy, busy, draft ? OpenCodeComposerSubmitRoute.LegacyText : OpenCodeComposerSubmitRoute.None, true);
    if (legacy || busy) Check(!b.CanSend && !b.CanStop && b.ShowProgress, "busy disables Send AND Stop");
    if (draft) Check(!b.CanStop && !b.StopMode, "draft never shows Stop");
}
var enhancedBlocked = OpenCodeComposerInputPolicy.Buttons(true, true, false, false, false, OpenCodeComposerSubmitRoute.Enhanced, false);
Check(!enhancedBlocked.CanSend, "enhanced send requires controller eligibility");
var stop = OpenCodeComposerInputPolicy.Buttons(true, false, true, false, false, OpenCodeComposerSubmitRoute.None, false);
Check(stop.CanStop && stop.StopMode && !stop.CanSend, "idle draftless working session can Stop");
Check(!OpenCodeComposerInputPolicy.Buttons(false, true, true, false, false, OpenCodeComposerSubmitRoute.LegacyText, true).CanSend, "no session no send");

// ---- Bounded summaries / MIME ----
Check(OpenCodeComposerInputPolicy.FormatBytes(0) == "0 B" && OpenCodeComposerInputPolicy.FormatBytes(1536) == "1.5 KB"
    && OpenCodeComposerInputPolicy.FormatBytes(10 * 1024 * 1024) == "10 MB", "byte formatting");
Check(OpenCodeComposerInputPolicy.Bound("a\r\nb\nc", 10) == "a b c", "newlines collapsed");
var bounded = OpenCodeComposerInputPolicy.Bound(new string('x', 500), 64);
Check(bounded.Length == 64 && bounded.EndsWith('…'), "bounded with ellipsis");
Check(OpenCodeComposerInputPolicy.AttachmentSummary(0, 0, 10, 20 << 20) == "", "no summary without files");
Check(OpenCodeComposerInputPolicy.AttachmentSummary(2, 1536, 10, 20 << 20) == "2/10 个附件 · 1.5 KB / 20 MB", "summary");
foreach (var (ext, mime) in new[] { (".png", "image/png"), (".JPG", "image/jpeg"), (".jpeg", "image/jpeg"), (".gif", "image/gif"),
    (".webp", "image/webp"), (".txt", "text/plain"), (".md", "text/markdown"), (".pdf", "application/pdf"), (".json", "application/json") })
    Check(OpenCodeComposerInputPolicy.MimeForName("a" + ext) == mime, "mime " + ext);
Check(OpenCodeComposerInputPolicy.MimeForName("a.exe") is null && OpenCodeComposerInputPolicy.MimeForName("noext") is null, "unknown mime");
Check(string.Join(",", OpenCodeComposerInputPolicy.PickerExtensions) == ".png,.jpg,.jpeg,.gif,.webp,.txt,.md,.pdf,.json", "picker allow-list");

Console.WriteLine($"composer-view policy: {passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;
