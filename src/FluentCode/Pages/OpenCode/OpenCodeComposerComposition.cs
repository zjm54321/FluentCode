namespace FluentCode.Pages.OpenCode;

/// <summary>
/// The native editor owns text and selection until the composition's commit callbacks have drained,
/// not merely until TextCompositionEnded arrives. A queued completion belongs to exactly one cycle.
/// </summary>
internal sealed class OpenCodeComposerComposition
{
    private long _version;
    internal bool IsActive { get; private set; }

    internal void Start()
    {
        _version++;
        IsActive = true;
    }

    internal long End()
    {
        IsActive = true; // Also protect a final commit when the start event was missed on focus transfer.
        return ++_version;
    }

    internal bool TryComplete(long version)
    {
        if (version != _version) return false;
        IsActive = false;
        return true;
    }

    internal void Reset()
    {
        _version++;
        IsActive = false;
    }
}
