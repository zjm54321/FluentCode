namespace FluentCode.Services.OpenCode.Browser;

/// <summary>
/// Optional local composition hook, separate from the frozen protocol/backend interface. The concrete engine
/// backend forwards to its owning workspace; the peer calls it before initiating attachment, not UI composition.
/// </summary>
public interface IOpenCodeBrowserPeerBinding
{
    void BindPeer(OpenCodeBrowserPeer peer);
}
