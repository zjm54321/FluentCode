using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private Func<OpenCodeToolImageSource, CancellationToken, Task<OpenCodeImageFile>> CreateToolImageReader(
        OpenCodeImageScopeIdentity identity, Func<bool> current, CancellationToken bindingToken)
    {
        // Reuse the instruction viewer's session-bound reader: one provenance/WSL mapping boundary.
        var read = CreateSessionFileReader(identity, current, bindingToken, (directory, path, wsl) =>
        {
            var plan = OpenCodeToolImageResolver.Plan(path, directory, wsl);
            return new SessionFileReadPlan(plan.Route == OpenCodeToolImageRoute.WslHost, plan.Path, plan.Reason);
        }, identity.Client.ReadBrowserImageFileAsync, OpenCodeToolImageResolver.ReadLocalAsync);
        return async (source, ct) => OpenCodeToolImageResolver.Validate(await read(source.Reference, ct));
    }
}
