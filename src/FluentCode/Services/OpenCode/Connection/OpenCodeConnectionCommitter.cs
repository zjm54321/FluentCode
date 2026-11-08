using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.Configuration;

namespace FluentCode.Services.OpenCode.Connection;

public enum OpenCodeCommitStage { Succeeded, Busy, Invalid, VerifyFailed, SecretFailed, SaveFailed, Cancelled }

/// <summary>提交结果；<see cref="Message"/> 为固定模板/安全类型名，绝不含密码、token 或链接。</summary>
public sealed record OpenCodeCommitOutcome(OpenCodeCommitStage Stage, string Message)
{
    public bool Succeeded => Stage == OpenCodeCommitStage.Succeeded;
    public bool RefreshDiscovery { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// “连接 OpenCode”的唯一提交管线（单账号、单飞）：
/// 1) 用内存中的凭据只读验证（GET api/location）；2) 秘密写入新的 DPAPI 引用（不覆盖旧凭据）；
/// 3) 读取最新设置并<strong>替换</strong>唯一连接槽位后保存。
/// 任一步失败都不改动已保存的连接（验证在写入之前），因此现有连接不会被先删后配导致断连。
/// </summary>
public sealed class OpenCodeConnectionCommitter
{
    private readonly Func<OpenCodeClientOptions, CancellationToken, Task> _verify;
    private readonly Func<string, string, CancellationToken, Task> _writeSecret;
    private readonly Func<Func<AppSettings, AppSettings>, CancellationToken, Task<AppSettings>> _update;
    private readonly Func<string, CancellationToken, Task> _removeSecret;
    private static int _busy;

    public OpenCodeConnectionCommitter(
        Func<OpenCodeClientOptions, CancellationToken, Task> verify,
        Func<string, string, CancellationToken, Task> writeSecret,
        Func<Func<AppSettings, AppSettings>, CancellationToken, Task<AppSettings>> update,
        Func<string, CancellationToken, Task> removeSecret)
    {
        _verify = verify ?? throw new ArgumentNullException(nameof(verify));
        _writeSecret = writeSecret ?? throw new ArgumentNullException(nameof(writeSecret));
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _removeSecret = removeSecret ?? throw new ArgumentNullException(nameof(removeSecret));
    }

    /// <summary>生产验证：真实 V2 只读请求，30 秒超时，不重试。</summary>
    public static async Task VerifyWithOpenCodeAsync(OpenCodeClientOptions options, CancellationToken cancellationToken)
    {
        using var client = new OpenCodeClient(options);
        await client.GetLocationAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public async Task<OpenCodeCommitOutcome> CommitAsync(
        OpenCodeConnectionKind kind, OpenCodeClientOptions options, string? wslDistribution, CancellationToken cancellationToken,
        string? sshHost = null, string? sshCommand = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new(OpenCodeCommitStage.Busy, "已有一次连接在进行中；只允许一个 OpenCode 连接，请等待其结束。");
        try
        {
            string? secret = options.Secret ?? options.BearerToken;
            if (options.AuthenticationMode != OpenCodeAuthenticationMode.None && string.IsNullOrWhiteSpace(secret))
                return new(OpenCodeCommitStage.Invalid, "缺少密码或凭据。");
            try
            {
                // 先做纯校验，确保验证与写入使用同一组合法值。
                OpenCodeConnectionProfile.Replace(new OpenCodeSettings(), kind, options.BaseUrl, options.AuthenticationMode,
                    options.Username, wslDistribution, sshHost, sshCommand);
            }
            catch (OpenCodeConfigurationException ex) { return new(OpenCodeCommitStage.Invalid, ex.Message); }

            try { await _verify(options, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new(OpenCodeCommitStage.Cancelled, "连接已取消；现有连接未改动。"); }
            catch (Exception ex)
            {
                return new(OpenCodeCommitStage.VerifyFailed, "后端验证失败：" + Describe(ex) + "（现有连接未改动）")
                {
                    RefreshDiscovery = ex is OpenCodeTransportException or OpenCodeTimeoutException
                        or OpenCodeApiException { StatusCode: 401 or 403 },
                };
            }

            string? stagedId = null;
            if (kind != OpenCodeConnectionKind.Ssh && options.AuthenticationMode != OpenCodeAuthenticationMode.None)
            {
                stagedId = "opencode-" + Guid.NewGuid().ToString("N");
                try { await _writeSecret(stagedId, secret!, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new(OpenCodeCommitStage.Cancelled, "凭据保存已取消；现有配置未改动。"); }
                catch (Exception ex) { return new(OpenCodeCommitStage.SecretFailed, "凭据保存失败：" + Describe(ex)); }
            }

            string? obsoleteSshSecret = null;
            try
            {
                try
                {
                    await _update(latest =>
                    {
                        OpenCodeSettings next = OpenCodeConnectionProfile.Replace(latest.OpenCode, kind, options.BaseUrl,
                            options.AuthenticationMode, options.Username, wslDistribution, sshHost, sshCommand);
                        obsoleteSshSecret = latest.OpenCode.Kind == OpenCodeConnectionKind.Ssh ? latest.OpenCode.CredentialId : null;
                        next = next with { CredentialId = kind == OpenCodeConnectionKind.Ssh ? null
                            : stagedId ?? (obsoleteSshSecret is null ? latest.OpenCode.CredentialId : null) };
                        return latest with { OpenCode = next };
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (SettingsChangeNotificationException)
                {
                    // Committed: the saved profile references stagedId, so a failing observer must not orphan it.
                }
            }
            catch (Exception ex)
            {
                // Only remove this attempt's unreferenced ciphertext. Never read, overwrite or delete the old credential.
                bool cleanupFailed = false;
                if (stagedId is not null)
                {
                    try { await _removeSecret(stagedId, CancellationToken.None).ConfigureAwait(false); }
                    catch { cleanupFailed = true; }
                }
                string message = ex is OperationCanceledException ? "配置保存已取消；现有连接未改动。" : "配置保存失败：" + Describe(ex) + "（现有连接未改动）";
                if (cleanupFailed) message += " 未引用的新凭据密文清理失败。";
                return new(ex is OperationCanceledException ? OpenCodeCommitStage.Cancelled : OpenCodeCommitStage.SaveFailed, message);
            }

            string successMessage = kind == OpenCodeConnectionKind.Ssh ? "已保存 SSH 连接设置；服务密码仅在内存中使用，未持久化。" : "已连接 OpenCode。";
            if (obsoleteSshSecret is not null)
            {
                try { await _removeSecret(obsoleteSshSecret, CancellationToken.None).ConfigureAwait(false); }
                catch { successMessage += " 旧 SSH 凭据密文清理失败，请稍后重试。"; }
            }
            return new(OpenCodeCommitStage.Succeeded, successMessage) { Warning = options.ConnectionWarning };
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>下一次设置保存后，原子清除 SSH 旧引用，再仅删除已不被当前连接引用的密文。</summary>
    public static async Task RemoveStoredSshSecretAsync(AppSettings observed,
        Func<Func<AppSettings, AppSettings>, CancellationToken, Task<AppSettings>> update,
        Func<AppSettings?> current, Func<string, CancellationToken, Task> remove, CancellationToken ct)
    {
        if (observed.OpenCode is not { Kind: OpenCodeConnectionKind.Ssh, CredentialId: { } id }) return;
        string? obsolete = null;
        try
        {
            await update(latest =>
            {
                if (latest.OpenCode.Kind != OpenCodeConnectionKind.Ssh || latest.OpenCode.CredentialId != id) return latest;
                obsolete = id;
                return latest with { OpenCode = latest.OpenCode with { CredentialId = null } };
            }, ct).ConfigureAwait(false);
        }
        catch (SettingsChangeNotificationException) { }
        if (obsolete is not null && current()?.OpenCode.CredentialId != obsolete)
            await remove(obsolete, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>OpenCode 异常消息为固定模板（不含凭据/响应体）；其他异常只给类型名与 HRESULT。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        OpenCodeException oc => oc.Message,
        _ => $"{ex.GetType().Name} (0x{ex.HResult:X8})",
    };
}
