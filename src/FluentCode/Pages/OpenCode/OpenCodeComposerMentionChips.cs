using System;
using System.Collections.Generic;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Inline "@" mention chips for the plain-TextBox composer. A TextBox cannot host inline elements and switching the
/// composer to RichEditBox would rewrite its IME/draft/ledger pipeline, so the text stays exactly "@id" (same draft text,
/// same sent parts) and a non-hit-testable layer paints a rounded, agent-coloured box behind/over each tracked mention
/// span, positioned from the TextBox's own character rectangles. Atomic delete and caret snapping live in
/// <see cref="OpenCodeComposerView"/>; this class is presentation only.
/// </summary>
internal sealed class OpenCodeComposerMentionChips
{
    private readonly TextBox _editor;
    private readonly Canvas _layer;
    private readonly Func<IReadOnlyList<OpenCodePromptContext>> _mentions;
    private readonly Func<string, string?> _runtimeColor;
    private ScrollViewer? _scroller;
    private string? _signature;

    internal OpenCodeComposerMentionChips(TextBox editor, Canvas layer, Func<IReadOnlyList<OpenCodePromptContext>> mentions,
        Func<string, string?> runtimeColor)
    {
        _editor = editor;
        _layer = layer;
        _mentions = mentions;
        _runtimeColor = runtimeColor;
        _layer.IsHitTestVisible = false;
        editor.TextChanged += (_, _) => Refresh();
        editor.SizeChanged += (_, _) => Refresh(force: true);
        editor.ActualThemeChanged += (_, _) => Refresh(force: true);
        editor.Loaded += (_, _) => { HookScroller(); Refresh(force: true); };
        editor.LayoutUpdated += (_, _) => Refresh(); // Cheap: signature-guarded (text, spans, scroll, size).
    }

    /// <summary>Chip rectangles per wrapped line segment: consecutive characters sharing a line top merge.</summary>
    internal static IReadOnlyList<Rect> Segments(IReadOnlyList<Rect> characterRects)
    {
        var result = new List<Rect>();
        foreach (var r in characterRects)
        {
            if (r.Width <= 0 && r.Height <= 0) continue;
            if (result.Count > 0 && Math.Abs(result[^1].Top - r.Top) < 1)
            {
                var last = result[^1];
                result[^1] = new Rect(last.Left, last.Top, Math.Max(last.Right, r.Right) - last.Left, Math.Max(last.Height, r.Height));
            }
            else result.Add(r);
        }
        return result;
    }

    private void HookScroller()
    {
        if (_scroller is not null) return;
        _scroller = FindScroller(_editor);
        if (_scroller is not null) _scroller.ViewChanged += (_, _) => Refresh(force: true);
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer { Name: "ContentElement" } scroller) return scroller;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    internal void Refresh(bool force = false)
    {
        var text = _editor.Text ?? string.Empty;
        var mentions = _mentions();
        var signature = text.GetHashCode() + "|" + string.Join(",", Spans(mentions, text)) + "|" + _scroller?.VerticalOffset + "|"
            + _scroller?.HorizontalOffset + "|" + _editor.ActualWidth + "|" + _editor.ActualHeight;
        if (!force && signature == _signature) return;
        _signature = signature;
        _layer.Children.Clear();
        if (_editor.ActualWidth <= 0) return;
        HookScroller();
        // GetRectFromCharacterIndex is in the text view's space (inside border/padding, moving with scroll):
        // map it through that view's live transform into the editor/layer space.
        GeneralTransform toEditor;
        try { toEditor = (_scroller?.Content as UIElement ?? _editor).TransformToVisual(_editor); }
        catch (ArgumentException) { return; }
        var dark = _editor.ActualTheme == ElementTheme.Dark;
        // Visible text area: inside border + padding (chips scrolled out of view are clipped, not drawn over chrome).
        var b = _editor.BorderThickness; var p = _editor.Padding;
        _layer.Clip = new RectangleGeometry { Rect = new Rect(b.Left + p.Left - 3, b.Top + 2, Math.Max(0, _editor.ActualWidth - b.Left - b.Right - p.Left - p.Right + 6),
            Math.Max(0, _editor.ActualHeight - b.Top - b.Bottom - 4)) };
        foreach (var m in mentions)
        {
            if (!m.Matches(text)) continue;
            var rects = new List<Rect>();
            for (var i = m.Start; i < m.End; i++)
            {
                try
                {
                    var r = toEditor.TransformBounds(_editor.GetRectFromCharacterIndex(i, false));
                    var t = toEditor.TransformBounds(_editor.GetRectFromCharacterIndex(i, true));
                    rects.Add(new Rect(r.Left, r.Top, Math.Max(0, t.Left - r.Left), r.Height));
                }
                catch (ArgumentException) { rects.Clear(); break; } // Not laid out yet; the next refresh draws it.
            }
            var color = Color(m, dark);
            foreach (var seg in Segments(rects))
            {
                var chip = new Rectangle
                {
                    Width = seg.Width + 6, Height = Math.Max(0, seg.Height + 2), RadiusX = 4, RadiusY = 4,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(dark ? 0x40 : 0x2A), color.R, color.G, color.B)),
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(dark ? 0xA0 : 0x80), color.R, color.G, color.B)),
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(chip, seg.Left - 3);
                Canvas.SetTop(chip, seg.Top - 1);
                _layer.Children.Add(chip);
            }
        }
    }

    private static IEnumerable<string> Spans(IReadOnlyList<OpenCodePromptContext> mentions, string text)
    {
        foreach (var m in mentions) if (m.Matches(text)) yield return m.Start + ":" + m.Id;
    }

    // Agents: the same colour as their icon in the agent picker / "@" list; skills: the system accent.
    private Windows.UI.Color Color(OpenCodePromptContext m, bool dark)
    {
        if (!m.IsSkill && OpenCodeAgentPresentation.Color(OpenCodeAgentIcons.Color(m.Id, _runtimeColor(m.Id), dark)) is { } agent) return agent;
        return Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Windows.UI.Color a
            ? a : Windows.UI.Color.FromArgb(255, 0, 120, 212);
    }
}
