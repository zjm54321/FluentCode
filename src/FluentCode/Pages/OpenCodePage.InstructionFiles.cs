using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private Func<string, CancellationToken, Task<string>> CreateInstructionFileReader(
        OpenCodeImageScopeIdentity identity, Func<bool> current, CancellationToken bindingToken)
        => CreateSessionFileReader(identity, current, bindingToken, (directory, path, wsl) =>
        {
            var plan = OpenCodeInstructionSource.Resolve(null, directory, path, wsl);
            return new SessionFileReadPlan(plan.Route == OpenCodeInstructionRoute.WslMapping, plan.Value, plan.Reason);
        }, identity.Client.ReadTextFileAsync, OpenCodeInstructionSource.ReadLocalUtf8Async);

    private readonly record struct SessionFileReadPlan(bool Host, string Value, string? Reason);

    /// <summary>Shared instruction/image file lease: exact session, live connection, bounded readers, WSL mapping only.</summary>
    private Func<string, CancellationToken, Task<T>> CreateSessionFileReader<T>(
        OpenCodeImageScopeIdentity identity, Func<bool> current, CancellationToken bindingToken,
        Func<string, string, bool, SessionFileReadPlan> resolve,
        Func<string, string, CancellationToken, Task<T>> sessionRead,
        Func<string, CancellationToken, Task<T>> hostRead)
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
            var plan = resolve(directory, path, kind == OpenCodeConnectionKind.Wsl);
            if (plan.Reason is not null) throw new IOException(plan.Reason);
            if (!plan.Host)
            {
                var result = await sessionRead(directory, plan.Value, linked.Token);
                CheckCurrent();
                linked.Token.ThrowIfCancellationRequested();
                return result;
            }
            var state = OpenCodeWslQuickAccess.Resolve(kind, true, distribution, plan.Value);
            if (state.Target is not { } target) throw new IOException(state.Reason);
            var mapped = await _wslHostPaths.MapAsync(target).WaitAsync(linked.Token);
            CheckCurrent();
            linked.Token.ThrowIfCancellationRequested();
            var resultLocal = await hostRead(mapped.HostPath, linked.Token);
            CheckCurrent();
            linked.Token.ThrowIfCancellationRequested();
            return resultLocal;
        };
    }
}
