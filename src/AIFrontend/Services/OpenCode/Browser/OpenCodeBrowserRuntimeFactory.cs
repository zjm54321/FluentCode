using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Starts one prepared local sidecar per workspace; never installs or resolves software on PATH.</summary>
public sealed class OpenCodeBrowserRuntimeFactory : IOpenCodeBrowserRuntimeFactory
{
    public async Task<IOpenCodeBrowserRuntime> CreateAsync(OpenCodeBrowserRuntimeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = new OpenCodeBrowserRuntime(options);
        try
        {
            await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
