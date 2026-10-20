using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace AIFrontend.Services.OpenCode;

public enum OpenCodeCapabilityAvailability { Unknown, Available, Unavailable }

public sealed record OpenCodeCapabilityState(OpenCodeCapabilityAvailability Availability, string Reason);

/// <summary>由调用方捕获来源/代际/位置和采样时间；异步结果应用前仍须复核来源。</summary>
public sealed record OpenCodeCapabilityScope(
    string EndpointIdentity, long Revision, string? Directory, DateTimeOffset SampledAt, bool Stale = false);

/// <summary>不可变的单次只读结果。失败/未查询与成功的空列表严格区分；不保存异常或原始错误体。</summary>
public sealed record OpenCodeCapabilityRead<T>(ImmutableArray<T> Items, bool Complete, string? UnknownReason)
{
    public static OpenCodeCapabilityRead<T> Success(IEnumerable<T> items)
        => new(items.ToImmutableArray(), true, null);

    public static OpenCodeCapabilityRead<T> NotObserved()
        => new(ImmutableArray<T>.Empty, false, "状态尚未查询，无法确认");

    public static OpenCodeCapabilityRead<T> Failed(OpenCodeException error)
        => new(ImmutableArray<T>.Empty, false, error switch
        {
            OpenCodeApiException api => $"状态查询失败（HTTP {api.StatusCode}），无法确认",
            OpenCodeTimeoutException => "状态查询超时，无法确认",
            OpenCodeProtocolException => "状态响应不符合公开契约，无法确认",
            _ => "状态查询失败，无法确认",
        });
}

public sealed record OpenCodeMcpStatusEntry(OpenCodeMcpServer Server, OpenCodeCapabilityState Connection);

/// <summary>出现在目录仅证明 Listed；Running 仅来自公开 state，不来自配置或 features。</summary>
public sealed record OpenCodePluginStatusEntry(
    OpenCodePluginInfo Plugin, bool Listed, bool? Running, OpenCodeCapabilityState Runtime);

public sealed record OpenCodeCapabilitySnapshot(
    OpenCodeCapabilityScope Scope,
    OpenCodeCapabilityState Mcp,
    OpenCodeCapabilityState Plugins,
    OpenCodeCapabilityState Lsp,
    OpenCodeCapabilityState Todo,
    OpenCodeCapabilityState Omo,
    OpenCodeCapabilityState Billion,
    ImmutableArray<OpenCodeMcpStatusEntry> McpServers,
    ImmutableArray<OpenCodePluginStatusEntry> PluginEntries,
    bool Complete);

/// <summary>纯状态归并；没有连接、配置读取、轮询、插件探测或 Billion 请求。</summary>
public static class OpenCodeCapabilityStatus
{
    public const string LspUnavailableReason = "OpenCode V2 未公开LSP状态接口";

    /// <summary>
    /// OpenAPI <c>Plugin.Source</c> 是按 <c>type</c> 区分的联合：builtin / package / local / sdk。
    /// 只有 <c>type == "builtin"</c> 才是 OpenCode 自带插件（服务端显式声明，不靠名称或路径猜测）。
    /// </summary>
    public const string BuiltInSourceType = "builtin";

    /// <summary>服务端声明为 OpenCode 内置的插件（连接详情中隐藏，仅计数）。</summary>
    public static bool IsBuiltIn(OpenCodePluginInfo plugin)
        => string.Equals(plugin?.Source?.Type, BuiltInSourceType, StringComparison.Ordinal);

    public static OpenCodeCapabilitySnapshot Create(
        OpenCodeCapabilityScope scope,
        OpenCodeCapabilityRead<OpenCodeMcpServer> mcp,
        OpenCodeCapabilityRead<OpenCodePluginInfo> plugins)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(plugins);
        var mcpRows = (mcp.Items.IsDefault ? ImmutableArray<OpenCodeMcpServer>.Empty : mcp.Items)
            .Select(server => new OpenCodeMcpStatusEntry(server,
                scope.Stale || !mcp.Complete ? Unknown("MCP 状态证据过期或不完整") : McpConnection(server)))
            .ToImmutableArray();
        var pluginRows = (plugins.Items.IsDefault ? ImmutableArray<OpenCodePluginInfo>.Empty : plugins.Items)
            .Select(plugin => new OpenCodePluginStatusEntry(plugin, true,
                scope.Stale || !plugins.Complete ? null : plugin.State?.Status switch
                {
                    "active" => true,
                    "failed" => false,
                    _ => (bool?)null,
                }, scope.Stale || !plugins.Complete ? Unknown("插件状态证据过期或不完整") : PluginRuntime(plugin)))
            .ToImmutableArray();
        return new(scope,
            ReadState(scope, mcp, "MCP 状态目录可用；不表示所有 MCP 已连接"),
            ReadState(scope, plugins, "服务器插件目录可用；配置存在不等于实际运行"),
            Unavailable(LspUnavailableReason),
            PluginFeature(scope, plugins, "opencode-todolist"),
            PluginFeature(scope, plugins, "oh-my-opencode-slim"),
            Unavailable("Billion 未授权连接；压缩控制禁用"),
            mcpRows, pluginRows, !scope.Stale && mcp.Complete && plugins.Complete
                && !mcp.Items.IsDefault && !plugins.Items.IsDefault);
    }

    private static OpenCodeCapabilityState ReadState<T>(
        OpenCodeCapabilityScope scope, OpenCodeCapabilityRead<T> read, string success)
        => scope.Stale ? Unknown("状态证据已过期，无法确认")
            : !read.Complete || read.Items.IsDefault ? Unknown(read.UnknownReason ?? "状态证据不完整，无法确认")
            : Available(success);

    private static OpenCodeCapabilityState McpConnection(OpenCodeMcpServer server)
        => server.Status?.Status switch
        {
            "connected" => Available("MCP 已连接"),
            "pending" => Unknown("MCP 正在连接，尚未确认可用"),
            "disabled" => Unavailable("MCP 已禁用"),
            "failed" => Unavailable("MCP 连接失败"),
            "needs_auth" => Unavailable("MCP 需要授权"),
            _ => Unknown("无法识别 MCP 连接状态"),
        };

    private static OpenCodeCapabilityState PluginRuntime(OpenCodePluginInfo plugin)
        => plugin.State?.Status switch
        {
            "active" => Available("服务器报告插件 active"),
            "failed" => Unavailable("服务器报告插件加载失败"),
            _ => Unknown("无法识别插件运行状态"),
        };

    private static OpenCodeCapabilityState PluginFeature(
        OpenCodeCapabilityScope scope, OpenCodeCapabilityRead<OpenCodePluginInfo> read, string package)
    {
        var directory = ReadState(scope, read, "");
        if (directory.Availability != OpenCodeCapabilityAvailability.Available) return directory;

        var matches = read.Items.Where(plugin => Identifies(plugin, package)).ToArray();
        if (matches.Length > 1) return Unknown("插件目录存在重复身份，无法确认运行状态");
        if (matches.Length == 1)
        {
            var runtime = PluginRuntime(matches[0]);
            if (runtime.Availability != OpenCodeCapabilityAvailability.Available) return runtime;
            return matches[0].Features?.Server == true
                ? Available("/api/plugin 已确认服务器插件 active；仅使用 core 公开数据")
                : Unavailable("插件未声明服务器功能；TUI 或 RPC 声明不代表此功能可用");
        }

        // 无身份的本地/SDK 插件可能就是目标插件；不能从路径、配置 pin 或工具名称猜测。
        // 内置插件（source.type=builtin）由 OpenCode 自带，不可能是第三方目标包，不因缺少 id 而变为无法确认。
        if (read.Items.Any(plugin => string.IsNullOrWhiteSpace(plugin.Id) && plugin.Source?.Type != "package" && !IsBuiltIn(plugin)))
            return Unknown("/api/plugin 含无法识别身份的插件，无法确认目标插件");
        return Unavailable("/api/plugin 未列出目标插件，无法确认当前启用（不代表未安装）");
    }

    private static bool Identifies(OpenCodePluginInfo plugin, string package)
        => string.Equals(plugin.Id, package, StringComparison.Ordinal)
            || (plugin.Source?.Type == "package" && plugin.Source.Target is { } target
                && (target == package || target.StartsWith(package + "@", StringComparison.Ordinal)
                    || target == package + "/server" || target.StartsWith(package + "/server@", StringComparison.Ordinal)));

    private static OpenCodeCapabilityState Available(string reason) => new(OpenCodeCapabilityAvailability.Available, reason);
    private static OpenCodeCapabilityState Unavailable(string reason) => new(OpenCodeCapabilityAvailability.Unavailable, reason);
    private static OpenCodeCapabilityState Unknown(string reason) => new(OpenCodeCapabilityAvailability.Unknown, reason);
}
