namespace FluentCode.Services.OpenCode.Connection;

/// <summary>顶栏短标签与完整说明分开传递；语义不依赖标点或中文文案。</summary>
public sealed record OpenCodeConnectionStatus(string Label, string Detail, bool Failed = false,
    SshFailureKind? FailureKind = null, bool Warning = false)
{
    internal static OpenCodeConnectionStatus SshFailure(SshOpenCodeException error) => new(
        error.Kind == SshFailureKind.ChangedHostKey ? "主机密钥已变化" : "SSH 连接已断开",
        error.Message + " 请到 设置 › 账号与连接 重新连接。", true, error.Kind);

    internal static OpenCodeConnectionStatus? SshWarning(string? warning) => warning is null ? null
        : new("SSH 已连接（安全提示）", warning, Warning: true);
}
