using System;
using System.Linq;
using Microsoft.UI.Dispatching;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AIFrontend.Pages.OpenCode;

// UI-thread snapshots only. The Page must bind these to its captured source/session/view owner;
// message DTOs do not contain a session ID. Rectangles are in the supplied ScrollViewer's coordinates.
internal sealed record OpenCodeVisibleIdle(string MessageId, double Watermark, Rect LabelBounds, Rect EffectiveViewport, Rect ScrollBounds);
internal sealed record OpenCodeIdleViewportSnapshot(long RenderVersion, OpenCodeVisibleIdle? Candidate);

/// <summary>
/// OpenCode 会话消息时间线的纯 C# 展示控件（<see cref="StackPanel"/> 子类）。
/// </summary>
/// <remarks>
/// <para>
/// 渲染范围（严格按已核实消息变体，不虚构内容）：
/// user 文本气泡；assistant 的 text / reasoning / tool 部件（工具用 <see cref="OpenCodeToolPartView"/>，
/// 思考用 <see cref="OpenCodeReasoningRowView"/>）；两段文本之间的全部思考 / 工具调用折叠为一个“处理过程”分组
/// （<see cref="OpenCodeToolGroupView"/>，“已处理 X 秒”；进行中展开、结束自动收起、历史默认收起；同一回合可跨多条 assistant 消息）；
/// idle 的回合级终态标注；synthetic / system / skill / shell / compaction / 切换标记的说明行；
/// 指令通知（“指令”行，详情列出指令文件）、异步完成通知（“终端完成”“子任务完成”“后台任务完成”）与插件内部触发消息
/// 只显示一行工具样式的紧凑行（<see cref="OpenCodeTimelineChipView"/>，全文在悬停详情中）；原始后台调用行随完成通知显示最终状态。
/// question 工具是“处理过程”分组内的普通工具行（“提问”，详情为问题与所选回答）；回答在输入区的提问遮罩中进行。
/// 用户消息气泡默认最多两行，更长时可用 chevron 展开（<see cref="OpenCodeUserBubbleView"/>）。
/// 无法识别的类型只显示类型名，不猜测含义。
/// </para>
/// <para>
/// 富文本：user 文本与 assistant 的 text 部件经 <see cref="OpenCodeRichTextPresenter"/>
/// 呈现真正的 Markdown 结构（标题/粗斜体/列表/引用/代码块/表格等）与安全 HTML 片段，
/// 使用原生 <c>RichTextBlock</c> 等控件（不派生控件、不创建 WebView）；工具部件仍为原生
/// <see cref="OpenCodeToolPartView"/>。部件/消息的元素身份、稳定 key 与增量 update 回调保持不变。
/// </para>
/// <para>
/// Full-history logical entries drive cross-message process chains. Only viewport ± buffer rows
/// own XAML bodies; measured-height gaps replace the rest. Message keys retain expansion state,
/// while an item-key/intra-row anchor owns prepend and streaming-follow corrections.
/// </para>
/// <para>
/// subagent 子任务（见 <see cref="OpenCodeSubagentPresentation"/>）：工具部件
/// （<c>name == "subagent"</c>）悬停详情显示调用预览，点击经 <see cref="SubagentSessionRequested"/>
/// 向上转发导航提示；带 <c>metadata.source == "subagent"</c> 完成 metadata 的
/// assistant / synthetic / system 消息显示「子任务结果」标记。事件只含不透明 ID，不授权、不发起请求；
/// 部件被移除、替换或清空时退订并 Dispose，不产生 stale callbacks。
/// </para>
/// </remarks>
public sealed class OpenCodeConversationView : StackPanel
{
    private readonly OpenCodeManualBackgroundIndex _manualBackground = new();
    internal event EventHandler? BindingCleared;
    internal event EventHandler? IdleViewportInvalidated;
    internal long RenderVersion { get; private set; }
    private long _idleLayoutVersion = -1;
    private bool _idleContentChanging;
    private bool _idleSnapshotComplete = true;
    private readonly Dictionary<string, int> _messageIdCounts = new(StringComparer.Ordinal);
    private readonly OpenCodeConversationWindow _window = new();
    private readonly List<Border> _windowGaps = new();
    private readonly Border _emptyHistoryMarker = new() { Height = 0 };
    private readonly Dictionary<string, bool> _rowExpansion = new(StringComparer.Ordinal);
    private ScrollViewer? _viewport;
    private OpenCodeConversationWindow.Anchor? _readAnchor;
    private bool _followingBottom;
    private bool _animateNextScroll;
    private bool _animatingScroll;
    private bool _windowQueued;
    private bool _updatingWindow;
    private double? _requestedOffset;
    private double _observedViewportOffset = double.NaN;
    private long _windowGeneration;
    private Func<bool>? _scrollOwnerCurrent;
    internal int RealizedRowCount => _entries.Count(entry => entry.Realized);
    internal bool IsRestoringViewport => _updatingWindow || _idleContentChanging || _requestedOffset is not null || _animatingScroll;
    // Publish classification from the single scroll owner, independent of native event
    // subscription order. Pagination observes corrections but never treats them as input.
    internal event Action<bool>? HistoryViewportChanged;
    internal void RefreshViewport(ScrollViewer viewport) { ConnectViewport(viewport); UpdateWindow(); }

    // Full retained history, not only realized rows. Estimated offsets converge as rows are measured.
    internal IReadOnlyList<(string Text, bool User, double Offset)> ScrollAnnotations()
    {
        var labels = new List<(string, bool, double)>();
        double offset = Padding.Top;
        foreach (var entry in _entries)
        {
            var message = entry.Snapshot;
            var user = message?.Type == OpenCodeMessageTypes.User;
            var text = user ? message?.Text : message?.Type == OpenCodeMessageTypes.Assistant
                ? string.Join("\n", message.Content?.Where(p => p.Type == OpenCodePartTypes.Text).Select(p => p.Text) ?? Array.Empty<string>()) : null;
            if (!string.IsNullOrWhiteSpace(text) && !entry.IsInstructionNotice && !entry.IsInitiatorChip && !entry.IsAsyncCompletion && !entry.IsManualBackgroundNotice)
                labels.Add((text, user, offset));
            offset += _window.Height(entry.Key);
        }
        return labels;
    }

    internal static string AnnotationLabel(string text)
    {
        text = text.TrimStart();
        var elements = StringInfo.GetTextElementEnumerator(text);
        var label = string.Empty;
        while (elements.MoveNext() && new StringInfo(label).LengthInTextElements < 2)
        {
            var next = elements.GetTextElement();
            if (string.IsNullOrWhiteSpace(next)) break;
            label += next;
        }
        return label;
    }

    private double WindowOffset()
        => _viewport is null ? 0 : Math.Max(0, -TransformToVisual(_viewport).TransformPoint(new Point(0, 0)).Y - Padding.Top);

    private void ConnectViewport()
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scroll) { ConnectViewport(scroll); break; }
    }

    private void ConnectViewport(ScrollViewer viewport)
    {
        if (ReferenceEquals(_viewport, viewport)) return;
        DisconnectViewport();
        _viewport = viewport;
        _observedViewportOffset = viewport.VerticalOffset;
        viewport.ViewChanged += WindowViewChanged;
        viewport.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(WindowPointerInput), handledEventsToo: true);
        viewport.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(WindowPointerInput), handledEventsToo: true);
        viewport.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(WindowKeyInput), handledEventsToo: true);
        QueueWindowUpdate();
    }

    private void DisconnectViewport()
    {
        if (_viewport is not null)
        {
            _viewport.ViewChanged -= WindowViewChanged;
            _viewport.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(WindowPointerInput));
            _viewport.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(WindowPointerInput));
            _viewport.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(WindowKeyInput));
        }
        _viewport = null;
        _windowGeneration++;
        _windowQueued = false;
    }

    private void WindowPointerInput(object sender, PointerRoutedEventArgs e) => CaptureInteractionAnchor();
    private void WindowKeyInput(object sender, KeyRoutedEventArgs e) => CaptureInteractionAnchor();
    private void CaptureInteractionAnchor()
    {
        if (_viewport is null || !IsLoaded) return;
        var stopAnimation = _animatingScroll;
        _animatingScroll = _animateNextScroll = false;
        _followingBottom = false;
        _scrollOwnerCurrent = null;
        _requestedOffset = null;
        _windowGeneration++;
        _windowQueued = false;
        _readAnchor = _window.Capture(WindowOffset());
        if (stopAnimation)
        {
            _requestedOffset = _viewport.VerticalOffset;
            _viewport.ChangeView(null, _requestedOffset, null, disableAnimation: true);
        }
    }

    private void WindowViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_viewport is not { } viewport) return;
        var moved = double.IsNaN(_observedViewportOffset) || Math.Abs(viewport.VerticalOffset - _observedViewportOffset) > 0.5;
        _observedViewportOffset = viewport.VerticalOffset;
        var restoring = IsRestoringViewport || !moved;
        if (_animatingScroll)
        {
            if (!e.IsIntermediate) { _animatingScroll = false; _requestedOffset = null; }
            HistoryViewportChanged?.Invoke(true);
            QueueWindowUpdate();
            return;
        }
        if (_requestedOffset is { } requested)
        {
            // ChangeView is asynchronous. Extent/intermediate notifications before its ACK
            // are not user scrolling and must not replace the retained key or realization range.
            if (Math.Abs(viewport.VerticalOffset - requested) <= 0.5) _requestedOffset = null;
        }
        else if (moved && !_updatingWindow && !_idleContentChanging)
        {
            _requestedOffset = null;
            _scrollOwnerCurrent = null; // A new user viewport supersedes any queued page scroll intent.
            _windowGeneration++;
            _windowQueued = false;
            _followingBottom = viewport.ScrollableHeight - viewport.VerticalOffset <= 0.5;
            _readAnchor = _window.Capture(WindowOffset());
        }
        HistoryViewportChanged?.Invoke(restoring);
        QueueWindowUpdate();
    }

    internal void PreserveReadingAnchor(ScrollViewer viewport, Func<bool> ownerCurrent)
    {
        ConnectViewport(viewport);
        CaptureInteractionAnchor();
        _scrollOwnerCurrent = ownerCurrent;
        _followingBottom = false;
        _animateNextScroll = false;
        _readAnchor = _window.Capture(WindowOffset());
    }

    // The page supplies its page/session fences; ClearMessages also invalidates queued callbacks.
    internal void FollowToBottom(ScrollViewer viewport, Func<bool> ownerCurrent, bool animated = false)
    {
        ConnectViewport(viewport);
        if (_animatingScroll && !animated) CaptureInteractionAnchor();
        _scrollOwnerCurrent = ownerCurrent;
        _followingBottom = true;
        _animateNextScroll = animated;
        QueueWindowUpdate();
    }

    private void QueueWindowUpdate()
    {
        if (_windowQueued || _updatingWindow || _viewport is null || !IsLoaded) return;
        var generation = _windowGeneration;
        _windowQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _windowGeneration) return;
            _windowQueued = false;
            UpdateWindow();
        });
    }

    private OpenCodeConversationWindow.Range WindowRange()
    {
        var height = _viewport?.ViewportHeight ?? 800;
        if (height <= 0) height = 800;
        var offset = _animatingScroll ? WindowOffset() : _followingBottom ? Math.Max(0, _window.Extent - height)
            : _window.Restore(_readAnchor, _viewport is null ? 0 : WindowOffset());
        return _window.Select(offset, height, Math.Max(height, 800));
    }

    private void UpdateWindow()
    {
        if (_updatingWindow || _viewport is not { } viewport || !IsLoaded) return;
        if (_scrollOwnerCurrent is { } current && !current()) return;
        _updatingWindow = true;
        var changed = false;
        try
        {
            viewport.UpdateLayout();
            if (_readAnchor is null) _readAnchor = _window.Capture(WindowOffset());
            changed = MeasureRealizedRows();
            var range = WindowRange();
            var replacements = new List<(int Index, bool Realize)>();
            for (var i = 0; i < _entries.Count; i++)
            {
                var old = _entries[i];
                var realize = i >= range.Start && i < range.End && (_window.Height(old.Key) > 0 || old.Realized) || KeepVisual(old);
                if (old.Realized == realize || old.Snapshot is null) continue;
                changed = true;
                replacements.Add((i, realize));
            }
            if (changed)
            {
                ChangeIdleContent(() =>
                {
                    foreach (var (index, realize) in replacements)
                    {
                        var old = _entries[index];
                        old.Dispose?.Invoke();
                        if (old.Realized) Children.Remove(old.Element);
                        var replacement = realize ? CreateEntry(old.Key, old.Snapshot!) : CreateUnrealizedEntry(old.Key, old.Snapshot!);
                        CopyEntrySnapshot(old, replacement);
                        _entries[index] = replacement;
                        _byKey[old.Key] = replacement;
                    }
                    ApplyStepChains();
                    AlignWindow();
                    RefreshRealizedMessageIds();
                    RaiseQuestionAnchorsChangedIfNeeded();
                });
            }
            viewport.UpdateLayout();
            // Realization, deferred Markdown and tool expansion can change heights in this
            // very layout pass. Accept them BEFORE restoring the anchor, not next frame.
            if (MeasureRealizedRows())
            {
                changed = true;
                ChangeIdleContent(AlignWindow);
                viewport.UpdateLayout();
            }
            var offset = _followingBottom ? viewport.ScrollableHeight
                : viewport.VerticalOffset + _window.Restore(_readAnchor, WindowOffset()) - WindowOffset();
            if (!_animatingScroll && Math.Abs(viewport.VerticalOffset - offset) > 0.5)
            {
                _requestedOffset = Math.Clamp(offset, 0, viewport.ScrollableHeight);
                var animated = _followingBottom && _animateNextScroll;
                _animateNextScroll = false;
                _animatingScroll = animated;
                viewport.ChangeView(null, _requestedOffset, null, disableAnimation: !animated);
            }
        }
        finally { _updatingWindow = false; }
        if (changed) QueueWindowUpdate();
    }

    private bool MeasureRealizedRows()
    {
        var changed = false;
        foreach (var entry in _entries)
            if (entry.Realized) changed |= _window.Measure(entry.Key,
                entry.Element.Visibility == Visibility.Collapsed ? 0 : entry.Element.ActualHeight + entry.Element.Margin.Top + entry.Element.Margin.Bottom);
        return changed;
    }

    private bool KeepVisual(Entry entry)
    {
        if (!entry.Realized) return false;
        var focused = XamlRoot is null ? null : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (DescendsFrom(focused, entry.Element) || DescendsFrom(OpenCodeToolDetailPopup.RetainedAnchor, entry.Element)) return true;
        return HasSelection(entry.Element);
    }

    private static bool DescendsFrom(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node)) if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private static bool HasSelection(DependencyObject node)
    {
        if (node is TextBlock text && !string.IsNullOrEmpty(text.SelectedText)
            || node is RichTextBlock rich && !string.IsNullOrEmpty(rich.SelectedText)) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasSelection(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }

    private void RefreshRealizedMessageIds()
    {
        _renderedMessageIds.Clear();
        foreach (var entry in _entries)
            if (entry.Realized && entry.Snapshot?.Id is { Length: > 0 } id) _renderedMessageIds.Add(id);
    }

    private static void CopyEntrySnapshot(Entry from, Entry to)
    {
        to.Snapshot = from.Snapshot; to.MessageType = from.MessageType; to.Fingerprint = from.Fingerprint;
        to.TaskTitle = from.TaskTitle; to.AsyncSignature = from.AsyncSignature; to.RecoverySignature = from.RecoverySignature;
        to.IdleMessageId = from.IdleMessageId; to.IdleWatermark = from.IdleWatermark;
    }

    private void AlignWindow()
    {
        var elements = new List<UIElement>();
        var gapIndex = 0;
        double gapHeight = 0;
        void FlushGap()
        {
            if (gapHeight <= 0) return;
            if (gapIndex == _windowGaps.Count) _windowGaps.Add(new Border());
            var gap = _windowGaps[gapIndex++];
            gap.Height = gapHeight;
            elements.Add(gap);
            gapHeight = 0;
        }
        foreach (var entry in _entries)
            if (entry.Realized) { FlushGap(); elements.Add(entry.Element); }
            else gapHeight += _window.Height(entry.Key);
        FlushGap();
        if (elements.Count == 0 && _entries.Count > 0) elements.Add(_emptyHistoryMarker);
        if (_retryDeadline is not null && _retryStatus is not null) elements.Add(_retryStatus);
        foreach (var id in _steerOrder)
            if (!_deliveredSteerIds.Contains(id)) elements.Add(_steerRows[id].Element);
        AlignChildren(this, elements);
    }

    private void InvalidateIdleViewport()
    {
        RenderVersion++;
        _idleLayoutVersion = -1;
        IdleViewportInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void ChangeIdleContent(Action change)
    {
        _idleContentChanging = true;
        _idleSnapshotComplete = false;
        try
        {
            InvalidateIdleViewport();
            change();
            _idleSnapshotComplete = true;
        }
        finally
        {
            _idleContentChanging = false;
            IdleViewportInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Conservative actual-label proof, not message-list membership. No layout/visibility fallback.
    /// Call on the UI thread after layout, then recheck the immutable snapshot at the Page's one-shot frame.
    /// No candidate is returned during mutation or before this version's LayoutUpdated.</summary>
    internal OpenCodeIdleViewportSnapshot CaptureIdleViewport(ScrollViewer viewport)
    {
        OpenCodeVisibleIdle? best = null;
        if (!_idleContentChanging && _idleSnapshotComplete && _idleLayoutVersion == RenderVersion)
            foreach (var entry in _entries)
            {
                if (entry.MessageType != OpenCodeMessageTypes.Idle || entry.IdleLabel is not { } label
                    || string.IsNullOrWhiteSpace(entry.IdleMessageId) || _messageIdCounts.GetValueOrDefault(entry.IdleMessageId) != 1
                    || entry.IdleWatermark is not { } water || !double.IsFinite(water) || water < 0
                    || string.IsNullOrWhiteSpace(label.Text) || !ReferenceEquals(entry.Element.Parent, this)
                    || !ReferenceEquals(VisualTreeHelper.GetParent(label), entry.Element)) continue;
                if (!TryIdleBounds(label, viewport, out var bounds, out var clip)) continue;
                if (best is null || water > best.Watermark || (water == best.Watermark && string.CompareOrdinal(entry.IdleMessageId, best.MessageId) < 0))
                    best = new(entry.IdleMessageId, water, bounds, clip, new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
            }
        return new(RenderVersion, best);
    }

    private bool TryIdleBounds(TextBlock label, ScrollViewer viewport, out Rect bounds, out Rect clip)
    {
        bounds = clip = default;
        try
        {
            if (viewport.XamlRoot is not { } root || !Positive(viewport.ActualWidth) || !Positive(viewport.ActualHeight)
                || !TryBounds(label, viewport, new Rect(0, 0, label.ActualWidth, label.ActualHeight), out bounds)) return false;
            clip = new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight);
            var hasView = false;
            var hasScroll = false;
            var hasPresenter = false;
            DependencyObject? current = label;
            var depth = 0;
            for (; current is not null && depth++ < 512; current = VisualTreeHelper.GetParent(current))
            {
                if (current is UIElement ui && (ui.Visibility != Visibility.Visible || !Positive(ui.Opacity))) return false;
                if (current is not FrameworkElement node) return false;
                if (!node.IsLoaded || !ReferenceEquals(node.XamlRoot, root)) return false;
                // Also stay inside ancestor layout bounds. This is deliberately conservative about
                // implicit layout clips (which WinUI does not expose as an IsArrangeValid contract).
                if (!ReferenceEquals(node, label))
                {
                    if (!TryBounds(node, viewport, new Rect(0, 0, node.ActualWidth, node.ActualHeight), out var layoutBounds)) return false;
                    clip = Intersect(clip, layoutBounds);
                }
                hasView |= ReferenceEquals(node, this);
                hasScroll |= ReferenceEquals(node, viewport);
                // Native ScrollContentPresenter is the template's actual clipping viewport, excluding chrome.
                if (node is ScrollContentPresenter)
                {
                    if (!hasScroll) hasPresenter = true;
                }
                if (node is Border border)
                {
                    var corners = border.CornerRadius;
                    var inset = Math.Max(Math.Max(corners.TopLeft, corners.TopRight), Math.Max(corners.BottomLeft, corners.BottomRight));
                    if (inset > 0)
                    {
                        // A rounded ancestor can implicitly clip even when UIElement.Clip is null.
                        // Its central inset rectangle is conservative, not the rounded bounding box.
                        if (!TryBounds(node, viewport, new Rect(inset, inset, node.ActualWidth - 2 * inset, node.ActualHeight - 2 * inset), out var roundSafe)) return false;
                        clip = Intersect(clip, roundSafe);
                    }
                }
                if (node.Clip is { } rectangle)
                {
                    // WinUI Clip is RectangleGeometry (not WPF's arbitrary Geometry).
                    // An additional clip transform is not proven by this axis-aligned query.
                    if (rectangle.Transform is not null
                        || !TryBounds(node, viewport, rectangle.Rect, out var explicitClip)) return false;
                    clip = Intersect(clip, explicitClip);
                }
            }
            if (current is not null || !hasView || !hasScroll || !hasPresenter) return false;
            return Positive(clip.Width) && Positive(clip.Height) && bounds.X >= clip.X && bounds.Y >= clip.Y
                && bounds.Right <= clip.Right && bounds.Bottom <= clip.Bottom;
        }
        catch (Exception) { return false; } // Detached tree/invalid transforms are absence of proof.
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static Rect Intersect(Rect a, Rect b)
    {
        var x = Math.Max(a.X, b.X); var y = Math.Max(a.Y, b.Y);
        return new Rect(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y));
    }
    private static bool TryBounds(FrameworkElement node, UIElement target, Rect local, out Rect bounds)
    {
        bounds = default;
        if (!double.IsFinite(local.X) || !double.IsFinite(local.Y) || !Positive(local.Width) || !Positive(local.Height)) return false;
        var transform = node.TransformToVisual(target);
        var a = transform.TransformPoint(new Point(local.X, local.Y));
        var b = transform.TransformPoint(new Point(local.Right, local.Y));
        var c = transform.TransformPoint(new Point(local.X, local.Bottom));
        var d = transform.TransformPoint(new Point(local.Right, local.Bottom));
        // Reject rotation/skew/reflection: bounding boxes alone overstate visible area in those cases.
        if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X) || !double.IsFinite(b.Y)
            || !double.IsFinite(c.X) || !double.IsFinite(c.Y) || !double.IsFinite(d.X) || !double.IsFinite(d.Y)
            || Math.Abs(a.Y - b.Y) > 0.000001 || Math.Abs(a.X - c.X) > 0.000001
            || Math.Abs(b.X - d.X) > 0.000001 || Math.Abs(c.Y - d.Y) > 0.000001 || b.X <= a.X || c.Y <= a.Y) return false;
        bounds = new Rect(a.X, a.Y, b.X - a.X, c.Y - a.Y);
        return Positive(bounds.Width) && Positive(bounds.Height);
    }
    private sealed class SteerRow
    {
        internal required OpenCodeInboxItem Item;
        internal readonly Grid Element = new() { Margin = new Thickness(0, 6, 0, 6) };
        internal readonly StackPanel Content = new() { Spacing = 4 };
        internal readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -32, 0, 0), Opacity = 0 };
        internal readonly OpenCodeRichTextPresenter Presenter = new();
        internal readonly List<Button> Buttons = new();
        internal string Signature = string.Empty;
        internal JsonElement? RenderedPayload;
    }
    private readonly Dictionary<string, SteerRow> _steerRows = new(StringComparer.Ordinal);
    private readonly List<string> _steerOrder = new();
    private readonly HashSet<string> _renderedUserMessageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deliveredSteerIds = new(StringComparer.Ordinal);
    private string? _steerSession;
    private bool _steerBusy;
    private Func<OpenCodeInboxItem, bool>? _canWithdrawSteer;
    private Func<OpenCodeInboxItem, bool>? _canChangeSteerDelivery;
    internal event EventHandler<string>? PendingSteerWithdrawRequested;
    internal event EventHandler<OpenCodePendingDeliveryRequest>? PendingSteerDeliveryChangeRequested;

    /// <summary>View-only projection of a fenced, accepted inbox snapshot. Never creates/adopts a history DTO or sends a prompt.
    /// Only exact user/steer entries of the supplied selected session appear; durable IDs deduplicate against actual user history.</summary>
    public void ApplyAdmittedSteer(string? sessionId, IReadOnlyList<OpenCodeInboxItem>? items,
        Func<OpenCodeInboxItem, bool>? canWithdraw = null, Func<OpenCodeInboxItem, bool>? canChangeDelivery = null)
    {
        var previousOrder = _steerOrder.ToArray();
        if (_steerSession != sessionId) _deliveredSteerIds.Clear();
        _steerSession = sessionId;
        _canWithdrawSteer = canWithdraw;
        _canChangeSteerDelivery = canChangeDelivery;
        var retained = new HashSet<string>(StringComparer.Ordinal);
        _steerOrder.Clear();
        if (sessionId is not null && items is not null)
            foreach (var item in items)
            {
                if (item.SessionId != sessionId || !OpenCodeQueuedPromptsView.IsSteerUser(item) || item.Id is not { Length: > 0 } id || !retained.Add(id)) continue;
                _steerOrder.Add(id);
                if (!_steerRows.TryGetValue(id, out var row))
                {
                    row = new SteerRow { Item = item };
                    row.Content.Children.Add(row.Presenter.Element);
                    var bubble = new OpenCodeUserBubbleView(row.Content, delivered: false);
                    row.Element.Children.Add(bubble.Element);
                    row.Element.Children.Add(row.Actions);
                    var hoverRow = row;
                    row.Element.PointerEntered += (_, _) => hoverRow.Actions.Opacity = 1;
                    row.Element.PointerExited += (_, _) => hoverRow.Actions.Opacity = 0;
                    row.Element.GotFocus += (_, _) => hoverRow.Actions.Opacity = 1;
                    row.Element.LostFocus += (_, _) => hoverRow.Actions.Opacity = 0;
                    AutomationProperties.SetAutomationId(row.Element, "OpenCodeAdmittedSteer." + id);
                    _steerRows[id] = row;
                }
                row.Item = item;
                UpdateSteerRow(row);
            }
        var stale = new List<string>();
        foreach (var pair in _steerRows) if (!retained.Contains(pair.Key)) { Children.Remove(pair.Value.Element); stale.Add(pair.Key); }
        foreach (var id in stale) _steerRows.Remove(id);
        ReconcileSteerRows();
        if (stale.Count > 0 || !previousOrder.SequenceEqual(_steerOrder)) InvalidateIdleViewport();
    }

    public void SetPendingSteerBusy(bool busy)
    {
        _steerBusy = busy;
        foreach (var row in _steerRows.Values) foreach (var button in row.Buttons) button.IsEnabled = !busy;
    }

    private bool CurrentSteer(SteerRow row) => !_steerBusy && row.Item.SessionId == _steerSession
        && row.Item.Id is { } id && _steerRows.TryGetValue(id, out var current) && ReferenceEquals(current, row)
        && !_renderedUserMessageIds.Contains(id) && !_deliveredSteerIds.Contains(id) && ReferenceEquals(row.Element.Parent, this);

    private void UpdateSteerRow(SteerRow row)
    {
        var item = row.Item;
        var withdraw = _canWithdrawSteer?.Invoke(item) == true;
        var change = _canChangeSteerDelivery?.Invoke(item) == true;
        var signature = withdraw + "|" + change;
        var samePayload = row.RenderedPayload is { } previous && item.Payload is { } current
            ? JsonElement.DeepEquals(previous, current) : row.RenderedPayload is null && item.Payload is null;
        if (signature == row.Signature && samePayload) return;
        InvalidateIdleViewport();
        row.Signature = signature;
        row.RenderedPayload = item.Payload;
        var rawText = OpenCodeQueuedPromptsView.ReadText(item.Payload);
        row.Presenter.Render(rawText, OpenCodeRichTextStyle.Body);
        while (row.Content.Children.Count > 1) row.Content.Children.RemoveAt(1);
        if (item.Payload is { ValueKind: JsonValueKind.Object } payload)
            foreach (var attachmentKind in new[] { "files", "agents", "skills" })
                if (payload.TryGetProperty(attachmentKind, out var attachments) && attachments.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var attachment in attachments.EnumerateArray())
                    {
                        index++;
                        var name = attachment.ValueKind == JsonValueKind.Object && attachment.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        var label = attachmentKind == "files" ? "附件" : attachmentKind == "agents" ? "智能体附件" : "技能附件";
                        row.Content.Children.Add(OpenCodeInteractionUi.StyledText(label + "：" + (string.IsNullOrWhiteSpace(name) ? index.ToString(CultureInfo.InvariantCulture) : name), OpenCodeInteractionUi.CaptionStyleKey, selectable: true));
                    }
                }
        if (string.IsNullOrEmpty(rawText) && row.Content.Children.Count == 1)
            row.Content.Children.Add(OpenCodeInteractionUi.StyledText("（空消息）", OpenCodeInteractionUi.CaptionStyleKey));
        row.Actions.Children.Clear(); row.Buttons.Clear();
        if (change)
        {
            var button = new Button { Content = "改为排队", IsEnabled = !_steerBusy };
            button.Click += (_, _) => { if (CurrentSteer(row) && _canChangeSteerDelivery?.Invoke(row.Item) == true)
                PendingSteerDeliveryChangeRequested?.Invoke(this, new(row.Item.Id!, OpenCodeDeliveryModes.Queue)); };
            AutomationProperties.SetAutomationId(button, "OpenCodeAdmittedSteerDelivery." + item.Id);
            row.Actions.Children.Add(button); row.Buttons.Add(button);
        }
        if (withdraw)
        {
            var button = new Button { Content = "撤回", IsEnabled = !_steerBusy };
            button.Click += (_, _) => { if (CurrentSteer(row) && _canWithdrawSteer?.Invoke(row.Item) == true) PendingSteerWithdrawRequested?.Invoke(this, row.Item.Id!); };
            AutomationProperties.SetAutomationId(button, "OpenCodeAdmittedSteerWithdraw." + item.Id);
            row.Actions.Children.Add(button); row.Buttons.Add(button);
        }
        row.Actions.Visibility = withdraw || change ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReconcileSteerRows()
    {
        var projected = new HashSet<UIElement>();
        foreach (var row in _steerRows.Values) projected.Add(row.Element);
        foreach (var id in _steerOrder)
        {
            if (_renderedUserMessageIds.Contains(id)) _deliveredSteerIds.Add(id);
            if (_deliveredSteerIds.Contains(id)) Children.Remove(_steerRows[id].Element);
        }
        var index = 0;
        foreach (var element in Children) if (!projected.Contains(element)) index++;
        foreach (var id in _steerOrder)
        {
            var element = _steerRows[id].Element;
            if (_deliveredSteerIds.Contains(id)) continue;
            if (index >= Children.Count || !ReferenceEquals(Children[index], element))
            {
                Children.Remove(element);
                Children.Insert(Math.Min(index, Children.Count), element);
            }
            index++;
        }
    }

    // Not a Button/detail source: errors offer text selection/copy and a native tooltip only.
    private sealed class AssistantErrorRow
    {
        private readonly FontIcon _icon = new() { Glyph = "\uEB90", FontSize = 16, Width = 16, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _text = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey, selectable: true);
        internal Grid Element { get; } = new() { ColumnSpacing = 8, Height = 32, Margin = new Thickness(13, 0, 8, 0) };

        internal AssistantErrorRow()
        {
            Element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            Element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _text.TextWrapping = TextWrapping.NoWrap;
            _text.TextTrimming = TextTrimming.CharacterEllipsis;
            _text.MaxLines = 1;
            _text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_text, 1);
            Element.Children.Add(_icon); Element.Children.Add(_text);
            AutomationProperties.SetAutomationId(Element, "OpenCodeAssistantError");
        }

        internal void Apply(string rawText)
        {
            _text.Text = rawText;
            _text.Foreground = OpenCodeInteractionUi.TryBrush(OpenCodeInteractionUi.CriticalBrushKey);
            _icon.Foreground = _text.Foreground;
            AutomationProperties.SetName(Element, rawText);
            ToolTipService.SetToolTip(_text, rawText);
        }
    }

    private sealed class Entry
    {
        internal TextBlock? IdleLabel { get; init; }
        internal string? IdleMessageId { get; set; }
        internal double? IdleWatermark { get; set; }
        public required string Key { get; init; }
        public string? MessageType { get; set; }
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>该消息在本次快照中对应的子任务标题（用于完成卡片；无匹配为 null）。</summary>
        public string? TaskTitle { get; set; }
        public FrameworkElement Element { get; init; } = null!;
        internal bool Realized => Element is not null;
        internal OpenCodeMessage? Snapshot { get; set; }
        public required Action<OpenCodeMessage> Update { get; init; }

        /// <summary>该条目是否为指令通知的安全呈现；呈现方式改变时据其整体替换旧条目。</summary>
        public bool IsInstructionNotice { get; init; }

        /// <summary>该条目是否为内部触发消息的紧凑提示行；呈现方式改变时据其整体替换旧条目。</summary>
        public bool IsInitiatorChip { get; init; }

        /// <summary>该条目是否为异步完成通知行；呈现方式改变时据其整体替换旧条目。</summary>
        public bool IsAsyncCompletion { get; init; }
        public bool IsManualBackgroundNotice { get; init; }

        /// <summary>assistant 条目中工具调用所关联异步完成通知的签名（变化时就地更新以刷新原始调用行）。</summary>
        public string AsyncSignature { get; set; } = string.Empty;
        public string RecoverySignature { get; set; } = string.Empty;

        /// <summary>assistant 条目的处理过程分组状态（跨消息链计算用）；其他条目为 null。</summary>
        public AssistantState? Assistant { get; init; }

        /// <summary>移除该条目（或清空）时调用：退订事件并停用子部件回调；无资源时为 null。</summary>
        public Action? Dispose { get; init; }
    }

    private sealed class PartEntry
    {
        public required string Key { get; init; }
        public required string Kind { get; init; }
        public string Fingerprint { get; set; } = string.Empty;
        public FrameworkElement Element { get; set; } = null!;
        public Action? EnsureVisual { get; set; }
        public Action? RefreshVisual { get; set; }
        public string ReasoningText { get; set; } = string.Empty;

        /// <summary>就地更新；第二参数为当前 assistant 消息 id（仅工具部件的导航事件使用，可为 null），第三参数为关联的异步完成通知。</summary>
        public required Action<OpenCodeContentPart, string?, OpenCodeAsyncCompletion?> Update { get; init; }

        /// <summary>移除该部件时调用：退订事件并 Dispose 控制器；无资源时为 null。</summary>
        public Action? Dispose { get; init; }

        /// <summary>最近一次应用的部件快照（工具分组摘要据此计算）。</summary>
        public OpenCodeContentPart? LastPart { get; set; }

        /// <summary>最近一次应用的关联异步完成通知（工具行摘要 / 分组计数据此计算）；无则 null。</summary>
        public OpenCodeAsyncCompletion? LastCompletion { get; set; }
        public bool ManualBackground { get; set; }

        /// <summary>是否为可参与分组的工具行（含 question 工具：普通工具行 + 隐藏的提问锚点）。</summary>
        public bool IsGroupableTool { get; init; }

        /// <summary>元素当前所在容器（消息部件面板或某个工具组的列表）；未入树为 null。</summary>
        public Panel? Host { get; set; }

        /// <summary>reasoning 部件的“思考”行（连续思考合并时只有段首的行入树）；其他部件为 null。</summary>
        public OpenCodeReasoningRowView? Reasoning { get; init; }
        public Action? OpenDetail { get; init; }
        public Action? HoverEnter { get; init; }
        public Action? HoverExit { get; init; }
        public Action<FrameworkElement?>? SetDetailAnchor { get; init; }
        public Func<OpenCodeToolRowModel>? Presentation { get; init; }
        public Func<string>? OperationKey { get; init; }
        public Func<string>? Glyph { get; init; }
        public Action<Action<string, OpenCodeContentPart>?>? ConfigureBackground { get; init; }
        public Func<Action?>? BackgroundAction { get; init; }
        public bool InlinePresentation { get; init; }

        /// <summary>是否属于“处理过程”分组（思考或普通工具调用）。</summary>
        public bool IsReasoning => Kind == OpenCodePartTypes.Reasoning;
        public OpenCodeToolRowStatus ReasoningStatus { get; set; }
        public bool IsStepPart => IsGroupableTool || IsReasoning;
    }

    /// <summary>一条 assistant 消息中一段连续的处理过程（思考 / 工具），对应一个分组视图。</summary>
    private sealed class StepRun
    {
        public required OpenCodeToolGroupView Group { get; init; }

        public required AssistantState Owner { get; init; }

        public List<OpenCodeToolRowModel> Tools { get; } = new();
        public List<PartEntry> Leaves { get; } = new();

        public int ReasoningRows { get; set; }

        public double? StartedAt { get; set; }

        public double? EndedAt { get; set; }

        /// <summary>本段之前没有可见的文本 / 提问等内容（可与上一条消息末尾的段落连成一条链）。</summary>
        public bool StartsMessage { get; set; }

        /// <summary>本段之后没有可见的文本 / 提问等内容（可与下一条消息开头的段落连成一条链）。</summary>
        public bool EndsMessage { get; set; }

        /// <summary>本段是跨消息链的后续段（头部隐藏，跟随链首展开状态）。</summary>
        public bool IsFollower { get; set; }
        public bool HasVisibleLeaves { get; set; } = true;
    }

    /// <summary>assistant 条目的分组状态（由 <see cref="LayoutParts"/> 产生，<see cref="ApplyStepChains"/> 消费）。</summary>
    private sealed class AssistantState
    {
        public OpenCodeMessage? Message { get; set; }

        public List<StepRun> Runs { get; } = new();

        /// <summary>除分组外还有可见内容（文本、提问、未识别部件、错误、重试、子任务完成卡片）。</summary>
        public bool HasBoundary { get; set; }

        public bool CompletionShown { get; set; }
        public bool IsProcessNotice { get; init; }

        public FrameworkElement? Element { get; set; }
    }

    /// <summary>用户在分组“已结束”时显式切换过的展开状态（按链首组 ID；跨刷新 / 分页保留，清空会话时重置）。</summary>
    private readonly Dictionary<string, bool> _groupExpansion = new(StringComparer.Ordinal);

    /// <summary>上一次计算时处于“进行中”的链首组 ID（用于识别 进入运行 / 运行结束 的转换）。</summary>
    private readonly HashSet<string> _activeGroups = new(StringComparer.Ordinal);

    private bool? _currentTurnActivity;
    private string? _observedTurnKey;
    private bool _observedTurnActive;
    private string? _terminalTurnKey;
    private Action<string, OpenCodeContentPart>? _backgroundAction;
    private DateTimeOffset? _retryDeadline;
    private int _retryAttempt;
    private string? _retryMessage;
    private TextBlock? _retryStatus;
    private DispatcherQueueTimer? _retryTimer;

    /// <summary>Publish only the live selected-session retry state, not an arbitrary historical message.
    /// V2 session.retry.scheduled.at is the absolute Unix-millisecond next-retry deadline. Null clears.</summary>
    public void SetCurrentRetry(OpenCodeMessageRetry? retry)
    {
        if (retry?.Attempt is not { } attempt || attempt <= 0 || !OpenCodeStepRunPresentation.IsPlausibleTimestamp(retry.At))
        {
            ClearCurrentRetry();
            return;
        }
        ShowCurrentRetry(attempt, DateTimeOffset.FromUnixTimeMilliseconds((long)retry.At!.Value), retry.Error?.Message);
    }

    private void ShowCurrentRetry(int attempt, DateTimeOffset nextRetryAt, string? message)
    {
        _retryAttempt = attempt;
        _retryDeadline = nextRetryAt;
        _retryMessage = message;
        _retryStatus ??= new TextBlock
        {
            Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionStyleKey),
            Foreground = OpenCodeInteractionUi.TryBrush("SystemFillColorCautionBrush"),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(37, 4, 8, 4),
        };
        AutomationProperties.SetAutomationId(_retryStatus, "OpenCodeCurrentRetry");
        UpdateRetryStatus();
        if (!Children.Contains(_retryStatus)) Children.Add(_retryStatus);
        StartRetryTimer();
    }

    public void ClearCurrentRetry()
    {
        _retryDeadline = null;
        _retryMessage = null;
        _retryTimer?.Stop();
        if (_retryStatus is not null) { _retryStatus.Opacity = 1; Children.Remove(_retryStatus); }
    }

    private void UpdateRetryStatus()
    {
        if (_retryStatus is null || _retryDeadline is not { } deadline) return;
        var now = DateTimeOffset.UtcNow;
        var text = OpenCodeStepRunPresentation.FormatRetryCountdown(_retryAttempt, deadline, now);
        if (_retryStatus.Text != text)
        {
            _retryStatus.Text = text;
            AutomationProperties.SetName(_retryStatus, text);
        }
        // A gentle two-second amber pulse; text/meaning remain visible throughout.
        _retryStatus.Opacity = 0.85 + 0.15 * Math.Cos(now.ToUnixTimeMilliseconds() * Math.PI / 1000);
        ToolTipService.SetToolTip(_retryStatus, _retryStatus.Text + (string.IsNullOrWhiteSpace(_retryMessage) ? string.Empty : "：" + _retryMessage));
    }

    private void StartRetryTimer()
    {
        if (_retryDeadline is null) return;
        if (_retryTimer is null && DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _retryTimer = queue.CreateTimer();
            _retryTimer.Interval = TimeSpan.FromMilliseconds(200);
            _retryTimer.IsRepeating = true;
            _retryTimer.Tick += (_, _) => UpdateRetryStatus();
        }
        _retryTimer?.Start();
    }

    /// <summary>Register only after transport support and selected-session mutation gates are verified.
    /// Callback receives the current parent message id and current tool snapshot; null hides all actions.
    /// No local state is changed on click: only later server snapshots update the row.</summary>
    public void SetBackgroundAction(Action<string, OpenCodeContentPart>? action)
    {
        _backgroundAction = action;
        foreach (var entry in _entries)
            if (entry.Assistant is { } owner)
                foreach (var run in owner.Runs)
                    foreach (var leaf in run.Leaves) leaf.ConfigureBackground?.Invoke(action);
        ApplyStepChains();
    }

    /// <summary>Selected-session authoritative turn activity, not an individual message/tool completion.
    /// Call after applying a snapshot; null restores message-based inference. ClearMessages resets the binding.</summary>
    public void SetCurrentTurnActivity(bool? active)
    {
        if (_currentTurnActivity != active && _entries.Any(entry => entry.Assistant is not null)) InvalidateIdleViewport();
        if (active == false) ClearCurrentRetry();
        _currentTurnActivity = active;
        if (active == false) _terminalTurnKey = _observedTurnKey;
        else if (active == true) _terminalTurnKey = null;
        ApplyStepChains();
        UpdateWindow();
    }

    /// <summary>当前已渲染的 question 工具锚点（key = 消息 id + “\n” + 部件 id）。</summary>
    private readonly Dictionary<string, OpenCodeQuestionAnchorHost> _questionAnchors = new(StringComparer.Ordinal);

    private bool _questionAnchorsChanged;

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前快照中「真实消息 id」的索引（仅非空 <see cref="OpenCodeMessage.Id"/>；匿名消息的
    /// 兜底 key 与重复 id 的序号兜底 key 一律不进入）。供 <see cref="ContainsRenderedMessage"/>
    /// 做逐条消息归属查询；集合本身不对外暴露、只读查询不改变渲染状态。
    /// </summary>
    private readonly HashSet<string> _renderedMessageIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 本次快照由 <see cref="OpenCodeSubagentTaskTitles.Build"/> 重建的「完成消息 id → 前序工具描述」
    /// 映射（仅作完成卡片标题；只读消费，<see cref="ClearMessages"/> 清空）。标题算法不在此实现。
    /// </summary>
    private IReadOnlyDictionary<string, string> _subagentTaskTitles =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 本次快照中的异步完成通知（key = 关联的不透明 id：shellID / childID / 任务 id；后出现的覆盖先出现的）。
    /// 原始后台调用行据此显示最终状态；<see cref="ClearMessages"/> 清空。
    /// </summary>
    private Dictionary<string, OpenCodeAsyncCompletion> _asyncCompletions = new(StringComparer.Ordinal);

    /// <summary>创建空的消息时间线视图。</summary>
    public OpenCodeConversationView()
    {
        Orientation = Orientation.Vertical;
        Spacing = 0;
        Padding = new Thickness(0, 4, 0, 4);
        Unloaded += (_, _) => _retryTimer?.Stop();
        Loaded += (_, _) => { StartRetryTimer(); ConnectViewport(); };
        Unloaded += (_, _) => DisconnectViewport();
        LayoutUpdated += (_, _) => { _idleLayoutVersion = RenderVersion; QueueWindowUpdate(); };
    }

    /// <summary>
    /// 用户点击某工具部件（<c>name == "subagent"</c>）或子任务完成行时请求只读子会话窗口。
    /// 参数只含父消息 id 与通过不透明校验的子会话 id，属纯导航提示：不授权、不直接发起任何
    /// HTTP 或子会话查询。消费方须先确认来源 / 选中代际，并以 GET child info.parentID（或祖先链）
    /// 复核归属后才加载子会话消息。
    /// </summary>
    public event EventHandler<OpenCodeSubagentSessionRequestedEventArgs>? SubagentSessionRequested;

    /// <summary>question 工具锚点集合发生变化（新增 / 移除）；交互视图据此重新放置提问条目。</summary>
    internal event EventHandler? QuestionAnchorsChanged;

    /// <summary>
    /// 按 Form metadata.tool 的 messageID / id 查找时间线中对应 question 工具部件的锚点；未渲染时为 null。
    /// 只读查询，不改变渲染状态。
    /// </summary>
    internal OpenCodeQuestionAnchorHost? FindQuestionAnchor(string? messageId, string? partId)
        => !string.IsNullOrEmpty(messageId) && !string.IsNullOrEmpty(partId)
            && _questionAnchors.TryGetValue(AnchorKey(messageId, partId), out var host)
                ? host
                : null;

    private static string AnchorKey(string messageId, string partId) => messageId + "\n" + partId;

    private void RaiseQuestionAnchorsChangedIfNeeded()
    {
        if (!_questionAnchorsChanged)
        {
            return;
        }

        _questionAnchorsChanged = false;
        QuestionAnchorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空全部已渲染消息（切换会话或卸载时调用）；同时退订并停用全部子部件回调。</summary>
    public void ClearMessages() => ChangeIdleContent(ClearMessagesCore);

    private void ClearMessagesCore()
    {
        _windowGeneration++;
        _windowQueued = false;
        _window.Reset();
        _readAnchor = null;
        _followingBottom = false;
        _animateNextScroll = _animatingScroll = false;
        _scrollOwnerCurrent = null;
        _requestedOffset = null;
        _windowGaps.Clear();
        _messageIdCounts.Clear();
        _manualBackground.Clear();
        _recoveredErrorMessages.Clear();
        _recoveredRetryMessages.Clear();
        BindingCleared?.Invoke(this, EventArgs.Empty);
        ApplyAdmittedSteer(null, null);
        _renderedUserMessageIds.Clear();
        ClearCurrentRetry();
        _backgroundAction = null;
        _currentTurnActivity = null;
        _observedTurnKey = null;
        _observedTurnActive = false;
        _terminalTurnKey = null;
        foreach (var entry in _entries)
        {
            entry.Dispose?.Invoke();
        }

        Children.Clear();
        _entries.Clear();
        _byKey.Clear();
        _renderedMessageIds.Clear();
        _subagentTaskTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        _asyncCompletions = new Dictionary<string, OpenCodeAsyncCompletion>(StringComparer.Ordinal);
        _groupExpansion.Clear();
        _rowExpansion.Clear();
        _activeGroups.Clear();
        if (_questionAnchors.Count > 0)
        {
            _questionAnchors.Clear();
            _questionAnchorsChanged = true;
        }

        RaiseQuestionAnchorsChangedIfNeeded();
    }

    /// <summary>
    /// 应用一次消息投影快照（调用方已按时间升序整理）。
    /// 增量更新：未变化的消息不触碰；已渲染元素尽量复用。
    /// </summary>
    /// <param name="messages">按展示顺序（旧→新）排列的消息投影。</param>
    public void ApplyMessages(IReadOnlyList<OpenCodeMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (_scrollOwnerCurrent is { } current && !current())
        {
            _windowGeneration++;
            _windowQueued = false;
            _scrollOwnerCurrent = null;
            _followingBottom = false;
            _animateNextScroll = _animatingScroll = false;
            _readAnchor = _viewport is null ? null : _window.Capture(WindowOffset());
        }
        ChangeIdleContent(() => ApplyMessagesCore(messages));
        UpdateWindow();
    }

    private void ApplyMessagesCore(IReadOnlyList<OpenCodeMessage> messages)
    {
        _messageIdCounts.Clear();
        // Accepted raw order, before safe presentation copies and before any provisional chat rows.
        _manualBackground.Update(messages);

        // 客户端安全化：不改动调用方集合，逐条投影为净化副本（顺序与数量不变）。
        var sanitizedMessages = new List<OpenCodeMessage>(messages.Count);
        foreach (var message in messages)
        {
            sanitizedMessages.Add(SanitizeSyntheticInstruction(OpenCodeInstructionNoticeProjection.SanitizeForClient(message)));
        }

        messages = sanitizedMessages;

        // B14: presentation-only recovery projection over THIS session snapshot (never mutates messages / logs).
        var recovery = OpenCodeErrorRecoveryProjection.Project(messages, IsRecoveryContinuationCandidate);
        _recoveredErrorMessages.Clear();
        _recoveredRetryMessages.Clear();
        for (var i = 0; i < messages.Count; i++)
        {
            if (recovery.IsErrorRecovered(i)) _recoveredErrorMessages.Add(messages[i]);
            if (recovery.IsRetryRecovered(i)) _recoveredRetryMessages.Add(messages[i]);
        }

        // 异步完成通知索引：原始后台调用行（终端 / 子任务）据此显示最终状态。
        var completions = new Dictionary<string, OpenCodeAsyncCompletion>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (!IsInstructionMessage(message)
                && OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out var completion)
                && completion.Key.Length > 0)
            {
                completions[completion.Key] = completion;
            }
        }

        _asyncCompletions = completions;

        // 完成卡片标题来源：按本次快照重建映射（只读消费；标题算法由 OpenCodeSubagentTaskTitles 提供）。
        _subagentTaskTitles = OpenCodeSubagentTaskTitles.Build(messages);

        var newEntries = new List<Entry>(messages.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var renderedUserIds = new HashSet<string>(StringComparer.Ordinal);

        var keys = new List<string>(messages.Count);
        var keySet = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            var itemKey = BuildKey(messages[i], i);
            if (!keySet.Add(itemKey)) itemKey += "#" + i.ToString(CultureInfo.InvariantCulture);
            keys.Add(itemKey);
        }
        _window.SetKeys(keys);
        var range = WindowRange();

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Id is { Length: > 0 } messageId)
            {
                // 仅收真实 id；匿名兜底 key 与重复 id 序号兜底 key 不进入归属索引。
                _messageIdCounts[messageId] = _messageIdCounts.GetValueOrDefault(messageId) + 1;
                if (message.Type == OpenCodeMessageTypes.User && !OpenCodeManualBackgroundIndex.TryReadNotice(message, out _)) renderedUserIds.Add(messageId);
            }

            var key = BuildKey(message, i);
            if (!seenKeys.Add(key))
            {
                // 契约中 id 必填且唯一；出现重复时加序号兜底，保证元素一对一。
                key = key + "#" + i.ToString(CultureInfo.InvariantCulture);
            }

            var fingerprint = ComputeFingerprint(message);
            // B14: recovery state is snapshot-relative (a LATER message can recover this one), so it is tracked beside the fingerprint.
            var recoverySignature = RecoverySignature(message);

            // 呈现方式改变（普通/subagent ↔ 指令通知 ↔ 内部触发提示行）时同 key 不能就地复用：旧条目须整体替换。
            // 先移除旧条目（Dispose → Children → _entries → _byKey），使末尾 retained 清理不会二次
            // Dispose，也不会因 _byKey.Remove(旧 key) 误删随后新建的同 key 条目。
            var isNotice = IsInstructionMessage(message);
            var isAsync = !isNotice && OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out _);
            var isManual = !isNotice && !isAsync && OpenCodeManualBackgroundIndex.TryReadNotice(message, out _);
            var isInitiator = !isNotice && !isAsync && !isManual && OpenCodeStepRunPresentation.IsInternalInitiator(message);
            var retainedVisual = _byKey.TryGetValue(key, out var prior) && prior.Realized;
            var realize = i >= range.Start && i < range.End && (_window.Height(key) > 0 || retainedVisual)
                || _byKey.TryGetValue(key, out var pinned) && KeepVisual(pinned);
            if (_byKey.TryGetValue(key, out var replaced)
                && (replaced.Realized != realize || replaced.IsInstructionNotice != isNotice || replaced.IsInitiatorChip != isInitiator
                    || replaced.IsAsyncCompletion != isAsync || replaced.IsManualBackgroundNotice != isManual))
            {
                replaced.Dispose?.Invoke();
                if (replaced.Realized) Children.Remove(replaced.Element);
                _entries.Remove(replaced);
                _byKey.Remove(key);
            }

            if (_byKey.TryGetValue(key, out var existing))
            {
                existing.MessageType = message.Type;
                // 变更检测同时比较消息指纹与「当前子任务标题」：两者任一变化都走就地更新，
                // 保证完成卡片标题随前序工具描述变化而刷新。
                var taskTitle = ResolveTaskTitle(message);
                var asyncSignature = ComputeAsyncSignature(message);
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                    !string.Equals(existing.TaskTitle, taskTitle, StringComparison.Ordinal) ||
                    !string.Equals(existing.AsyncSignature, asyncSignature, StringComparison.Ordinal) ||
                    !string.Equals(existing.RecoverySignature, recoverySignature, StringComparison.Ordinal))
                {
                    existing.Update(message);
                    existing.Fingerprint = fingerprint;
                }

                existing.TaskTitle = taskTitle;
                existing.AsyncSignature = asyncSignature;
                existing.RecoverySignature = recoverySignature;
                newEntries.Add(existing);
            }
            else
            {
                var entry = realize ? CreateEntry(key, message) : CreateUnrealizedEntry(key, message);
                entry.MessageType = message.Type;
                entry.Fingerprint = fingerprint;
                entry.TaskTitle = ResolveTaskTitle(message);
                entry.AsyncSignature = ComputeAsyncSignature(message);
                entry.RecoverySignature = recoverySignature;
                _byKey[key] = entry;
                newEntries.Add(entry);
            }
            newEntries[^1].IdleMessageId = message.Id;
            newEntries[^1].Snapshot = message;
            newEntries[^1].IdleWatermark = message.Type == OpenCodeMessageTypes.Idle ? message.Time?.Created : null;
        }

        // 移除不再出现的消息（例如窗口化拉取时旧消息滑出）。
        var retained = new HashSet<Entry>(newEntries);
        foreach (var entry in _entries)
        {
            if (!retained.Contains(entry))
            {
                entry.Dispose?.Invoke();
                if (entry.Realized) Children.Remove(entry.Element);
                _byKey.Remove(entry.Key);
            }
        }

        _entries.Clear();
        _entries.AddRange(newEntries);

        // Accepted user IDs deduplicate admission rows; navigation membership is realized-only.
        RefreshRealizedMessageIds();
        _renderedUserMessageIds.Clear();
        _renderedUserMessageIds.UnionWith(renderedUserIds);
        ApplyStepChains();
        AlignWindow();
        QueueWindowUpdate();
        RaiseQuestionAnchorsChangedIfNeeded();
    }

    /// <summary>
    /// 查询给定父消息 id 是否属于当前已渲染的消息快照：严格按真实消息 id 匹配
    /// （<see cref="OpenCodeMessage.Id"/>），匿名消息与重复 id 的兜底 key 一律不参与；
    /// 空 / null 一律返回 false。只读查询，不暴露内部集合、不改变任何渲染状态。
    /// </summary>
    /// <param name="messageId">待查询的消息 id（契约内为真实 <c>msg_</c> 前缀 id）。</param>
    internal bool ContainsRenderedMessage(string? messageId)
        => !string.IsNullOrEmpty(messageId) && _renderedMessageIds.Contains(messageId);

    /// <summary>
    /// 取该消息在当前快照中对应的子任务标题（用于完成卡片）；无匹配返回 null。
    /// 只读查询，不改变渲染状态。
    /// </summary>
    private string? ResolveTaskTitle(OpenCodeMessage message)
        => message.Id is { Length: > 0 } id && _subagentTaskTitles.TryGetValue(id, out var title)
            ? title
            : null;

    // B14: identity sets (records use value equality) of this snapshot's recovered message-level errors / retry records.
    private readonly HashSet<OpenCodeMessage> _recoveredErrorMessages = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<OpenCodeMessage> _recoveredRetryMessages = new(ReferenceEqualityComparer.Instance);

    /// <summary>B14: only ordinary assistant replies may prove recovery (not notices, async/subagent completions, manual-background or internal initiator rows).</summary>
    private string RecoverySignature(OpenCodeMessage message)
        => "recovered:" + _recoveredErrorMessages.Contains(message) + "/" + _recoveredRetryMessages.Contains(message);

    private static bool IsRecoveryContinuationCandidate(OpenCodeMessage message)
        => !IsInstructionMessage(message)
            && !OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out _)
            && !OpenCodeManualBackgroundIndex.TryReadNotice(message, out _)
            && !OpenCodeStepRunPresentation.IsInternalInitiator(message)
            && !OpenCodeSubagentPresentation.TryReadCompletionMetadata(message.Metadata, out _);

    /// <summary>指令消息：system 指令通知（安全投影）或 synthetic 指令文件消息（metadata.instruction.paths）。</summary>
    private static bool IsInstructionMessage(OpenCodeMessage message)
        => OpenCodeInstructionNoticeProjection.TryGetInstructionsNotice(message, out _)
            || OpenCodeStepRunPresentation.TryGetInstructionPaths(message, out _);

    /// <summary>取指令消息的安全投影（system 通知原样投影；synthetic 指令只取路径列表，绝不取正文）。</summary>
    private static bool TryGetInstructionNotice(OpenCodeMessage message, out OpenCodeInstructionNotice notice)
    {
        if (OpenCodeInstructionNoticeProjection.TryGetInstructionsNotice(message, out notice))
        {
            return true;
        }

        if (OpenCodeStepRunPresentation.TryGetInstructionPaths(message, out var paths))
        {
            notice = new OpenCodeInstructionNotice(
                message.Id,
                paths.IsDefaultOrEmpty
                    ? "已加载指令"
                    : "已加载 " + paths.Length.ToString(CultureInfo.InvariantCulture) + " 个指令文件",
                paths,
                message.Time is { Created: > 0 } time ? time.Created : (double?)null);
            return true;
        }

        notice = null!;
        return false;
    }

    /// <summary>
    /// synthetic 指令消息的客户端安全化：只保留 id / type / time.created 与 metadata.instruction.paths，
    /// 正文（指令全文）与其余字段不进入指纹、呈现或任何视图。非指令消息原样返回（同一引用）。
    /// </summary>
    private static OpenCodeMessage SanitizeSyntheticInstruction(OpenCodeMessage message)
    {
        if (OpenCodeInstructionNoticeProjection.TryGetInstructionsNotice(message, out _)
            || !OpenCodeStepRunPresentation.TryGetInstructionPaths(message, out var paths))
        {
            return message;
        }

        return new OpenCodeMessage
        {
            Id = message.Id,
            Type = message.Type,
            Time = message.Time is { } time ? new OpenCodeMessageTime { Created = time.Created } : null,
            Metadata = JsonSerializer.SerializeToElement(new { instruction = new { paths } }),
        };
    }

    /// <summary>该部件关联的异步完成通知（按 state.metadata 中的不透明 id 匹配）；无则 null。</summary>
    private OpenCodeAsyncCompletion? ResolveCompletion(OpenCodeContentPart part)
    {
        if (_asyncCompletions.Count == 0 || part.Type != OpenCodePartTypes.Tool)
        {
            return null;
        }

        foreach (var key in OpenCodeStepRunPresentation.AsyncKeys(part))
        {
            if (_asyncCompletions.TryGetValue(key, out var completion))
            {
                return completion;
            }
        }

        return null;
    }

    private static string CompletionSignature(OpenCodeAsyncCompletion? completion)
        => completion is null
            ? string.Empty
            : completion.Key + "|" + completion.State + "|" + completion.ExitCode?.ToString(CultureInfo.InvariantCulture);

    /// <summary>assistant 消息中全部工具调用所关联异步完成通知的签名（非 assistant 为空串）。</summary>
    private string ComputeAsyncSignature(OpenCodeMessage message)
    {
        if (OpenCodeManualBackgroundIndex.TryReadNotice(message, out var items))
            return "manual-notice:" + string.Join(",", items.Select(item =>
                _manualBackground.IsNoticeItemRepresented(message.Id, item.Kind, item.Title) ? "represented" : "notice"));
        if (message.Type != OpenCodeMessageTypes.Assistant || message.Content is not { Count: > 0 } parts)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part?.Id is { } id && message.Id is { } parent && _manualBackground.Contains(parent, id))
                builder.Append("manual:").Append(id).Append(';');
            if (part is not null && ResolveCompletion(part) is { } completion)
            {
                builder.Append(CompletionSignature(completion)).Append(';');
            }
        }

        return builder.ToString();
    }

    private static string CompletionBody(OpenCodeMessage message)
    {
        if (message.Text is { } text) return text;
        var parts = new List<string>();
        if (message.Content is { } content)
            foreach (var part in content)
                if (part is { Type: OpenCodePartTypes.Text, Text: { } body }) parts.Add(body);
        return string.Join("\n\n", parts);
    }

    private static string BuildKey(OpenCodeMessage message, int index) =>
        message.Id is { Length: > 0 } id
            ? id
            : "anon:" + index.ToString(CultureInfo.InvariantCulture) + ":" + (message.Type ?? "?");

    private static string ComputeFingerprint(OpenCodeMessage message)
    {
        try
        {
            return JsonSerializer.Serialize(message, OpenCodeJson.Options);
        }
        catch (Exception)
        {
            // 指纹仅用于变更检测；序列化失败时退化为“每次都更新”，不影响正确性。
            return Guid.NewGuid().ToString("N");
        }
    }

    private static string ComputeFingerprint(OpenCodeContentPart part)
    {
        try
        {
            return JsonSerializer.Serialize(part, OpenCodeJson.Options);
        }
        catch (Exception)
        {
            // 指纹仅用于变更检测；序列化失败时退化为“每次都更新”，不影响正确性。
            return Guid.NewGuid().ToString("N");
        }
    }

    // The same full-history chain algorithm consumes these leaves. Only callbacks and XAML
    // objects are absent; LayoutParts still determines runs, boundaries and reasoning status.
    private Entry CreateUnrealizedEntry(string key, OpenCodeMessage message)
    {
        var instruction = IsInstructionMessage(message);
        var async = !instruction && OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out _);
        var manual = !instruction && !async && OpenCodeManualBackgroundIndex.TryReadNotice(message, out _);
        var initiator = !instruction && !async && !manual && OpenCodeStepRunPresentation.IsInternalInitiator(message);
        var process = !instruction && (async || manual || message.Type is OpenCodeMessageTypes.Shell or OpenCodeMessageTypes.LocationSwitched);
        var state = process || !instruction && message.Type == OpenCodeMessageTypes.Assistant && !initiator
            ? new AssistantState { Message = message, IsProcessNotice = process } : null;
        var groups = new Dictionary<string, OpenCodeToolGroupView>(StringComparer.Ordinal);
        void Apply(OpenCodeMessage current)
        {
            if (state is null) return;
            state.Message = current;
            if (!process)
            {
                var parts = new List<PartEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var content = current.Content ?? new List<OpenCodeContentPart>();
                for (var i = 0; i < content.Count; i++)
                {
                    var part = content[i];
                    var kind = part.Type ?? "?";
                    var partKey = part.Id is { Length: > 0 } id ? id : kind + ":" + i.ToString(CultureInfo.InvariantCulture);
                    if (!seen.Add(partKey)) partKey += "#" + i.ToString(CultureInfo.InvariantCulture);
                    parts.Add(new PartEntry
                    {
                        Key = partKey, Kind = kind, IsGroupableTool = kind == OpenCodePartTypes.Tool,
                        LastPart = part, LastCompletion = ResolveCompletion(part),
                        ManualBackground = current.Id is { } parent && part.Id is { } partId && _manualBackground.Contains(parent, partId),
                        Update = (_, _, _) => { },
                    });
                }
                LayoutParts(current, null, parts, groups, state);
                state.HasBoundary |= current.Error is not null && !_recoveredErrorMessages.Contains(current)
                    || current.Retry is not null && !_recoveredRetryMessages.Contains(current);
                state.CompletionShown = OpenCodeSubagentPresentation.TryReadCompletionMetadata(current.Metadata, out var completion);
                if (!state.CompletionShown || !OpenCodeSubagentPresentation.IsOpaqueSessionId(completion.ChildSessionId)) return;
                state.HasBoundary = false;
                state.Runs.Clear();
                var completed = new OpenCodeAsyncCompletion(OpenCodeAsyncCompletionKind.Subagent,
                    completion.ChildSessionId, completion.State ?? string.Empty, ResolveTaskTitle(current) ?? string.Empty, null,
                    CompletionBody(current) ?? string.Empty, completion.ChildSessionId, null);
                AddProcess(key + "/completion", ChipModel(OpenCodeStepRunPresentation.DescribeAsyncCompletion(completed)),
                    "async:" + completion.ChildSessionId);
                return;
            }
            state.Runs.Clear();
            if (manual)
            {
                if (!OpenCodeManualBackgroundIndex.TryReadNotice(current, out var items)) return;
                var run = NewRun(key + "/manual-notice");
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (_manualBackground.IsNoticeItemRepresented(current.Id, item.Kind, item.Title)) continue;
                    var name = item.Kind == "shell" ? "终端（后台）" : "子智能体（后台）";
                    var model = ChipModel(new OpenCodeTimelineRowContent(name, item.Title, "后台运行中", OpenCodeToolRowStatus.Running,
                        name + " · " + item.Title + " · 后台运行中（通知时尚未完成）", name, Array.Empty<OpenCodeToolProperty>(), item.Title));
                    AddLeaf(run, model, "notice:" + key + "/" + i.ToString(CultureInfo.InvariantCulture), inline: true);
                }
                if (run.Leaves.Count > 0) state.Runs.Add(run);
            }
            else if (async && OpenCodeStepRunPresentation.TryReadAsyncCompletion(current, out var notification))
                AddProcess(key + "/process", ChipModel(OpenCodeStepRunPresentation.DescribeAsyncCompletion(notification)),
                    notification.Key.Length > 0 ? "async:" + notification.Key : "notice:" + key);
            else if (current.Type == OpenCodeMessageTypes.LocationSwitched)
                AddProcess(key + "/process", ChipModel(new OpenCodeTimelineRowContent("目录", current.Location?.Directory ?? "（未提供）", string.Empty, null,
                    "已切换目录：" + (current.Location?.Directory ?? "（未提供）"), "目录", Array.Empty<OpenCodeToolProperty>(), current.Location?.Directory)), "notice:" + key);
            else
            {
                var status = current.Status == OpenCodeShellStatuses.Running ? OpenCodeToolRowStatus.Running
                    : current.Status is OpenCodeShellStatuses.Killed or OpenCodeShellStatuses.Timeout ? OpenCodeToolRowStatus.Error
                    : current.Status == OpenCodeShellStatuses.Exited && current.Exit is { ValueKind: JsonValueKind.Number } exit && exit.TryGetInt32(out var code)
                        ? code == 0 ? OpenCodeToolRowStatus.Completed : OpenCodeToolRowStatus.Error : OpenCodeToolRowStatus.Unknown;
                AddProcess(key + "/process", ChipModel(new OpenCodeTimelineRowContent("终端", current.Command ?? string.Empty, string.Empty, status,
                    DescribeShell(current), "终端", Array.Empty<OpenCodeToolProperty>(), current.Output?.Output)), "notice:" + key);
            }
        }
        StepRun NewRun(string groupId)
        {
            if (!groups.TryGetValue(groupId, out var group)) groups[groupId] = group = new(groupId, OnStepGroupToggled);
            return new() { Group = group, Owner = state!, StartsMessage = true, EndsMessage = true };
        }
        static void AddLeaf(StepRun run, OpenCodeToolRowModel model, string operation, bool inline)
        {
            run.Tools.Add(model);
            run.Leaves.Add(new PartEntry { Key = operation, Kind = "process", Update = (_, _, _) => { },
                Presentation = () => model, OperationKey = () => operation, InlinePresentation = inline });
        }
        void AddProcess(string groupId, OpenCodeToolRowModel model, string operation, bool inline = false)
        {
            var run = NewRun(groupId);
            AddLeaf(run, model, operation, inline);
            state!.Runs.Add(run);
        }
        Apply(message);
        return new Entry { Key = key, Assistant = state, Update = Apply, IsInstructionNotice = instruction,
            IsAsyncCompletion = async, IsManualBackgroundNotice = manual, IsInitiatorChip = initiator,
            Dispose = () => { foreach (var group in groups.Values) group.Dispose(); } };
    }

    private Entry CreateEntry(string key, OpenCodeMessage message)
    {
        // 指令通知优先：命中即只构造安全通知呈现，绝不先建 Synthetic/Assistant 正文或 Subagent 包装。
        if (TryGetInstructionNotice(message, out var notice))
        {
            return CreateInstructionNoticeEntry(key, notice);
        }

        // 异步完成通知（后台终端 / 子任务 / 插件后台任务）：工具样式的紧凑行，完整结果在详情中。
        if (OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out _))
        {
            return CreateAsyncCompletionEntry(key, message);
        }

        if (OpenCodeManualBackgroundIndex.TryReadNotice(message, out _))
            return CreateManualBackgroundNoticeEntry(key, message);

        // 插件内部触发消息（SLIM_INTERNAL_INITIATOR 等标记）：只显示一行紧凑提示，全文仅在悬停详情中。
        if (OpenCodeStepRunPresentation.IsInternalInitiator(message))
        {
            return CreateInitiatorChipEntry(key, message);
        }

        switch (message.Type)
        {
            case OpenCodeMessageTypes.User:
                return CreateUserEntry(key, message);
            case OpenCodeMessageTypes.Assistant:
                return CreateAssistantEntry(key, message);
            case OpenCodeMessageTypes.Idle:
                return CreateIdleEntry(key, message);
            case OpenCodeMessageTypes.Synthetic:
            case OpenCodeMessageTypes.System:
                return CreateSyntheticEntry(key, message, DescribePlainText);
            case OpenCodeMessageTypes.Skill:
                return CreateCaptionEntry(key, message, DescribeSkill);
            case OpenCodeMessageTypes.Shell:
                return CreateShellEntry(key, message);
            case OpenCodeMessageTypes.Compaction:
                return CreateCompactionEntry(key, message);
            case OpenCodeMessageTypes.AgentSwitched:
                return CreateCaptionEntry(key, message,
                    m => "已切换 agent：" + (m.Agent ?? "（未提供）"));
            case OpenCodeMessageTypes.ModelSwitched:
                return CreateModelSwitchEntry(key, message);
            case OpenCodeMessageTypes.LocationSwitched:
                return CreateDirectoryEntry(key, message);
            default:
                return CreateCaptionEntry(key, message,
                    m => "（未识别的消息类型：" + (m.Type ?? "未知") + "）");
        }
    }

    private Entry CreateDirectoryEntry(string key, OpenCodeMessage message)
    {
        var chip = new OpenCodeTimelineChipView("OpenCodeDirectorySwitch", "\uE8B7");
        void Apply(OpenCodeMessage m) => chip.UpdateRow(new OpenCodeTimelineRowContent(
            "目录", m.Location?.Directory ?? "（未提供）", string.Empty, null,
            "已切换目录：" + (m.Location?.Directory ?? "（未提供）"), "目录",
            Array.Empty<OpenCodeToolProperty>(), m.Location?.Directory));
        Apply(message);
        return CreateProcessEntry(key, message, chip.Element, Apply, () => ChipModel(chip.Content),
            chip.OpenDetail, chip.HoverEnter, chip.HoverExit, chip.SetDetailAnchor, () => chip.Glyph, chip.Dispose);
    }

    private Entry CreateManualBackgroundNoticeEntry(string key, OpenCodeMessage message)
    {
        var root = new StackPanel();
        var group = new OpenCodeToolGroupView(key + "/manual-notice", OnStepGroupToggled);
        root.Children.Add(group.Element);
        var state = new AssistantState { Element = root, Message = message, IsProcessNotice = true };
        var chips = new List<OpenCodeTimelineChipView>();
        void Apply(OpenCodeMessage current)
        {
            foreach (var old in chips) old.Dispose();
            chips.Clear();
            group.ItemsHost.Children.Clear();
            state.Runs.Clear();
            state.Message = current;
            if (!OpenCodeManualBackgroundIndex.TryReadNotice(current, out var items)) return;
            var run = new StepRun { Group = group, Owner = state, StartsMessage = true, EndsMessage = true };
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                // The exact existing call/real terminal owns that operation; never add a second one.
                if (_manualBackground.IsNoticeItemRepresented(current.Id, item.Kind, item.Title)) continue;
                var glyph = item.Kind == "shell" ? OpenCodeStepRunPresentation.TerminalGlyph : OpenCodeStepRunPresentation.SubagentGlyph;
                var name = item.Kind == "shell" ? "终端（后台）" : "子智能体（后台）";
                var chip = new OpenCodeTimelineChipView("OpenCodeManualBackgroundNotice", glyph);
                chip.UpdateRow(new OpenCodeTimelineRowContent(name, item.Title, "后台运行中", OpenCodeToolRowStatus.Running,
                    name + " · " + item.Title + " · 后台运行中（通知时尚未完成）", name,
                    Array.Empty<OpenCodeToolProperty>(), item.Title));
                chips.Add(chip);
                group.ItemsHost.Children.Add(chip.Element);
                var itemKey = "notice:" + key + "/" + index.ToString(CultureInfo.InvariantCulture);
                run.Leaves.Add(new PartEntry
                {
                    Key = itemKey, Kind = "process", Element = chip.Element, Update = (_, _, _) => { },
                    OpenDetail = chip.OpenDetail, HoverEnter = chip.HoverEnter, HoverExit = chip.HoverExit,
                    SetDetailAnchor = chip.SetDetailAnchor, Presentation = () => ChipModel(chip.Content),
                    Glyph = () => chip.Glyph, OperationKey = () => itemKey, InlinePresentation = true,
                });
                run.Tools.Add(ChipModel(chip.Content));
            }
            root.Visibility = chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (chips.Count > 0) state.Runs.Add(run);
        }
        Apply(message);
        return new Entry
        {
            Key = key, Element = root, Assistant = state, IsManualBackgroundNotice = true, Update = Apply,
            Dispose = () => { foreach (var chip in chips) chip.Dispose(); group.Dispose(); },
        };
    }

    private static Entry CreateModelSwitchEntry(string key, OpenCodeMessage message)
    {
        var view = new OpenCodeTurnChipView(message);
        return new Entry { Key = key, Element = view.Element, Update = view.Update, Dispose = view.Dispose };
    }

    /// <summary>Safe standalone chip above the following work; never a processing-group leaf.</summary>
    private Entry CreateInstructionNoticeEntry(string key, OpenCodeInstructionNotice notice)
    {
        var view = new OpenCodeInstructionNoticeView(notice);
        return new Entry { Key = key, Element = view.Element, IsInstructionNotice = true, Update = m =>
        {
            if (TryGetInstructionNotice(m, out var updated)) view.Update(updated);
        }, Dispose = view.Dispose };
    }

    private static OpenCodeToolRowModel ChipModel(OpenCodeTimelineRowContent content) => new()
    {
        Name = content.Name, Status = content.Status ?? OpenCodeToolRowStatus.Unknown,
        StatusText = content.Summary, Target = content.Target, AccessibleName = content.AccessibleName,
        IsBackground = content.Name.EndsWith("（后台）", StringComparison.Ordinal),
    };

    // Non-body process notices use the same chain machinery as assistant tool/reasoning runs.
    // The leaf controller remains the actual detail/child source, never a replacement properties card.
    private Entry CreateProcessEntry(string key, OpenCodeMessage? message, FrameworkElement content,
        Action<OpenCodeMessage> apply, Func<OpenCodeToolRowModel> presentation,
        Action open, Action enter, Action exit, Action<FrameworkElement?> anchor, Func<string> glyph,
        Action dispose, bool instruction = false, bool async = false, Func<string>? operationKey = null, bool inline = false)
    {
        var group = new OpenCodeToolGroupView(key + "/process", OnStepGroupToggled);
        var state = new AssistantState { Element = group.Element, Message = message, IsProcessNotice = true };
        var leaf = new PartEntry
        {
            Key = key, Kind = "process", Element = content, Update = (_, _, _) => { },
            OpenDetail = open, HoverEnter = enter, HoverExit = exit, SetDetailAnchor = anchor,
            Presentation = presentation, Glyph = glyph, OperationKey = operationKey ?? (() => "notice:" + key),
            InlinePresentation = inline,
        };
        var run = new StepRun { Group = group, Owner = state, StartsMessage = true, EndsMessage = true };
        run.Leaves.Add(leaf);
        run.Tools.Add(presentation());
        state.Runs.Add(run);
        group.ItemsHost.Children.Add(content);
        return new Entry
        {
            Key = key, Element = group.Element, Assistant = state, IsInstructionNotice = instruction, IsAsyncCompletion = async,
            Update = m => { apply(m); state.Message = m; run.Tools.Clear(); run.Tools.Add(presentation()); },
            Dispose = () => { dispose(); group.Dispose(); },
        };
    }

    /// <summary>构造内部触发消息的紧凑提示行（图标 + 短标签；全文只在悬停详情中显示）。</summary>
    private static Entry CreateInitiatorChipEntry(string key, OpenCodeMessage message)
    {
        var chip = new OpenCodeTimelineChipView("OpenCodeInternalInitiator", "\uE8BD");

        void Apply(OpenCodeMessage m)
        {
            var properties = new List<OpenCodeToolProperty>
            {
                new(0, "来源", "oh-my-opencode-slim 插件（内部自动发送）", OpenCodeToolPropertyKind.Text),
                new(0, "消息类型", m.Type ?? "未知", OpenCodeToolPropertyKind.Code),
            };
            chip.Update(
                OpenCodeStepRunPresentation.InternalInitiatorLabel,
                OpenCodeStepRunPresentation.InternalInitiatorLabel + "（插件内部发送，悬停查看全文）",
                OpenCodeStepRunPresentation.InternalInitiatorLabel,
                properties,
                OpenCodeStepRunPresentation.DescribeInternalInitiatorText(m));
        }

        Apply(message);
        return new Entry
        {
            Key = key,
            Element = chip.Element,
            Update = Apply,
            IsInitiatorChip = true,
            Dispose = chip.Dispose,
        };
    }

    /// <summary>
    /// 异步完成通知行：名称“终端完成 / 子任务完成 / 后台任务完成”、目标为命令 / 任务、摘要为退出码；
    /// 详情为键值 + 结果正文；子任务完成的详情含“打开子会话”（只转发已通过不透明校验的 childID）。
    /// </summary>
    private Entry CreateAsyncCompletionEntry(string key, OpenCodeMessage message)
    {
        var chip = new OpenCodeTimelineChipView("OpenCodeAsyncCompletion", "\uE930");
        string? parentId = message.Id;
        string? childId = null;
        var operationKey = "notice:" + key;

        void OpenChild()
        {
            if (parentId is { Length: > 0 } messageId && childId is { } child)
            {
                SubagentSessionRequested?.Invoke(this, new OpenCodeSubagentSessionRequestedEventArgs(messageId, child));
            }
        }

        void Apply(OpenCodeMessage m)
        {
            parentId = m.Id;
            if (!OpenCodeStepRunPresentation.TryReadAsyncCompletion(m, out var completion))
            {
                return;
            }

            childId = completion.ChildSessionId is { } child && OpenCodeSubagentPresentation.IsOpaqueSessionId(child) ? child : null;
            operationKey = completion.Key.Length > 0 ? "async:" + completion.Key : "notice:" + key;
            var content = OpenCodeStepRunPresentation.DescribeAsyncCompletion(completion);
            if (childId is null && content.LinkText is not null)
            {
                content = content with { LinkText = null };
            }

            chip.UpdateRow(content, childId is null ? null : OpenChild);
        }

        Apply(message);
        return CreateProcessEntry(key, message, chip.Element, Apply, () => ChipModel(chip.Content),
            chip.OpenDetail, chip.HoverEnter, chip.HoverExit, chip.SetDetailAnchor, () => chip.Glyph, () =>
            {
                childId = null;
                chip.Dispose();
            }, async: true, operationKey: () => operationKey);
    }

    private Entry CreateUserEntry(string key, OpenCodeMessage message)
    {
        var presenter = new OpenCodeRichTextPresenter();
        presenter.Render(message.Text, OpenCodeRichTextStyle.Body);

        // 默认最多两行，更长时用 chevron 展开 / 收起（H14）；呈现器实例与 key 跨更新保持。
        var bubble = new OpenCodeUserBubbleView(presenter.Element, delivered: true);
        if (_rowExpansion.TryGetValue(key, out var expanded)) bubble.SetExpanded(expanded);
        bubble.Element.Margin = new Thickness(0, 6, 0, 6);

        return new Entry
        {
            Key = key,
            Element = bubble.Element,
            Update = m => presenter.Render(m.Text, OpenCodeRichTextStyle.Body),
            Dispose = () => _rowExpansion[key] = bubble.IsExpanded,
        };
    }

    private Entry CreateAssistantEntry(string key, OpenCodeMessage message)
    {
        var subagentMarker = CreateSubagentMarker(HorizontalAlignment.Left);
        var partsPanel = new StackPanel { Spacing = 8 };
        var errorRow = new AssistantErrorRow();
        errorRow.Element.Visibility = Visibility.Collapsed;
        var retryText = new TextBlock
        {
            Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionStyleKey),
            Foreground = OpenCodeInteractionUi.TryBrush("SystemFillColorCautionBrush"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed,
        };

        var root = new StackPanel
        {
            Spacing = 0,
            Children = { subagentMarker, partsPanel, errorRow.Element, retryText },
        };

        // 完成卡片控制器包裹原内容 root：Element 身份稳定，普通内容/卡片切换与折叠状态由控制器负责；
        // 导航只转发已通过校验的 childID，空父消息 id 不发。
        var subagentCompletion = new OpenCodeSubagentCompletionView(
            root,
            childId =>
            {
                if (message.Id is { Length: > 0 } messageId)
                {
                    SubagentSessionRequested?.Invoke(
                        this,
                        new OpenCodeSubagentSessionRequestedEventArgs(messageId, childId));
                }
            });

        var partEntries = new List<PartEntry>();
        var groups = new Dictionary<string, OpenCodeToolGroupView>(StringComparer.Ordinal);
        var state = new AssistantState { Element = subagentCompletion.Element };
        OpenCodeToolGroupView? completionGroup = null;
        StepRun? completionRun = null;

        void Apply(OpenCodeMessage m)
        {
            UpdateAssistant(m, partsPanel, partEntries, groups, state, subagentMarker, errorRow, retryText);
            // 旧消息级标记让位给完成卡片，避免重复展示；普通消息仍走完整原内容路径。
            subagentMarker.Visibility = Visibility.Collapsed;
            var completion = OpenCodeSubagentPresentation.TryReadCompletionMetadata(m.Metadata, out var info) ? info : null;
            state.CompletionShown = completion is not null;
            subagentCompletion.Update(completion, ResolveTaskTitle(m), CompletionBody(m));
            if (completion is not null && OpenCodeSubagentPresentation.IsOpaqueSessionId(completion.ChildSessionId))
            {
                if (completionGroup is null)
                {
                    completionGroup = new OpenCodeToolGroupView(key + "/completion", OnStepGroupToggled);
                    completionRun = new StepRun { Group = completionGroup, Owner = state, StartsMessage = true, EndsMessage = true };
                    completionRun.Leaves.Add(new PartEntry
                    {
                        Key = key, Kind = "process", Element = subagentCompletion.ProcessContent, Update = (_, _, _) => { },
                        Presentation = () => ChipModel(subagentCompletion.ProcessPresentation),
                        OperationKey = () => "async:" + (OpenCodeSubagentPresentation.TryReadCompletionMetadata(state.Message?.Metadata, out var current) ? current.ChildSessionId : key),
                        Glyph = () => OpenCodeStepRunPresentation.SubagentGlyph,
                        OpenDetail = subagentCompletion.OpenDetail, HoverEnter = subagentCompletion.HoverEnter, HoverExit = subagentCompletion.HoverExit,
                        SetDetailAnchor = subagentCompletion.SetDetailAnchor,
                    });
                }
                subagentCompletion.SetProcessGroup(completionGroup.Element);
                if (completionGroup.ItemsHost.Children.Count == 0) completionGroup.ItemsHost.Children.Add(subagentCompletion.ProcessContent);
                completionRun!.Tools.Clear();
                completionRun.Tools.Add(ChipModel(subagentCompletion.ProcessPresentation));
                state.Runs.Clear();
                state.Runs.Add(completionRun);
                state.HasBoundary = false;
            }
        }

        Apply(message);

        return new Entry
        {
            Key = key,
            Element = subagentCompletion.Element,
            Assistant = state,
            Update = Apply,
            Dispose = () =>
            {
                foreach (var partEntry in partEntries)
                {
                    partEntry.Dispose?.Invoke();
                }

                foreach (var group in groups.Values)
                {
                    group.Dispose();
                }

                subagentCompletion.Dispose();
                completionGroup?.Dispose();
            },
        };
    }

    private void UpdateAssistant(
        OpenCodeMessage message,
        StackPanel partsPanel,
        List<PartEntry> partEntries,
        Dictionary<string, OpenCodeToolGroupView> groups,
        AssistantState state,
        TextBlock subagentMarker,
        AssistantErrorRow errorRow,
        TextBlock retryText)
    {
        ApplySubagentMarker(subagentMarker, message);
        state.Message = message;

        var parts = message.Content ?? new List<OpenCodeContentPart>();
        var newEntries = new List<PartEntry>(parts.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var kind = part.Type ?? "?";
            var partKey = part.Id is { Length: > 0 } id
                ? id
                : kind + ":" + i.ToString(CultureInfo.InvariantCulture);
            if (!seenKeys.Add(partKey))
            {
                partKey = partKey + "#" + i.ToString(CultureInfo.InvariantCulture);
            }

            var completion = ResolveCompletion(part);
            var manual = message.Id is { } parent && part.Id is { } manualPartId && _manualBackground.Contains(parent, manualPartId);
            var fingerprint = ComputeFingerprint(part) + "\n" + CompletionSignature(completion) + "\nmanual:" + manual;

            PartEntry? existing = null;
            foreach (var candidate in partEntries)
            {
                if (candidate.Key == partKey)
                {
                    existing = candidate;
                    break;
                }
            }

            if (existing is not null && existing.Kind == kind)
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    existing.Update(part, message.Id, completion);
                    existing.Fingerprint = fingerprint;
                    existing.LastPart = part;
                    existing.LastCompletion = completion;
                    existing.ManualBackground = manual;
                }

                newEntries.Add(existing);
            }
            else
            {
                if (existing is not null)
                {
                    // 同一 id 的部件类型发生变化（罕见）：退订旧控制器后移除元素并重建。
                    existing.Dispose?.Invoke();
                    DetachPart(existing);
                    partEntries.Remove(existing);
                }

                var entry = kind is OpenCodePartTypes.Tool or OpenCodePartTypes.Reasoning
                    ? CreateLazyPartEntry(partKey, kind, part, message.Id, completion)
                    : CreatePartEntry(partKey, kind, part, message.Id, completion);
                entry.Fingerprint = fingerprint;
                entry.LastPart = part;
                entry.LastCompletion = completion;
                entry.ManualBackground = manual;
                newEntries.Add(entry);
            }
        }

        // 移除消失的部件。
        var retained = new HashSet<PartEntry>(newEntries);
        foreach (var entry in partEntries)
        {
            if (!retained.Contains(entry))
            {
                entry.Dispose?.Invoke();
                DetachPart(entry);
            }
        }

        LayoutParts(message, partsPanel, newEntries, groups, state);

        partEntries.Clear();
        partEntries.AddRange(newEntries);
        // B14: a recovered (hidden) error/retry record is not a visible boundary — no blank gap, step chain continues.
        state.HasBoundary |= (message.Error is not null && !_recoveredErrorMessages.Contains(message))
            || (message.Retry is not null && !_recoveredRetryMessages.Contains(message));

        // 助手消息级错误（已核实的结构化错误字段；出现即如实展示，不推断请求成败）。
        // B14: a transient link error that this same session demonstrably continued past is hidden (presentation only).
        if (message.Error is { } error && !_recoveredErrorMessages.Contains(message))
        {
            var text = string.IsNullOrWhiteSpace(error.Message) ? "（服务端未提供错误详情）" : error.Message!;
            var meta = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(error.Type))
            {
                meta.Add("type=" + error.Type);
            }
            if (error.Status is { } statusCode)
            {
                meta.Add("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture));
            }
            if (meta.Count > 0)
            {
                text += "（" + string.Join(", ", meta) + "）";
            }

            errorRow.Apply(text);
            errorRow.Element.Visibility = Visibility.Visible;
        }
        else
        {
            errorRow.Element.Visibility = Visibility.Collapsed;
        }

        // 重试信息：仅陈述服务端已记录的字段，不解释为最终状态。
        if (message.Retry is { } retry && !_recoveredRetryMessages.Contains(message))
        {
            var text = retry.Attempt is { } attempt
                ? "服务端记录了重试（第 " + attempt.ToString(CultureInfo.InvariantCulture) + " 次）"
                : "服务端记录了重试";
            if (!string.IsNullOrWhiteSpace(retry.Error?.Message))
            {
                text += "；最近错误：" + retry.Error!.Message;
            }

            retryText.Text = text;
            retryText.Visibility = Visibility.Visible;
        }
        else
        {
            retryText.Visibility = Visibility.Collapsed;
        }
    }

    private static void DetachPart(PartEntry entry)
    {
        if (entry.Host is { } host && entry.Element is not null)
        {
            host.Children.Remove(entry.Element);
            entry.Host = null;
        }
    }

    /// <summary>
    /// 部件布局（F4 / F5）：两段可见内容（文本、提问锚点、未识别部件）之间的全部思考与普通工具调用放入一个
    /// “处理过程”分组（组 ID = 段内首个可见部件 key；任意数量）；连续的思考部件合并为一行“思考”。
    /// 其余部件直接位于部件面板。元素只在必要时 Remove+Insert，换容器前总是先离开旧容器。
    /// 分组摘要与展开状态在全部消息就绪后由 <see cref="ApplyStepChains"/> 统一计算（同一回合可跨多条 assistant 消息）。
    /// </summary>
    private void LayoutParts(
        OpenCodeMessage message,
        StackPanel? partsPanel,
        List<PartEntry> entries,
        Dictionary<string, OpenCodeToolGroupView> groups,
        AssistantState state)
    {
        var layout = new List<UIElement>(entries.Count);
        var boundary = new List<bool>(entries.Count);
        var targets = new Dictionary<PartEntry, Panel?>();
        var runs = new List<(StepRun Run, List<UIElement> Items, int LayoutIndex)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var messageStreaming = message.Time is { Completed: null };

        var i = 0;
        while (i < entries.Count)
        {
            var entry = entries[i];
            if (!entry.IsStepPart)
            {
                if (entry.Element is not null) layout.Add(entry.Element);
                boundary.Add(!(entry.Kind == OpenCodePartTypes.Text && string.IsNullOrWhiteSpace(entry.LastPart?.Text)));
                targets[entry] = partsPanel;
                i++;
                continue;
            }

            var start = i;
            while (i < entries.Count && entries[i].IsStepPart)
            {
                i++;
            }

            var runEndsParts = i == entries.Count;
            var shown = new List<PartEntry>();
            var tools = new List<OpenCodeToolRowModel>();
            var reasoningRows = 0;
            double? startedAt = null;
            double? endedAt = null;
            PartEntry? lead = null;
            var texts = new List<string?>();

            var leadInProgress = false;

            void FlushReasoning(bool atRunEnd)
            {
                if (lead is not null)
                {
                    lead.ReasoningStatus = leadInProgress || (atRunEnd && runEndsParts && messageStreaming)
                        ? OpenCodeToolRowStatus.Streaming : OpenCodeToolRowStatus.Completed;
                    lead.ReasoningText = OpenCodeStepRunPresentation.MergeReasoning(texts);
                    lead.RefreshVisual?.Invoke();
                    lead.Reasoning?.Update(
                        lead.ReasoningText,
                        leadInProgress || (atRunEnd && runEndsParts && messageStreaming),
                        QualifyKey(message, lead));
                }

                lead = null;
                leadInProgress = false;
                texts.Clear();
            }

            for (var k = start; k < i; k++)
            {
                var part = entries[k];
                targets[part] = null;
                if (part.IsReasoning)
                {
                    var text = part.LastPart?.Text;

                    // S2：仍在生成的思考（消息未完成，且部件 time.completed 缺失；无时间字段时以“是消息最后一个部件”判断）
                    // 即使文本为空也立即显示“思考中…”行；已结束的空白思考（如仅有加密状态）不显示。
                    var inProgress = messageStreaming
                        && (part.LastPart?.Time is { Completed: null } || (part.LastPart?.Time is null && k == entries.Count - 1));
                    if (string.IsNullOrWhiteSpace(text) && !inProgress)
                    {
                        continue;
                    }

                    if (lead is null)
                    {
                        lead = part;
                        shown.Add(part);
                        reasoningRows++;
                    }

                    leadInProgress |= inProgress;
                    texts.Add(text);
                    continue;
                }

                FlushReasoning(atRunEnd: false);
                var snapshot = part.LastPart ?? new OpenCodeContentPart();
                tools.Add(OpenCodeToolRowPresentation.Build(snapshot, part.LastCompletion, part.ManualBackground));
                shown.Add(part);
                if (snapshot.Time is { } time)
                {
                    if (OpenCodeStepRunPresentation.IsPlausibleTimestamp(time.Created))
                    {
                        startedAt = startedAt is { } s ? Math.Min(s, time.Created) : time.Created;
                    }

                    if (OpenCodeStepRunPresentation.IsPlausibleTimestamp(time.Completed))
                    {
                        endedAt = endedAt is { } e ? Math.Max(e, time.Completed!.Value) : time.Completed;
                    }
                }
            }

            FlushReasoning(atRunEnd: true);
            if (shown.Count == 0)
            {
                // 只有空白思考：不显示分组。
                continue;
            }

            // 组 ID 必须全局唯一（H1）：v2 的 text / reasoning 部件没有 id，其兜底 key（“reasoning:0”）在每条消息中重复，
            // 若直接作为组 ID，展开状态（按链首组 ID 记住）会被所有同 key 的组共享——点一个组会切换全部组。
            var groupId = QualifyKey(message, shown[0]);
            if (!groups.TryGetValue(groupId, out var group))
            {
                group = new OpenCodeToolGroupView(groupId, OnStepGroupToggled);
                groups[groupId] = group;
            }

            var run = new StepRun
            {
                Group = group,
                Owner = state,
                ReasoningRows = reasoningRows,
                StartedAt = startedAt,
                EndedAt = endedAt,
                StartsMessage = !boundary.Contains(true),
            };
            run.Tools.AddRange(tools);
            run.Leaves.AddRange(shown);

            var items = new List<UIElement>(shown.Count);
            foreach (var part in shown)
            {
                if (partsPanel is not null)
                {
                    if (part.Element is not null) items.Add(part.Element);
                    targets[part] = group.ItemsHost;
                }
            }

            used.Add(groupId);
            runs.Add((run, items, boundary.Count));
            if (partsPanel is not null) layout.Add(group.Element);
            boundary.Add(false);
        }

        // 段首 / 段尾判定与消息级时间兜底（思考部件没有时间戳：段在消息开头 / 结尾时用消息创建 / 完成时间）。
        state.Runs.Clear();
        foreach (var (run, _, layoutIndex) in runs)
        {
            run.EndsMessage = !boundary.GetRange(layoutIndex + 1, boundary.Count - layoutIndex - 1).Contains(true);
            if (run.StartsMessage && message.Time is { } messageTime
                && OpenCodeStepRunPresentation.IsPlausibleTimestamp(messageTime.Created))
            {
                run.StartedAt = run.StartedAt is { } s ? Math.Min(s, messageTime.Created) : messageTime.Created;
            }

            if (run.EndsMessage && message.Time?.Completed is { } completed
                && OpenCodeStepRunPresentation.IsPlausibleTimestamp(completed))
            {
                run.EndedAt = run.EndedAt is { } e ? Math.Max(e, completed) : completed;
            }

            state.Runs.Add(run);
        }

        state.HasBoundary = boundary.Contains(true);

        // 换容器（或不再显示）的部件先离开旧容器（测试宿主与 WinUI 都不允许一个元素同时有两个父级）。
        foreach (var entry in entries)
        {
            if (entry.Host is { } host && entry.Element is not null && !ReferenceEquals(host, targets[entry]))
            {
                host.Children.Remove(entry.Element);
                entry.Host = null;
            }
        }

        // 不再使用的组：移出面板并停用。
        var stale = new List<string>();
        foreach (var pair in groups)
        {
            if (!used.Contains(pair.Key))
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var key in stale)
        {
            if (partsPanel is not null) partsPanel.Children.Remove(groups[key].Element);
            groups[key].Dispose();
            groups.Remove(key);
        }

        if (partsPanel is not null) foreach (var (run, items, _) in runs)
        {
            AlignChildren(run.Group.ItemsHost, items);
        }

        if (partsPanel is not null) AlignChildren(partsPanel, layout);
        foreach (var entry in entries)
        {
            entry.Host = targets[entry];
        }
    }

    /// <summary>
    /// 部件在整个时间线中唯一的 key：有真实部件 id（工具调用 id、带 id 的思考）时原样使用；
    /// 否则（v2 的 text / reasoning 部件无 id，兜底 key 为“类型:序号”）加上所属消息 id 前缀。
    /// </summary>
    private static string QualifyKey(OpenCodeMessage message, PartEntry entry)
        => entry.LastPart?.Id is { Length: > 0 }
            ? entry.Key
            : (message.Id is { Length: > 0 } id ? id : "anon") + "/" + entry.Key;

    private void OnStepGroupToggled(string groupId, bool expanded)
    {
        CaptureInteractionAnchor();
        if (!_groupExpansion.TryGetValue(groupId, out var previous) || previous != expanded) InvalidateIdleViewport();
        _groupExpansion[groupId] = expanded;

        ApplyStepChains();
        UpdateWindow();
    }

    /// <summary>
    /// 默认进行中展开、终态收起；手动选择优先，在本组整个生命周期内跨流式刷新和终态保留。
    /// </summary>
    private bool ResolveExpansion(string leaderId, bool active, HashSet<string> nowActive)
    {
        if (active) nowActive.Add(leaderId);
        return _groupExpansion.TryGetValue(leaderId, out var userExpanded) ? userExpanded : active;
    }

    /// <summary>
    /// 跨消息计算处理过程分组：同一回合按步骤拆成多条 assistant 消息时，消息 A 末尾的段与紧随其后的消息 B 开头的段
    /// （两者之间没有可见文本 / 提问 / 其他消息）连成一条链，链首显示合并摘要（“已处理 X 秒”，进行中“处理中… N 秒”），
    /// 后续段隐藏头部并跟随链首的展开状态（默认进行中展开、终态收起，手动选择优先；规则见 ResolveExpansion）。
    /// 只剩隐藏的后续段（或什么都不显示）的 assistant 条目整体折叠，避免空行间距。
    /// </summary>
    private void ApplyStepChains()
    {
        var chains = new List<List<StepRun>>();
        List<StepRun>? open = null;
        for (var index = 0; index < _entries.Count; index++)
        {
            var state = _entries[index].Assistant;
            if (state is null)
            {
                open = null;
                continue;
            }

            if (state.Runs.Count == 0)
            {
                if (state.HasBoundary || state.CompletionShown)
                {
                    open = null;
                }
                else if (open is not null)
                {
                    // 尚无可见内容的 assistant 消息（新步骤刚开始）：不打断链。
                }

                continue;
            }

            for (var r = 0; r < state.Runs.Count; r++)
            {
                var run = state.Runs[r];
                if (r == 0 && run.StartsMessage && open is not null)
                {
                    open.Add(run);
                }
                else
                {
                    open = new List<StepRun> { run };
                    chains.Add(open);
                }

            }

            if (!state.Runs[^1].EndsMessage)
            {
                open = null;
            }
        }

        // A completed assistant message is only a step boundary. Latch activity for the whole
        // latest turn until idle/new user or an authoritative selected-session terminal signal.
        var turnStart = 0;
        var lastBoundaryIdle = false;
        for (var index = 0; index < _entries.Count; index++)
        {
            var type = _entries[index].MessageType;
            if (type is OpenCodeMessageTypes.User or OpenCodeMessageTypes.Idle && !_entries[index].IsManualBackgroundNotice)
            {
                turnStart = index + 1;
                lastBoundaryIdle = type == OpenCodeMessageTypes.Idle;
            }
        }
        var turnKey = turnStart > 0 ? _entries[turnStart - 1].Key : (_entries.Count > 0 ? _entries[0].Key : string.Empty);
        if (_observedTurnKey != turnKey) { _observedTurnKey = turnKey; _observedTurnActive = false; }
        var currentOwners = new HashSet<AssistantState>();
        OpenCodeMessage? executingAssistant = null;
        for (var index = turnStart; index < _entries.Count; index++)
            if (_entries[index].Assistant is { } owner)
            {
                currentOwners.Add(owner);
                if (!owner.IsProcessNotice) executingAssistant = owner.Message;
                if (!owner.IsProcessNotice && !owner.CompletionShown && owner.Message?.Time is { Completed: null }) _observedTurnActive = true;
                foreach (var run in owner.Runs)
                    foreach (var tool in run.Tools)
                        if (tool.IsActive && !tool.IsBackground) _observedTurnActive = true;
            }
        var wholeTurnActive = _currentTurnActivity ?? (_observedTurnActive && _terminalTurnKey != turnKey);
        if (lastBoundaryIdle && currentOwners.Count == 0) wholeTurnActive = false;

        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nowActive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chain in chains)
        {
            var leaves = new List<PartEntry>();
            var operations = new HashSet<string>(StringComparer.Ordinal);
            var invocationKeys = new HashSet<string>(StringComparer.Ordinal);
            var reasoningRows = 0;
            double? startedAt = null;
            double? endedAt = null;
            foreach (var run in chain)
            {
                run.HasVisibleLeaves = false;
                foreach (var part in run.Leaves)
                {
                    var operation = part.OperationKey?.Invoke();
                    if (part.LastPart is { } snapshot && !part.IsReasoning)
                    {
                        // Distinct calls stay distinct even if they reuse one child session.
                        operation = "call:" + (snapshot.Id ?? run.Group.GroupId + "/" + part.Key);
                        foreach (var asyncKey in OpenCodeStepRunPresentation.AsyncKeys(snapshot))
                            invocationKeys.Add("async:" + asyncKey);
                        if (part.LastCompletion is { Key.Length: > 0 } completion) invocationKeys.Add("async:" + completion.Key);
                    }
                    operation ??= run.Group.GroupId + "/" + part.Key;
                    var unique = !invocationKeys.Contains(operation) && operations.Add(operation);
                    if (part.Element is not null) part.Element.Visibility = unique ? Visibility.Visible : Visibility.Collapsed;
                    if (unique) { leaves.Add(part); run.HasVisibleLeaves = true; }
                }
                reasoningRows += run.ReasoningRows;
                if (run.StartedAt is { } s)
                {
                    startedAt = startedAt is { } current ? Math.Min(current, s) : s;
                }

                if (run.EndedAt is { } e)
                {
                    endedAt = endedAt is { } current ? Math.Max(current, e) : e;
                }
            }

            var tools = new List<OpenCodeToolRowModel>();
            foreach (var part in leaves)
                if (part.Presentation is { } presentation) tools.Add(presentation());
                else if (part.LastPart is { } snapshot && !part.IsReasoning)
                    tools.Add(OpenCodeToolRowPresentation.Build(snapshot, part.LastCompletion, part.ManualBackground));

            var summary = OpenCodeStepRunPresentation.Build(
                tools,
                reasoningRows,
                startedAt,
                endedAt,
                wholeTurnActive && currentOwners.Contains(chain[0].Owner),
                now, authoritativeTurnState: true);
            var leaderId = chain[0].Group.GroupId;
            var expanded = ResolveExpansion(leaderId, summary.IsActive, nowActive);
            // B17: decorative working phrases follow only real state — the latest visible step and the model
            // that is actually executing (including a new assistant with no visible parts yet), never the UI dropdown.
            var latestLeaf = leaves.Count > 0 ? leaves[^1] : null;
            // Prefer the newest still-running real step; a later completed background notification must not mask it.
            var currentLeaf = latestLeaf;
            for (var p = leaves.Count - 1; p >= 0; p--)
            {
                var candidate = leaves[p];
                var model = StepModel(candidate);
                if (candidate.IsReasoning && candidate.ReasoningStatus == OpenCodeToolRowStatus.Streaming
                    || model is { Status: OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming, IsBackground: false })
                { currentLeaf = candidate; break; }
            }
            var currentModel = currentLeaf is null ? null : StepModel(currentLeaf);
            var currentStep = currentModel is not null
                ? currentModel.Name + (string.IsNullOrWhiteSpace(currentModel.Target) ? string.Empty : " · " + currentModel.Target.Replace('\r', ' ').Replace('\n', ' '))
                    + (string.IsNullOrEmpty(currentModel.StatusText) ? string.Empty : " · " + currentModel.StatusText)
                : currentLeaf?.IsReasoning == true ? (currentLeaf.ReasoningStatus == OpenCodeToolRowStatus.Streaming ? "思考中…" : "思考 · 已完成") : "处理中…";
            chain[0].Group.SetCurrentStep(currentStep);
            var latestReasoning = latestLeaf?.IsReasoning == true && latestLeaf.ReasoningStatus == OpenCodeToolRowStatus.Streaming;
            var anyToolRunning = tools.Exists(t => t.Status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming);
            // Missing model on the newest message means unknown; never borrow a previous step's model.
            var executingModelId = summary.IsActive ? executingAssistant?.Model?.Id : null;
            chain[0].Group.SetWorkingContext(latestReasoning, anyToolRunning, executingModelId);
            foreach (var part in leaves) part.SetDetailAnchor?.Invoke(null);
            for (var k = 0; k < chain.Count; k++)
            {
                chain[k].IsFollower = k > 0;
                var leaf = leaves.Count == 1 && k == 0 ? leaves[0] : null;
                leaf?.SetDetailAnchor?.Invoke(summary.IsActive ? null : chain[k].Group.DetailAnchor);
                var leafModel = leaf?.Presentation?.Invoke()
                    ?? (leaf?.LastPart is { } snapshot && !leaf.IsReasoning ? OpenCodeToolRowPresentation.Build(snapshot, leaf.LastCompletion, leaf.ManualBackground) : null);
                chain[k].Group.BindSingleLeaf(leaf?.OpenDetail, leaf?.HoverEnter, leaf?.HoverExit,
                    leaf is null ? null : leafModel?.Name ?? "思考",
                    leaf is null ? null : leaf.Glyph?.Invoke() ?? (leafModel is null ? "\uEA80" : leafModel.GlyphOverride ?? OpenCodeToolPartView.KindGlyph(leafModel.Kind)),
                    leaf is null ? null : leafModel?.Status ?? (leaf.IsReasoning ? leaf.ReasoningStatus : null));
                chain[k].Group.SetBackgroundAction(leaf?.BackgroundAction?.Invoke());
                chain[k].Group.SetInlineLeaf(leaf?.InlinePresentation == true && !summary.IsActive);
                chain[k].Group.Update(summary, expanded, headerVisible: k == 0);
                if (chain[k].Owner.Element is not null && chain[k].Group.ShowsItems)
                {
                    var items = new List<UIElement>();
                    foreach (var part in chain[k].Leaves)
                    {
                        part.EnsureVisual?.Invoke();
                        if (part.Element is null) continue;
                        var host = chain[k].Group.ItemsHost;
                        if (part.Host is not null && !ReferenceEquals(part.Host, host)) part.Host.Children.Remove(part.Element);
                        part.Host = host;
                        part.Element.Visibility = leaves.Contains(part) ? Visibility.Visible : Visibility.Collapsed;
                        items.Add(part.Element);
                    }
                    AlignChildren(chain[k].Group.ItemsHost, items);
                }
                chain[k].Group.SetVisibility(k == 0 || (leaves.Count > 1 && chain[k].HasVisibleLeaves));
            }
        }

        _activeGroups.Clear();
        _activeGroups.UnionWith(nowActive);

        foreach (var entry in _entries)
        {
            if (entry.Assistant is not { } state)
            {
                continue;
            }

            var hasVisibleRun = false;
            foreach (var run in state.Runs)
            {
                if (!run.IsFollower || (run.HasVisibleLeaves && run.Group.IsExpanded))
                {
                    hasVisibleRun = true;
                    break;
                }
            }

            var visible = state.HasBoundary || state.CompletionShown || hasVisibleRun;
            if (!visible) _window.Measure(entry.Key, 0);
            else if (_window.Height(entry.Key) == 0) _window.Measure(entry.Key, _window.EstimatedHeight);
            if (state.Element is { } element)
            {
                element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                element.Margin = state.HasBoundary ? new Thickness(0, 6, 0, 6) : new Thickness(0);
            }
        }
        if (_retryDeadline is not null && _retryStatus is not null && !Children.Contains(_retryStatus)) Children.Add(_retryStatus);
        ReconcileSteerRows();
    }

    private static OpenCodeToolRowModel? StepModel(PartEntry part)
        => part.Presentation?.Invoke() ?? (part.LastPart is { } snapshot && !part.IsReasoning
            ? OpenCodeToolRowPresentation.Build(snapshot, part.LastCompletion, part.ManualBackground) : null);

    private static void AlignChildren(Panel panel, List<UIElement> elements)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], element))
            {
                continue;
            }

            panel.Children.Remove(element);
            panel.Children.Insert(Math.Min(i, panel.Children.Count), element);
        }

        while (panel.Children.Count > elements.Count)
        {
            panel.Children.RemoveAt(panel.Children.Count - 1);
        }
    }

    private PartEntry CreateLazyPartEntry(string key, string kind, OpenCodeContentPart part, string? parent, OpenCodeAsyncCompletion? completion)
    {
        PartEntry? visual = null;
        PartEntry descriptor = null!;
        FrameworkElement? detailAnchor = null;
        var snapshot = part;
        var messageId = parent;
        var notification = completion;
        var disposed = false;
        OpenCodeToolRowModel Model() => OpenCodeToolRowPresentation.Build(snapshot, notification, descriptor.ManualBackground);
        void Ensure()
        {
            if (disposed) return;
            if (visual is null)
            {
                visual = CreatePartEntry(key, kind, snapshot, messageId, notification);
                descriptor.Element = visual.Element;
                visual.SetDetailAnchor?.Invoke(detailAnchor);
                if (!_idleContentChanging) RaiseQuestionAnchorsChangedIfNeeded();
            }
            if (kind == OpenCodePartTypes.Reasoning)
                visual.Reasoning?.Update(descriptor.ReasoningText,
                    descriptor.ReasoningStatus == OpenCodeToolRowStatus.Streaming,
                    snapshot.Id is { Length: > 0 } ? key : (messageId is { Length: > 0 } ? messageId : "anon") + "/" + key);
        }
        descriptor = new PartEntry
        {
            Key = key, Kind = kind, IsGroupableTool = kind == OpenCodePartTypes.Tool,
            EnsureVisual = Ensure,
            RefreshVisual = () => { if (visual is not null) Ensure(); },
            Update = (current, id, finished) =>
            {
                snapshot = current; messageId = id; notification = finished;
                visual?.Update(current, id, finished);
            },
            OpenDetail = () => { if (!disposed) { Ensure(); visual!.OpenDetail?.Invoke(); } },
            HoverEnter = () => { if (!disposed) { Ensure(); visual!.HoverEnter?.Invoke(); } },
            HoverExit = () => visual?.HoverExit?.Invoke(),
            SetDetailAnchor = anchor => { detailAnchor = anchor; visual?.SetDetailAnchor?.Invoke(anchor); },
            Presentation = kind == OpenCodePartTypes.Tool ? Model : null,
            Glyph = () => kind == OpenCodePartTypes.Reasoning ? "\uEA80" : Model().GlyphOverride ?? OpenCodeToolPartView.KindGlyph(Model().Kind),
            ConfigureBackground = action => visual?.ConfigureBackground?.Invoke(action),
            BackgroundAction = () => kind == OpenCodePartTypes.Tool && _backgroundAction is not null && Model().CanMoveToBackground
                ? () => { if (!disposed && Model().CanMoveToBackground && snapshot.Id is { Length: > 0 } && messageId is { Length: > 0 } id) _backgroundAction?.Invoke(id, snapshot); } : null,
            Dispose = () => { disposed = true; visual?.Dispose?.Invoke(); visual = null; descriptor.Element = null!; },
        };
        return descriptor;
    }

    private PartEntry CreatePartEntry(string partKey, string kind, OpenCodeContentPart part, string? parentMessageId, OpenCodeAsyncCompletion? completion)
    {
        switch (kind)
        {
            case OpenCodePartTypes.Text:
            {
                var presenter = new OpenCodeRichTextPresenter();
                presenter.Render(part.Text, OpenCodeRichTextStyle.Body);
                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    Element = new Border
                    {
                        Child = presenter.Element, Padding = new Thickness(12, 10, 12, 10),
                        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                        Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", OpenCodeInteractionUi.CardBackgroundBrushKey),
                            ("BorderBrush", OpenCodeInteractionUi.CardStrokeBrushKey)),
                    },
                    Update = (p, _, _) => presenter.Render(p.Text, OpenCodeRichTextStyle.Body),
                };
            }

            case OpenCodePartTypes.Reasoning:
            {
                // “思考”行（F4）：外观同工具行，完整文本在悬停详情中；连续思考由 LayoutParts 合并到段首行。
                var row = new OpenCodeReasoningRowView();
                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    Element = row.Element,
                    Reasoning = row,
                    OpenDetail = row.OpenDetail,
                    HoverEnter = row.HoverEnter,
                    HoverExit = row.HoverExit,
                    SetDetailAnchor = row.SetDetailAnchor,
                    // 文本由 LayoutParts 按合并结果写入（LastPart 已由调用方更新）。
                    Update = (_, _, _) => { },
                    Dispose = row.Dispose,
                };
            }

            case OpenCodePartTypes.Tool when OpenCodeToolRowPresentation.IsQuestionTool(part):
            {
                // question 工具（G3 / D3）：“处理过程”分组内的普通工具行（“提问”，详情为问题 + 所选回答）。
                // 同时保留一个不可见的提问锚点：交互视图仍把匹配的待处理 Form 条目放入其中（不在时间线中显示独立卡片），
                // 回答在输入区的提问遮罩中进行；提交 / 取消 / 重查路径不变。
                var view = new OpenCodeToolPartView();
                view.SetBackgroundAction(_backgroundAction);
                view.Update(part, parentMessageId, completion, parentMessageId is { } parent && part.Id is { } id && _manualBackground.Contains(parent, id));
                EventHandler<OpenCodeSubagentSessionRequestedEventArgs> questionHandler =
                    (_, e) => SubagentSessionRequested?.Invoke(this, e);
                view.SubagentSessionRequested += questionHandler;
                var host = new OpenCodeQuestionAnchorHost { Visibility = Visibility.Collapsed };
                host.UpdateFallback(OpenCodeToolRowPresentation.BuildQuestionFallback(part));
                var questionRoot = new StackPanel { Spacing = 0 };
                questionRoot.Children.Add(view.Element);
                questionRoot.Children.Add(host);
                string? anchorKey = null;
                if (parentMessageId is { Length: > 0 } anchorMessageId && part.Id is { Length: > 0 } anchorPartId)
                {
                    anchorKey = AnchorKey(anchorMessageId, anchorPartId);
                    _questionAnchors[anchorKey] = host;
                    _questionAnchorsChanged = true;
                }

                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    IsGroupableTool = true,
                    Element = questionRoot,
                    OpenDetail = view.TogglePinnedDetail,
                    HoverEnter = view.HoverEnterDetail,
                    HoverExit = view.HoverExitDetail,
                    SetDetailAnchor = view.SetDetailAnchor,
                    ConfigureBackground = view.SetBackgroundAction,
                    BackgroundAction = () => view.BackgroundAction,
                    Update = (p, messageId, c) =>
                    {
                        view.Update(p, messageId, c);
                        host.UpdateFallback(OpenCodeToolRowPresentation.BuildQuestionFallback(p));
                    },
                    Dispose = () =>
                    {
                        view.SubagentSessionRequested -= questionHandler;
                        view.Dispose();
                        if (anchorKey is not null
                            && _questionAnchors.TryGetValue(anchorKey, out var registered)
                            && ReferenceEquals(registered, host))
                        {
                            _questionAnchors.Remove(anchorKey);
                            _questionAnchorsChanged = true;
                        }
                    },
                };
            }

            case OpenCodePartTypes.Tool:
            {
                var view = new OpenCodeToolPartView();
                view.SetBackgroundAction(_backgroundAction);
                view.Update(part, parentMessageId, completion, parentMessageId is { } parent && part.Id is { } id && _manualBackground.Contains(parent, id));
                // subagent 工具行固定详情时的「打开子会话」只向上转发；本视图在移除/替换时退订并 Dispose（无 stale callbacks）。
                EventHandler<OpenCodeSubagentSessionRequestedEventArgs> handler =
                    (_, e) => SubagentSessionRequested?.Invoke(this, e);
                view.SubagentSessionRequested += handler;
                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    IsGroupableTool = true,
                    // 入树的是工具行根元素（同一实例）；控制器由 Update 闭包持有，节点身份跨轮询保持。
                    Element = view.Element,
                    OpenDetail = view.TogglePinnedDetail,
                    HoverEnter = view.HoverEnterDetail,
                    HoverExit = view.HoverExitDetail,
                    SetDetailAnchor = view.SetDetailAnchor,
                    ConfigureBackground = view.SetBackgroundAction,
                    BackgroundAction = () => view.BackgroundAction,
                    Update = (p, messageId, c) => view.Update(p, messageId, c, messageId is { } parent && p.Id is { } id && _manualBackground.Contains(parent, id)),
                    Dispose = () =>
                    {
                        view.SubagentSessionRequested -= handler;
                        view.Dispose();
                    },
                };
            }

            default:
            {
                var text = new TextBlock
                {
                    FontSize = 12,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                    Text = "（未识别的部件类型：" + (part.Type ?? "未知") + "）",
                };
                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    Element = text,
                    Update = (_, _, _) => { },
                };
            }
        }
    }

    private Entry CreateCaptionEntry(string key, OpenCodeMessage message, Func<OpenCodeMessage, string> describe)
    {
        var text = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        text.Text = describe(message);

        return new Entry
        {
            Key = key,
            Element = text,
            Update = m => text.Text = describe(m),
        };
    }

    /// <summary>
    /// synthetic / system 说明行：附带「子任务结果」消息级标记（构造时折叠，仅完成 metadata 命中时显示）。
    /// </summary>
    private Entry CreateSyntheticEntry(string key, OpenCodeMessage message, Func<OpenCodeMessage, string> describe)
    {
        var subagentMarker = CreateSubagentMarker(HorizontalAlignment.Center);
        var text = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        var root = new StackPanel
        {
            Spacing = 2,
            Children = { subagentMarker, text },
        };

        // 完成卡片控制器包裹原内容 root；导航只转发已通过校验的 childID，空父消息 id 不发。
        var subagentCompletion = new OpenCodeSubagentCompletionView(
            root,
            childId =>
            {
                if (message.Id is { Length: > 0 } messageId)
                {
                    SubagentSessionRequested?.Invoke(
                        this,
                        new OpenCodeSubagentSessionRequestedEventArgs(messageId, childId));
                }
            });

        void Apply(OpenCodeMessage m)
        {
            text.Text = m.Type is OpenCodeMessageTypes.Synthetic or OpenCodeMessageTypes.System && m.Text == OpenCodeNoticeDismissals.AutomaticRestartText ? OpenCodeNoticeDismissals.DisplayNoticeText(m.Text) : describe(m);
            // 旧消息级标记让位给完成卡片，避免重复展示。
            subagentMarker.Visibility = Visibility.Collapsed;
            subagentCompletion.Update(
                OpenCodeSubagentPresentation.TryReadCompletionMetadata(m.Metadata, out var info) ? info : null,
                ResolveTaskTitle(m), CompletionBody(m));
        }

        Apply(message);
        return new Entry
        {
            Key = key,
            Element = subagentCompletion.Element,
            Update = Apply,
            Dispose = subagentCompletion.Dispose,
        };
    }

    /// <summary>创建「子任务结果」消息级标记（构造时折叠；同一实例跨增量更新）。</summary>
    private static TextBlock CreateSubagentMarker(HorizontalAlignment alignment) => new()
    {
        FontSize = 12,
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = alignment,
        IsTextSelectionEnabled = true,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>
    /// 按已核准形状（<c>metadata.source == "subagent"</c> 且 childID 通过不透明校验）更新标记；
    /// 不匹配（含 missing/malformed）时折叠，消息其余内容按通用路径原样展示。
    /// </summary>
    private static void ApplySubagentMarker(TextBlock marker, OpenCodeMessage message)
    {
        if (OpenCodeSubagentPresentation.TryReadCompletionMetadata(message.Metadata, out var completion))
        {
            marker.Text = OpenCodeSubagentPresentation.DescribeCompletionMarker(completion);
            marker.Visibility = Visibility.Visible;
        }
        else
        {
            marker.Visibility = Visibility.Collapsed;
        }
    }

    private Entry CreateShellEntry(string key, OpenCodeMessage message)
    {
        var chip = new OpenCodeTimelineChipView("OpenCodeShell", OpenCodeStepRunPresentation.TerminalGlyph);
        void Apply(OpenCodeMessage m)
        {
            var status = m.Status == OpenCodeShellStatuses.Running ? OpenCodeToolRowStatus.Running
                : m.Status is OpenCodeShellStatuses.Killed or OpenCodeShellStatuses.Timeout ? OpenCodeToolRowStatus.Error
                : m.Status == OpenCodeShellStatuses.Exited && m.Exit is { ValueKind: JsonValueKind.Number } exit && exit.TryGetInt32(out var code)
                    ? code == 0 ? OpenCodeToolRowStatus.Completed : OpenCodeToolRowStatus.Error : OpenCodeToolRowStatus.Unknown;
            chip.UpdateRow(new OpenCodeTimelineRowContent("终端", m.Command ?? string.Empty, string.Empty,
                status, DescribeShell(m), "终端", new[] { new OpenCodeToolProperty(0, "命令", m.Command ?? "（未提供）", OpenCodeToolPropertyKind.Code) },
                m.Output?.Output));
        }
        Apply(message);
        return CreateProcessEntry(key, message, chip.Element, Apply, () => ChipModel(chip.Content),
            chip.OpenDetail, chip.HoverEnter, chip.HoverExit, chip.SetDetailAnchor, () => chip.Glyph, chip.Dispose);
    }

    private Entry CreateCompactionEntry(string key, OpenCodeMessage message)
    {
        var caption = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        var summaryText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var summaryExpander = new Expander
        {
            Header = new TextBlock { Text = "摘要", FontSize = 12, Opacity = 0.75 },
            Content = summaryText,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };

        var root = new StackPanel
        {
            Spacing = 6,
            Children = { caption, summaryExpander },
        };
        if (_rowExpansion.TryGetValue(key, out var expanded)) summaryExpander.IsExpanded = expanded;

        void Apply(OpenCodeMessage m)
        {
            caption.Text = DescribeCompaction(m);
            if (string.IsNullOrWhiteSpace(m.Summary))
            {
                summaryExpander.Visibility = Visibility.Collapsed;
            }
            else
            {
                summaryText.Text = m.Summary;
                summaryExpander.Visibility = Visibility.Visible;
            }
        }

        Apply(message);
        return new Entry { Key = key, Element = root, Update = Apply, Dispose = () => _rowExpansion[key] = summaryExpander.IsExpanded };
    }

    /// <summary>回合结束：居中分隔线 + “回合结束 · 成功 · 12:04”；可读名称保留“回合结束：成功”。</summary>
    private static Entry CreateIdleEntry(string key, OpenCodeMessage message)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(12, 0, 12, 0),
        };
        ApplyStyle(text, "CaptionSubtleTextBlockStyle");
        var leftLine = CreateSeparatorLine();
        var rightLine = CreateSeparatorLine();
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(leftLine);
        row.Children.Add(text);
        row.Children.Add(rightLine);
        SetIdleColumns(row, leftLine, text, rightLine);

        void Apply(OpenCodeMessage m)
        {
            text.Text = DescribeIdleSeparator(m);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, DescribeIdle(m).Trim('—', ' '));
        }

        Apply(message);
        return new Entry { Key = key, Element = row, Update = Apply, IdleLabel = text };
    }

    private static string DescribeIdleSeparator(OpenCodeMessage message)
    {
        var outcome = DescribeIdle(message).Trim('—', ' ');
        const string prefix = "回合结束：";
        var label = outcome.StartsWith(prefix, StringComparison.Ordinal) ? outcome[prefix.Length..] : outcome;
        var text = "回合结束 · " + label;
        if (message.Time is { Created: > 0 } time)
        {
            try
            {
                text += " · " + DateTimeOffset.FromUnixTimeMilliseconds((long)time.Created).ToLocalTime()
                    .ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 时间超出范围时不显示时间。
            }
        }

        return text;
    }

    private static string DescribeIdle(OpenCodeMessage message)
    {
        var outcome = message.Outcome switch
        {
            OpenCodeSessionOutcomes.Succeeded => "成功",
            OpenCodeSessionOutcomes.Failed => "失败",
            OpenCodeSessionOutcomes.Interrupted => "已中断",
            null => "（未提供 outcome）",
            var other => "未识别 outcome：" + other,
        };

        // idle 是已核实的“回合级”终态边界，不代表任何单次 prompt 的请求级成败。
        return "— 回合结束：" + outcome + " —";
    }

    private static string DescribePlainText(OpenCodeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            return message.Text!;
        }

        return string.IsNullOrWhiteSpace(message.Description) ? "（无文本）" : message.Description!;
    }

    private static string DescribeSkill(OpenCodeMessage message)
    {
        var name = message.Name ?? message.Skill ?? "（未命名）";
        var text = string.IsNullOrWhiteSpace(message.Text) ? string.Empty : "：" + message.Text;
        return "技能调用：" + name + text;
    }

    private static string DescribeShell(OpenCodeMessage message)
    {
        var status = message.Status switch
        {
            OpenCodeShellStatuses.Running => "运行中",
            OpenCodeShellStatuses.Exited => "已退出",
            OpenCodeShellStatuses.Timeout => "超时",
            OpenCodeShellStatuses.Killed => "已终止",
            null => "（未提供状态）",
            var other => "未识别状态：" + other,
        };

        var text = "终端：" + (message.Command ?? "（无命令）") + "（" + status + "）";

        // exit 的原始值可能是 number 或 "Infinity"/"-Infinity"/"NaN" 字符串，按原样展示。
        if (message.Exit is { } exit && exit.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            text += "，退出码 " + exit;
        }

        return text;
    }

    private static string DescribeCompaction(OpenCodeMessage message)
    {
        var status = message.Status switch
        {
            OpenCodeCompactionStatuses.Running => "进行中",
            OpenCodeCompactionStatuses.Completed => "已完成",
            OpenCodeCompactionStatuses.Failed => "失败",
            null => "（未提供状态）",
            var other => "未识别状态：" + other,
        };

        var reason = message.Reason switch
        {
            OpenCodeCompactionReasons.Auto => "自动",
            OpenCodeCompactionReasons.Manual => "手动",
            null => "（未提供原因）",
            var other => "未识别原因：" + other,
        };

        var text = "上下文压缩：" + status + "（原因：" + reason + "）";

        if (message.Status == OpenCodeCompactionStatuses.Failed &&
            !string.IsNullOrWhiteSpace(message.Error?.Message))
        {
            text += "；错误：" + message.Error!.Message;
        }

        return text;
    }

    private static void SetIdleColumns(Grid row, FrameworkElement left, FrameworkElement text, FrameworkElement right)
    {
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(right, 2);
    }

    private static Border CreateSeparatorLine()
    {
        var line = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyStyle(line, "OpenCodeDividerBorderStyle");
        return line;
    }

    /// <summary>应用内置/应用文本样式；缺失时不改变控件外观。</summary>
    internal static void ApplyStyle(FrameworkElement element, string key)
    {
        try
        {
            if (Application.Current?.Resources is { } resources && resources[key] is Style style)
            {
                element.Style = style;
            }
        }
        catch
        {
            // 样式不可用时保持默认外观。
        }
    }

    /// <summary>安全取主题画刷：键不存在时返回 null（不抛异常，控件回退到默认外观）。</summary>
    internal static Brush? TryBrush(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } resources)
            {
                // 用索引器读取；键缺失抛 KeyNotFoundException，由外层 catch 兜底。
                // 不依赖 ResourceDictionary.TryGetValue 在不同 WinUI 版本中的可用性。
                return resources[key] as Brush;
            }
        }
        catch
        {
            // 资源字典在极端时机（关闭中）可能不可用；回退默认外观即可。
        }

        return null;
    }
}

/// <summary>
/// 时间线中 question 工具部件的锚点（§4.1）：交互视图把匹配（metadata.tool.messageID / id）的待处理 Form 条目
/// 放入这里；没有条目时显示只读占位（标题 + 已回答 / 已跳过 / 等待回答）。本类不发送任何请求、不持有回调。
/// </summary>
internal sealed class OpenCodeQuestionAnchorHost : StackPanel
{
    private readonly Border _fallback;
    private readonly TextBlock _titleText;
    private readonly TextBlock _statusText;
    private UIElement? _card;
    private string _fallbackAccessibleName = "提问";

    internal OpenCodeQuestionAnchorHost()
    {
        Spacing = 4;
        _titleText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        OpenCodeConversationView.ApplyStyle(_titleText, "BodyStrongTextBlockStyle");
        _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        OpenCodeConversationView.ApplyStyle(_statusText, "CaptionSubtleTextBlockStyle");
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(_titleText);
        panel.Children.Add(_statusText);
        _fallback = new Border
        {
            Child = panel,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };
        OpenCodeConversationView.ApplyStyle(_fallback, "OpenCodeQuestionFallbackChromeStyle");
        Children.Add(_fallback);
    }

    /// <summary>当前是否承载了一个可回答的提问条目。</summary>
    internal bool HasCard => _card is not null;

    internal void UpdateFallback(OpenCodeQuestionFallback fallback)
    {
        _titleText.Text = "提问：" + fallback.Title;
        _statusText.Text = fallback.StatusText;
        _fallbackAccessibleName = "提问：" + fallback.Title + "，" + fallback.StatusText;
        UpdateAccessibleName();
    }

    private void UpdateAccessibleName() => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,
        _card is null ? _fallbackAccessibleName : Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(_card));

    /// <summary>放入提问条目（调用方保证该元素已离开旧父级）；占位随之隐藏。</summary>
    internal void Attach(UIElement card)
    {
        if (ReferenceEquals(_card, card))
        {
            UpdateAccessibleName();
            return;
        }

        if (_card is not null)
        {
            Children.Remove(_card);
        }

        Children.Add(card);
        _card = card;
        _fallback.Visibility = Visibility.Collapsed;
        UpdateAccessibleName();
    }

    /// <summary>移出提问条目；占位恢复显示。</summary>
    internal void Detach(UIElement card)
    {
        if (!ReferenceEquals(_card, card))
        {
            return;
        }

        Children.Remove(card);
        _card = null;
        _fallback.Visibility = Visibility.Visible;
        UpdateAccessibleName();
    }
}
