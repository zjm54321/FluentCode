namespace FluentCode.Services.OpenCode.Browser;

/// <summary>
/// Optional workspace composition. WorkspaceId should include the source/session identity when profile reuse
/// is desired; the default is a fresh isolated identity. ProfileDirectory must be private to that workspace.
/// RuntimeDirectory is the absolute deployed bundle location, not a writable data directory.
/// </summary>
public sealed record OpenCodeBrowserWorkspaceOptions(
    string? WorkspaceId = null,
    string? ProfileDirectory = null,
    string? RuntimeDirectory = null,
    IOpenCodeBrowserRuntimeFactory? RuntimeFactory = null,
    IOpenCodeBrowserPageDriverFactory? DriverFactory = null);
