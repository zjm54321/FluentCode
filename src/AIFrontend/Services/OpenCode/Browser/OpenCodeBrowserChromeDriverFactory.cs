using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Null/default options launch installed Chrome with an app-owned workspace profile.</summary>
public sealed record OpenCodeBrowserChromeOptions(string? ExecutablePath = null, bool Attach = false, string? Endpoint = null);

public sealed class OpenCodeBrowserChromeDriverFactory : IOpenCodeBrowserPageDriverFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OpenCodeBrowserChromeWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly OpenCodeBrowserChromeOptions _options;

    public OpenCodeBrowserChromeDriverFactory(OpenCodeBrowserChromeOptions? options = null)
        => _options = options ?? new();

    public IOpenCodeBrowserPageDriver Create(OpenCodeBrowserPageDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.WorkspaceId)) throw new ArgumentException("Chrome requires a workspace identity.", nameof(options));
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(options.WorkspaceId, out var workspace))
            {
                workspace = new OpenCodeBrowserChromeWorkspace(_options, options, Release);
                _workspaces.Add(options.WorkspaceId, workspace);
            }
            else if (!workspace.Matches(options))
                throw new InvalidOperationException("A Chrome workspace cannot change its profile or proxy while pages are alive.");
            workspace.AddReference();
            return new OpenCodeBrowserChromeDriver(workspace);
        }
    }

    private Task Release(OpenCodeBrowserChromeWorkspace workspace)
    {
        lock (_gate)
        {
            if (!workspace.RemoveReference()) return Task.CompletedTask;
            _workspaces.Remove(workspace.Options.WorkspaceId);
        }
        return workspace.DisposeAsync().AsTask();
    }
}
