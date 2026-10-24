using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private OpenCodeBrowserSelection? _browserSelection;
    private void InitializeBrowserSelection(OpenCodeBrowserOverlay overlay)
    {
        _browserSelection = new(overlay, (page, node, generation, frame) =>
        {
            var owner = _browserWorkspaces.FirstOrDefault(pair => pair.Value.Pages.Contains(page)).Key;
            if (owner is null || !_browserBackends.TryGetValue(owner, out var backend)) throw new InvalidOperationException("页面所属会话已关闭。");
            return backend.RegisterElementRef(page.Id, node, generation, frame);
        }, AttachBrowserSelectionAsync);
    }
    private void DisposeBrowserSelection() { _browserSelection?.Dispose(); _browserSelection = null; }
    private Task<bool> AttachBrowserSelectionAsync(OpenCodeBrowserPage page, IReadOnlyList<BrowserSelectionAttachment> captures)
    {
        // Never resolve a delayed capture against whichever session happens to be selected next.
        var binding = _composerBinding;
        if (!_pageActive || !SourceReady || binding is null || _composerController is null || _composerView is null
            || _selectedSessionId != binding.SessionId || !OwnsComposerBinding(binding)
            || !_browserWorkspaces.TryGetValue(binding.SessionId, out var workspace) || !workspace.Pages.Contains(page)
            || GetComposerSnapshot().IsBusy || !_composerView.PrepareSubmission()) return Task.FromResult(false);
        var snapshot = _composerController.Snapshot;
        if (!snapshot.IsActive) return Task.FromResult(false);
        var added = _composerController.TryAddCapturedImages(snapshot.Token, captures.Select(c =>
            new OpenCodeComposerAttachmentPresentation(c.Name, c.Image, c.Details, c.Context)).ToArray());
        if (added) { workspace.HideOverlay(); Composer.Focus(FocusState.Programmatic); }
        return Task.FromResult(added);
    }
}
