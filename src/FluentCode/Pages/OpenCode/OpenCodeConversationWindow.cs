using System;
using System.Collections.Generic;

namespace FluentCode.Pages.OpenCode;

// No XAML objects: the retained history and measured extent outlive recycled row visuals.
internal sealed class OpenCodeConversationWindow
{
    internal readonly record struct Anchor(string Key, double Offset);
    internal readonly record struct Range(int Start, int End, double Before, double After);
    private readonly Dictionary<string, double> _heights = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _keys = Array.Empty<string>();
    internal double EstimatedHeight { get; set; } = 80;
    internal double Extent { get; private set; }

    internal void Reset()
    {
        _keys = Array.Empty<string>();
        _heights.Clear();
        Extent = 0;
    }

    internal void SetKeys(IReadOnlyList<string> keys)
    {
        _keys = keys;
        var retained = new HashSet<string>(keys, StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var key in _heights.Keys) if (!retained.Contains(key)) stale.Add(key);
        foreach (var key in stale) _heights.Remove(key);
        Recalculate();
    }

    internal double Height(string key) => _heights.TryGetValue(key, out var height) ? height : EstimatedHeight;

    internal bool Measure(string key, double height)
    {
        if (!double.IsFinite(height) || height < 0) return false;
        var previous = Height(key);
        _heights[key] = height;
        Extent += height - previous;
        return Math.Abs(previous - height) > 0.01;
    }

    internal Anchor? Capture(double offset)
    {
        double top = 0;
        foreach (var key in _keys)
        {
            var height = Height(key);
            if (height > 0 && top + height > offset) return new(key, offset - top);
            top += height;
        }
        return _keys.Count == 0 ? null : new(_keys[^1], offset - top + Height(_keys[^1]));
    }

    internal double Restore(Anchor? anchor, double fallback)
    {
        if (anchor is not { } value) return fallback;
        double top = 0;
        foreach (var key in _keys)
        {
            if (key == value.Key) return top + value.Offset;
            top += Height(key);
        }
        return fallback;
    }

    internal Range Select(double offset, double viewport, double buffer)
    {
        var low = Math.Max(0, offset - buffer);
        var high = offset + viewport + buffer;
        var start = 0;
        double before = 0;
        while (start < _keys.Count && before + Height(_keys[start]) <= low)
            before += Height(_keys[start++]);
        var end = start;
        var afterTop = before;
        while (end < _keys.Count && afterTop < high) afterTop += Height(_keys[end++]);
        return new(start, end, before, Math.Max(0, Extent - afterTop));
    }

    private void Recalculate()
    {
        Extent = 0;
        foreach (var key in _keys) Extent += Height(key);
    }
}
