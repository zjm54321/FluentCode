using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private Func<string, CancellationToken, Task<string>> CreateInstructionFileReader(
        OpenCodeImageScopeIdentity identity, Func<bool> current, CancellationToken bindingToken)
    {
        var settings = _homeSettings?.Current?.OpenCode;
        var kind = settings?.Kind;
        var distribution = settings?.WslDistribution;
        void CheckCurrent()
        {
            var live = _homeSettings?.Current?.OpenCode;
            if (!current() || bindingToken.IsCancellationRequested || live?.Kind != kind || live?.WslDistribution != distribution)
                throw new OperationCanceledException(bindingToken);
        }
        return async (path, ct) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, bindingToken);
            CheckCurrent();
            var info = await identity.Client.GetSessionInfoAsync(identity.SessionId, linked.Token);
            CheckCurrent();
            if (info.Id != identity.SessionId || info.Location?.Directory is not { } directory)
                throw new IOException("会话未提供有效的工作目录。");
            var plan = OpenCodeInstructionSource.Resolve(null, directory, path, kind == OpenCodeConnectionKind.Wsl);
            if (plan.Route == OpenCodeInstructionRoute.Unavailable) throw new IOException(plan.Reason);
            if (plan.Route == OpenCodeInstructionRoute.Http)
                return await identity.Client.ReadTextFileAsync(directory, plan.Value, linked.Token);
            var state = OpenCodeWslQuickAccess.Resolve(kind, true, distribution, plan.Value);
            if (state.Target is not { } target) throw new IOException(state.Reason);
            var mapped = await _wslHostPaths.MapAsync(target);
            CheckCurrent();
            linked.Token.ThrowIfCancellationRequested();
            var text = await OpenCodeInstructionSource.ReadLocalUtf8Async(mapped.HostPath, linked.Token);
            CheckCurrent();
            return text;
        };
    }
}
