namespace FluentCode.Services.OpenCode.Browser;

/// <summary>Top-level WebView navigation identity, independent of CDP subframe events.</summary>
public sealed class OpenCodeBrowserNavigation
{
    private ulong? _activeId, _committedId;
    public long Generation { get; private set; }
    public long Version { get; private set; }

    public void Start(ulong navigationId)
    {
        // HTTP redirects retain NavigationId, unlike a new user/script navigation.
        if (_activeId == navigationId) return;
        _activeId = navigationId; Version++;
    }

    public bool Commit(ulong navigationId)
    {
        if (!IsCurrent(navigationId) || _committedId == navigationId) return false;
        _committedId = navigationId; Generation++; return true;
    }

    public bool IsCurrent(ulong navigationId) => _activeId == navigationId;

    public void CheckWait(long acceptedVersion)
    {
        if (Version != acceptedVersion) throw new OpenCodeBrowserException("stale_generation", "页面已导航，请重新读取页面状态");
    }
}
