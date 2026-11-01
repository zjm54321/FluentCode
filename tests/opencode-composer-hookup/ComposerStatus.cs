// RenderStatus is extracted verbatim by run.ps1. These fields are in-memory UI/sync seams,
// not a copied status policy or evidence of native WinUI rendering.
#pragma warning disable CS0649
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class ComposerStatusHost
{
    private sealed class ControlState
    {
        internal Visibility Visibility;
        internal bool IsActive;
        internal string Text = "", Glyph = "";
        internal object? Style;
    }
    private sealed class SyncState
    {
        internal OpenCodeComposerInputState State;
    }
    private readonly ControlState _statusBar = new(), _statusText = new(), _statusRing = new(),
        _statusIcon = new();
    private SyncState? _sync;
    private string? _slotUnavailableReason, _localNote;
    private bool _picking;
    private int _sideQuestionPending;
    private static object ThemedForeground(string control, string key) => (control, key);

    internal static void Run()
    {
        OpenCodeComposerSnapshot idle = new(default, "", null, OpenCodeComposerCatalogState.Closed,
            Array.Empty<OpenCodeComposerCommandRow>(), Array.Empty<OpenCodeComposerAttachmentRow>(),
            false, false, true, false, false, false, null);
        foreach (var enhanced in new[] { false, true })
        {
            var draft = new ComposerStatusHost();
            draft.RenderStatus(idle with { HasDraft = true, IsBoundDraft = true }, enhanced, false);
            Program.Check(draft._statusBar.Visibility == Visibility.Collapsed,
                "DD1 ordinary bound text/command/attachment draft has no synthetic discard-status row");
        }
        var sending = idle with { IsBusy = true, IsProtected = true, HasDraft = true, IsBoundDraft = true };
        foreach (var route in new[] { "text", "command", "attachment", "new-session" })
        {
            var host = new ComposerStatusHost { _sideQuestionPending = 1 };
            host.RenderStatus(sending with { SelectedCommand = route == "command" ? "review" : null }, route != "text", false);
            Program.Check(host._statusBar.Visibility == Visibility.Collapsed && !host._statusRing.IsActive,
                route + " sending has no status/protection/pending progress row or second spinner");
        }
        var picking = new ComposerStatusHost { _picking = true };
        picking.RenderStatus(sending, true, false);
        Program.Check(picking._statusBar.Visibility == Visibility.Visible && picking._statusRing.IsActive,
            "attachment picker retains its distinct pre-send progress");
        picking._picking = false;
        picking.RenderStatus(sending, true, false);
        Program.Check(picking._statusBar.Visibility == Visibility.Collapsed && !picking._statusRing.IsActive,
            "picker-to-send transition collapses the existing row and stops its ring");
        foreach (var note in new[] { "服务器未接纳提交；草稿已保留。", "提交结果未知；禁止自动重试。" })
        {
            var failed = new ComposerStatusHost();
            failed.RenderStatus(idle with { HasDraft = true, IsBoundDraft = true, Note = note }, true, false);
            Program.Check(failed._statusBar.Visibility == Visibility.Visible && failed._statusText.Text == note
                && !failed._statusRing.IsActive,
                "failure/unknown recovery note remains visible without draft-discard UI");
        }
        var recovery = new ComposerStatusHost { _slotUnavailableReason = "恢复草稿失败。" };
        recovery.RenderStatus(sending, true, false);
        Program.Check(recovery._statusBar.Visibility == Visibility.Visible && recovery._statusText.Text == recovery._slotUnavailableReason,
            "recovery explanation stays visible even while the page is busy");
        var held = new ComposerStatusHost { _sync = new() { State = OpenCodeComposerInputState.HeldStale } };
        held.RenderStatus(idle, false, false);
        Program.Check(held._statusBar.Visibility == Visibility.Visible && held._statusText.Text.Contains("复制")
            && !held._statusText.Text.Contains("丢弃"), "stale-input recovery retains copy guidance without advertising removed action");
        var local = new ComposerStatusHost { _localNote = "附件未添加；原附件保持不变。" };
        local.RenderStatus(sending, true, false);
        Program.Check(local._statusBar.Visibility == Visibility.Visible && local._statusText.Text == local._localNote,
            "real local error is not hidden by submission progress suppression");
        var protectedDraft = new ComposerStatusHost();
        protectedDraft.RenderStatus(idle with { IsProtected = true }, true, false);
        Program.Check(protectedDraft._statusBar.Visibility == Visibility.Visible && !protectedDraft._statusRing.IsActive,
            "actionable protection outside a send remains visible");
        var inactive = new ComposerStatusHost();
        inactive.RenderStatus(idle with { IsActive = false, IsBoundDraft = true }, true, true);
        Program.Check(inactive._statusBar.Visibility == Visibility.Visible && inactive._statusText.Text.Contains("返回原会话")
            && !inactive._statusText.Text.Contains("丢弃"), "inactive draft retains return-to-origin recovery without discard action");
        Console.WriteLine("PASS: 15 production status-renderer checks; in-memory UI seams, no native UI.");
    }
}

internal static class OpenCodeThemedXaml
{
    internal static object BrushStyle(string control, params (string Property, string Resource)[] resources) => resources;
}
