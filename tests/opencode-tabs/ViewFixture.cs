#pragma warning disable CS0169, CS0414
using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls;
namespace AIFrontend.Pages.OpenCode;
// Native view boundary wrapping REAL sync/evidence plus verbatim activation/recovery methods
// extracted by run.ps1. No Page slot-association algorithm lives here.
internal sealed partial class OpenCodeComposerView
{
    private readonly Func<OpenCodeComposerSnapshot> _source;
    private readonly TextBox _editor;
    private readonly OpenCodeComposerEditorSync _sync;
    private readonly OpenCodeComposerInputEvidenceSource _inputEvidence;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher=new();
    private string? _slotUnavailableReason;
    private bool _editorReadOnlyApplied;
    private readonly Dictionary<Guid,Microsoft.UI.Xaml.FrameworkElement> _attachmentChips=new();
    private readonly OpenCodeComposerViewLifetime _lifetime=new(new Immediate(),()=>{});
    private sealed class Immediate:IOpenCodeComposerUiScheduler{public bool TryEnqueue(Action action){action();return true;}}
    internal event EventHandler<OpenCodeComposerAttachmentRemovedEventArgs>? AttachmentRemoved;
    private readonly Func<bool> _composition;
    private bool _composing=>_composition();
    internal OpenCodeComposerView(Func<OpenCodeComposerSnapshot> source,TextBox editor,Func<bool> composing,
        Func<OpenCodeComposerToken,string,bool,OpenCodeComposerEditorAck?> apply)
    {
        _source=source;_editor=editor;_composition=composing;
        _sync=new(source,()=>editor.Text,text=>editor.Text=text,composing,apply);
        _inputEvidence=new(()=>_sync,()=>_lifetime.IsLive,()=>_lifetime.Generation,composing);
        _lifetime.OnLoaded();_lifetime.Invalidated+=_inputEvidence.LifetimeChanged;
        _inputEvidence.LifetimeChanged();
    }
    internal bool PrepareClientTextApplication(){var clean=_sync.Prepare();_inputEvidence.Refresh();return clean;}
    internal bool PrepareSubmission()=>PrepareClientTextApplication();
    internal OpenCodeComposerInputEvidence InputEvidence=>_inputEvidence.Current;
    internal void Observe(){_inputEvidence.RecordTextChanging();_sync.Flush();_inputEvidence.Refresh();}
    internal void RecordOnly()=>_inputEvidence.RecordTextChanging(); // Native TextChanging before the queued flush; real sync pending basis.
    internal void NativeUnloaded()=>_lifetime.OnUnloaded();
    internal void NativeLoaded()=>_lifetime.OnLoaded();
    // Fixture-only core reset reconciliation, not a user-facing discard action.
    internal void Discard(){_sync.Reconcile();_inputEvidence.Refresh();}
    internal void BeginDraftSlotTransition(){}internal void EndDraftSlotTransition(){}
    internal void RequestRender(){}internal void RevokeSlotGestures(){_lifetime.OnEditorAttached();}
    internal Action Removal()
    {
        var s=_source();var row=s.Attachments.Single();var chip=AttachmentChip(row,s.Token);_attachmentChips[row.Id]=chip;
        return()=>((Button)chip.Tag).Press();
    }
    private bool FlushForIntent(){var clean=_sync.Flush()==OpenCodeComposerInputState.Clean;_inputEvidence.Refresh();return clean;}
    private static Button ChipRemoveButton(string id)=>new(); // Styling only; the real callback/body is source-linked.
    private static object? ThemedForeground(string type,string key)=>null;
    private static object? ThemedBorder(string fill,string stroke)=>null;
    private static TextBlock Caption(string text,double tone)=>new(){Text=text};
    private void BeginCatalogLifetime(){}
    private void RotatePlaceholderSplash(){} // Placeholder-only splash rotation (B15); covered by tests/opencode-composer.
}
