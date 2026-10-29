using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodePluginMutationPresentation
{
    // Only fixed local copy and exact allowlisted reasons; never echo arbitrary server text.
    internal static string Describe(PluginMutationResult result, bool preset) => result.Status switch
    {
        PluginMutationStatus.Accepted => preset ? "预设应用已被服务器接纳，仅影响之后新建的子会话。" : "清除操作已被服务器接纳。",
        PluginMutationStatus.OutcomeUnknown => "服务器结果仍未知；本地保护已保留。不会自动重试，刷新也不能确认此次结果。",
        PluginMutationStatus.Rejected => result.FailureCode switch
        {
            "rpc.method_not_found" => "当前服务不支持此操作，未执行。",
            "rpc.invalid_input" => "服务器拒绝了输入，请检查完整名称和当前会话。",
            "unauthorized" => "当前连接的认证被拒绝，未执行。",
            "failed" => result.SafeReason switch
            {
                "Current OpenCode host lacks required clearing interface" => "当前 OpenCode 服务缺少清除接口，未执行。",
                "Unable to determine session location" => "服务器无法确认会话目录，未执行。",
                "Unable to determine local database" => "服务器无法确认本地数据库，未执行。",
                "Standalone Bun runtime is unavailable or incompatible" => "服务器清理运行环境不可用，未执行。",
                "Clearing operation is already in progress for this session" => "服务器正在清除此会话，请稍后检查状态。没有重试。",
                _ => "服务器明确拒绝了此操作；没有自动重试。",
            },
            _ => "服务器明确拒绝了此操作；没有自动重试。",
        },
        _ => result.FailureCode == "session-protected" ? "当前会话仍有操作保护，请检查并确认对应的保护。" : "当前状态不允许开始操作；没有发送或重试。",
    };
}
