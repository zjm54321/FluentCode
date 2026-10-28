// Only the editor surface/dispatcher are inert. QueueFullScreenComposer and
// ApplyFullScreenText are extracted unchanged from the production page by run.ps1.
using System;
using System.Collections.Generic;
using AIFrontend.Pages.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly OpenCodeComposerController _composerController;
    private readonly OpenCodeComposerBinding _binding;
    private bool _pageActive = true;
    private bool _owns = true;
    private readonly QueueProbe DispatcherQueue = new();
    private OpenCodeComposerBinding? _fullScreenBinding;
    private OpenCodeComposerToken _fullScreenToken;
    private readonly EditorProbe _fullScreenEditor = new();
    private bool _fullScreenApplying = false;
    private bool _fullScreenComposing = false;
    private bool FullScreenComposerActive { get; set; }
    private bool IsChildComposerSession => false;
    internal int Opens { get; private set; }
    internal bool OpenedWhileActionOwned { get; private set; }
    private OpenCodeComposerClientActionTicket? _entry;

    internal OpenCodePage(OpenCodeComposerController controller, OpenCodeComposerBinding binding)
    { _composerController = controller; _binding = binding; }
    private bool OwnsComposerBinding(OpenCodeComposerBinding binding) => _owns && binding == _binding;
    private void OpenFullScreenComposer()
    {
        Opens++;
        OpenedWhileActionOwned = _entry is not null && _composerController.OwnsClientAction(_entry);
        _fullScreenBinding = _binding;
        _fullScreenToken = _composerController.Snapshot.Token;
        _fullScreenEditor.Text = _composerController.Snapshot.Text;
        FullScreenComposerActive = true;
    }
    internal void Request(OpenCodeComposerClientActionTicket ticket) { _entry = ticket; QueueFullScreenComposer(ticket); }
    internal void Drain() => DispatcherQueue.Drain();
    internal void Deactivate() => _pageActive = false;
    internal void LoseBinding() => _owns = false;
    internal bool Edit(string text) { _fullScreenEditor.Text = text; return ApplyFullScreenText(); }
    private sealed class EditorProbe { internal string Text { get; set; } = string.Empty; }
    private sealed class QueueProbe
    {
        private readonly Queue<Action> _queue = new();
        internal bool TryEnqueue(Action callback) { _queue.Enqueue(callback); return true; }
        internal void Drain() { while (_queue.TryDequeue(out var callback)) callback(); }
    }
}
