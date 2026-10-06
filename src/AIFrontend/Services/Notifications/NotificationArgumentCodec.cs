using System;

namespace AIFrontend.Services.Notifications;

/// <summary>
/// 通知激活参数的最小解析器，以及 requestId / 参数值的字符集校验工具。
/// 出站（写入通知 arguments）与入站（解析激活参数）共用同一契约，保证往返解析精确。
/// </summary>
/// <remarks>
/// 已核实的参数格式（Microsoft Learn，AppNotificationActivatedEventArgs.Argument）：
/// 形如 <c>key=value</c> 的键值对，多个键值对以 '&amp;' 分隔，
/// 例如 <c>action=completed&amp;source=opencode&amp;requestId=abc</c>。
/// <list type="bullet">
/// <item><description>只读取本服务写入的固定键 <c>action</c> / <c>source</c> / <c>requestId</c>；
/// 不反射、不做 URL 解码、不执行任何内容。</description></item>
/// <item><description>本服务写出的所有值都满足 <see cref="IsValidValue"/> 的字符集约束
/// （不含 '&amp;' 与 '='），因此按字面切分即可精确还原。</description></item>
/// <item><description><see cref="NotificationActivationInfo.RawArguments"/> 仅为诊断保留，
/// 调用方不得将其当作可执行输入。</description></item>
/// </list>
/// </remarks>
internal static class NotificationArgumentCodec
{
    /// <summary>“完成动作”的 action 键。</summary>
    internal const string ActionKey = "action";

    /// <summary>来源标签键。</summary>
    internal const string SourceKey = "source";

    /// <summary>请求标识键。</summary>
    internal const string RequestIdKey = "requestId";

    /// <summary>本服务写入的“任务完成” action 值。</summary>
    internal const string CompletionActionValue = "completed";

    /// <summary>requestId 的最大长度。</summary>
    internal const int MaxRequestIdLength = 128;

    /// <summary>
    /// 校验调用方提供的 requestId：1..<see cref="MaxRequestIdLength"/> 个字符，仅允许 ASCII 字母、数字及 <c>. _ : -</c>。
    /// 该约束同时保证参数可安全拼入通知 arguments（不引入 '&amp;' / '='），
    /// 并避免聊天内容或密钥被误当作标识传入通知。
    /// </summary>
    internal static bool IsValidRequestId(string? requestId) =>
        !string.IsNullOrEmpty(requestId)
        && requestId.Length <= MaxRequestIdLength
        && IsValidValue(requestId);

    /// <summary>参数值字符集校验（与 requestId 相同的安全字符集）。</summary>
    internal static bool IsValidValue(string value)
    {
        foreach (char c in value)
        {
            bool ok = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == ':' || c == '-';

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 解析激活参数字符串为经最小化的 <see cref="NotificationActivationInfo"/>。
    /// 永不抛出；无法识别的内容一律降级为“非完成动作、来源/标识为 null”。
    /// </summary>
    /// <param name="rawArguments">平台提供的原始参数字符串，可为 null。</param>
    internal static NotificationActivationInfo Parse(string? rawArguments)
    {
        string raw = rawArguments ?? string.Empty;

        string? action = null;
        string? sourceTag = null;
        string? requestId = null;

        foreach (string segment in raw.Split('&'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            int separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = segment.Substring(0, separator);
            string value = segment.Substring(separator + 1);

            // 重复键取首个值，保证解析结果与书写顺序中的第一次出现一致。
            switch (key)
            {
                case ActionKey:
                    action ??= value;
                    break;
                case SourceKey:
                    sourceTag ??= value;
                    break;
                case RequestIdKey:
                    requestId ??= value;
                    break;
                default:
                    break;
            }
        }

        bool isCompletionAction = string.Equals(action, CompletionActionValue, StringComparison.Ordinal);
        NotificationSource? source = NotificationSourceLabels.TryParseTag(sourceTag);
        string? validatedRequestId = IsValidRequestId(requestId) ? requestId : null;

        return new NotificationActivationInfo(isCompletionAction, source, validatedRequestId, raw);
    }
}
