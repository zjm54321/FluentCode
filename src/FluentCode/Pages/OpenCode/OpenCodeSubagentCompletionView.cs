using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>Child completion is a peer timeline row. Click opens the same child chat as an invocation;
/// result metadata/body remain hover-only. The original non-completion content retains its identity.</summary>
internal sealed class OpenCodeSubagentCompletionView : IDisposable
{
    private readonly Grid _root = new();
    private readonly FrameworkElement _content;
    private readonly OpenCodeTimelineChipView _chip = new("OpenCodeSubagentCompletion", OpenCodeStepRunPresentation.SubagentGlyph);
    private Action<string>? _onChildSessionRequested;
    private string? _childSessionId;
    private bool _disposed;

    public OpenCodeSubagentCompletionView(FrameworkElement content, Action<string>? onChildSessionRequested = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _onChildSessionRequested = onChildSessionRequested;
        _root.Children.Add(content);
    }

    public FrameworkElement Element => _root;
    internal FrameworkElement ProcessContent => _chip.Element;
    internal OpenCodeTimelineRowContent ProcessPresentation => _chip.Content;
    internal void OpenDetail() => _chip.OpenDetail();
    internal void HoverEnter() => _chip.HoverEnter();
    internal void HoverExit() => _chip.HoverExit();
    internal void SetDetailAnchor(FrameworkElement? anchor) => _chip.SetDetailAnchor(anchor);
    internal void SetProcessGroup(FrameworkElement group)
    {
        if (_root.Children.Count == 1 && ReferenceEquals(_root.Children[0], group)) return;
        _root.Children.Clear();
        _root.Children.Add(group);
    }

    public void Update(OpenCodeSubagentCompletionInfo? info, string? taskTitle = null, string? body = null)
    {
        if (_disposed) return;
        if (info is null || !OpenCodeSubagentPresentation.IsOpaqueSessionId(info.ChildSessionId))
        {
            _childSessionId = null;
            if (_root.Children.Count == 1 && ReferenceEquals(_root.Children[0], _content)) return;
            _root.Children.Clear();
            _root.Children.Add(_content);
            return;
        }
        _childSessionId = info.ChildSessionId;
        var completion = new OpenCodeAsyncCompletion(OpenCodeAsyncCompletionKind.Subagent,
            info.ChildSessionId, info.State ?? string.Empty, taskTitle ?? string.Empty, null, body ?? string.Empty,
            info.ChildSessionId, null);
        _chip.UpdateRow(OpenCodeStepRunPresentation.DescribeAsyncCompletion(completion), () =>
        {
            if (!_disposed && _childSessionId is { } child) _onChildSessionRequested?.Invoke(child);
        });
        // A conversation-owned process group may already own the chip.
        if (_chip.Element.Parent is not null && !ReferenceEquals(_chip.Element.Parent, _root)) return;
        if (_root.Children.Count == 1 && ReferenceEquals(_root.Children[0], _chip.Element)) return;
        _root.Children.Clear();
        _root.Children.Add(_chip.Element);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _childSessionId = null;
        _onChildSessionRequested = null;
        _chip.Dispose();
    }
}
