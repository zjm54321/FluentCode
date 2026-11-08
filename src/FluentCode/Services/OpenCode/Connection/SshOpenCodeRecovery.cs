using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;

namespace FluentCode.Services.OpenCode.Connection;

/// <summary>每个失效来源只有一次有界、单飞恢复；只替换隧道/来源，不重放写操作或写配置。</summary>
internal sealed class SshOpenCodeRecovery(Func<OpenCodeSettings, Action<string>, CancellationToken, Task<SshOpenCodeConnection>> connect,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal const int AttemptLimit = 3;
    private readonly object _sync = new();
    private Task<SshOpenCodeConnection?>? _work;

    internal Task<SshOpenCodeConnection?> RecoverAsync(OpenCodeSettings settings, Action<OpenCodeConnectionStatus> progress,
        CancellationToken ct, bool initial = false)
    {
        if (settings.Kind != OpenCodeConnectionKind.Ssh || !SshConnectionTarget.TryParse(settings.SshHost, out _))
            return Task.FromResult<SshOpenCodeConnection?>(null);
        lock (_sync) return _work ??= Task.Run(() => RunAsync(settings, progress, ct, initial), ct);
    }

    private async Task<SshOpenCodeConnection?> RunAsync(OpenCodeSettings settings, Action<OpenCodeConnectionStatus> progress,
        CancellationToken ct, bool initial)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            for (int attempt = 0; attempt < AttemptLimit; attempt++)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (attempt != 0) await (delay ?? Task.Delay)(TimeSpan.FromSeconds(attempt), budget.Token).ConfigureAwait(false);
                string label = initial ? "正在连接 SSH…" : "SSH 已断开，正在重连（" + (attempt + 1) + "/" + AttemptLimit + "）";
                progress(new(label, initial ? "正在连接 SSH…" : "正在重新连接 SSH…"));
                SshOpenCodeConnection? candidate = null;
                try
                {
                    candidate = await connect(settings, detail => progress(new(label, detail)), budget.Token).ConfigureAwait(false);
                    budget.Token.ThrowIfCancellationRequested();
                    if (!candidate.Tunnel.IsAlive) { candidate.Dispose(); continue; }
                    return candidate;
                }
                catch (SshOpenCodeException ex) when (!ex.CanRetry) { candidate?.Dispose(); progress(OpenCodeConnectionStatus.SshFailure(ex)); return null; }
                catch (OpenCodeConfigurationException ex) { candidate?.Dispose(); progress(new("SSH 连接已断开", ex.Message, true)); return null; }
                catch (Exception ex) when (ex is OpenCodeException or IOException or Win32Exception) { candidate?.Dispose(); }
                catch { candidate?.Dispose(); throw; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        progress(new("SSH 连接已断开", "SSH 连接失败（已达重试上限或等待超时）；请到 设置 › 账号与连接 重新连接。", true));
        return null;
    }
}
