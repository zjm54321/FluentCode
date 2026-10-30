using System;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SidebarVisualFixture")]
namespace SidebarVisual.Support;
public static class BackendAccess
{
    // Test-assembly bridge to existing internal Source creation/revocation. No production API changed.
    public static OpenCodePageSource Source(string identity, long revision, Func<OpenCodeClient> client) => new(identity, revision, client);
    public static void Revoke(OpenCodePageSource source) => source.Revoke();
}
