// Headless closure for OpenCodePageSource's unrelated desktop bridge field. Network, source
// revocation, HTTP, native WebSocket and persistent-terminal controller are production code.
namespace FluentCode.Services.OpenCode
{
    public sealed class OpenCodeDesktopBridge { }
    public sealed partial class OpenCodeClient
    {
        internal OpenCodeDesktopBridge? DesktopBridge { get; set; }
    }
}
