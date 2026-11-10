using System;
using System.Collections.Generic;

namespace FluentCode.Motion;

// UI-thread owned. The element holds its token; the registry never owns a callback or element.
internal sealed class FluentMotionBindings
{
    private readonly List<WeakReference<Token>> _entries = new();
    private int _pruneAt = 16;

    internal int EntryCount => _entries.Count;

    internal Token CreateToken()
    {
        if (_entries.Count >= _pruneAt) Prune();
        var token = new Token();
        _entries.Add(new(token));
        return token;
    }

    internal void Apply(bool enabled)
    {
        Prune();
        foreach (var entry in _entries.ToArray())
            if (entry.TryGetTarget(out var token)) token.Apply(enabled);
    }

    private void Prune()
    {
        _entries.RemoveAll(static entry => !entry.TryGetTarget(out _));
        _pruneAt = Math.Max(16, _entries.Count * 2);
    }

    internal sealed class Token
    {
        private readonly List<Action<bool>> _callbacks = new();

        internal void Add(Action<bool> apply) => _callbacks.Add(apply);

        internal void Apply(bool enabled)
        {
            foreach (var apply in _callbacks.ToArray())
            {
                try { apply(enabled); }
                catch { }
            }
        }
    }
}
