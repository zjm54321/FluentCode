using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages;

/// <summary>
/// 页头连接状态按钮的详情 Flyout（重设计 Wave 3 F10b）：服务状态（地址 / 版本 / 最近请求结果）、MCP、插件（隐藏内置）、LSP。
/// </summary>
/// <remarks>
/// <para>
/// 读取时机：安装来源时为常驻页脚读取服务版本；Flyout 打开时补读未加载/失败的版本（或点“重新读取”）。服务版本 = 一次 GET api/info，按来源引用 + Revision +
/// 来源代际 + 页面客户端归属，换源即作废；MCP / 插件复用会话概况控制器的同一次显式读取（打开时若当前会话尚无结果才读取），
/// 因为插件/MCP 目录按会话目录查询。没有计时器、没有轮询；Flyout 关闭后不再更新内容。
/// </para>
/// <para>
/// 每次 await 之后复核来源归属（引用 / Revision / 代际 / 客户端）与请求序号，迟到或过期结果直接丢弃；起发经来源 TryExecute。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    private sealed record ServerInfoOwner(OpenCodePageSource Source, long Revision, int SourceGeneration, OpenCodeClient Client);

    private ServerInfoOwner? _serverInfoOwner;
    private OpenCodeServerInfoView _serverInfo = OpenCodeServerInfoView.NotLoaded;
    private long _serverInfoRequest;
    private string _connectionStatusDetail = string.Empty;

    // wave5 H6：Flyout 内当前打开的详情页键（null = 列表页）；关闭 Flyout 时回到列表。
    private string? _connectionDetailKey;
    // MCP 连接控制：正在进行的服务器（单飞，按名称）与最近一次失败说明（只在对应详情页显示）。
    private readonly HashSet<string> _mcpBusy = new(StringComparer.Ordinal);
    private (string Server, string Message)? _mcpActionError;

    private void ConnectionFlyout_Opening(object? sender, object e)
    {
        if (!_pageActive)
        {
            return;
        }

        RenderConnectionFlyout();
        if (CurrentServerInfo().State is OpenCodeInsightState.NotLoaded or OpenCodeInsightState.Failed)
        {
            _ = ReadServerInfoAsync();
        }

        // MCP / 插件：当前会话尚无概况结果时读取一次（与打开概况面板相同的显式读取）。
        if (_insights is { } insights && TryCaptureInsightsBinding(out var binding) && !insights.HasResultFor(binding!))
        {
            _ = LoadSessionInsightsAsync();
        }
    }

    private void ConnectionFlyout_Closed(object? sender, object e)
    {
        // 关闭后释放内容；下次打开按最新状态重建（回到列表页）。
        ConnectionFlyout.Content = null;
        _connectionDetailKey = null;
        _mcpActionError = null;
    }

    private void RefreshConnectionDetails()
    {
        _ = ReadServerInfoAsync();
        _ = LoadSessionInsightsAsync();
        RenderConnectionFlyout();
    }

    /// <summary>会话概况快照或连接状态变化时调用：仅当 Flyout 正在显示才重建内容。</summary>
    private void RenderConnectionFlyoutIfOpen()
    {
        RefreshRightPanelFooter();
        if (ConnectionFlyout is { IsOpen: true })
        {
            RenderConnectionFlyout();
        }
    }

    partial void OnSessionInsightsRendered() => RenderConnectionFlyoutIfOpen();

    private void RenderConnectionFlyout()
    {
        var text = ConnectionStatusText.Text;
        string? baseUrl = null;
        try
        {
            baseUrl = SourceReady ? _client?.DisplayBaseUrl : null;
        }
        catch (ObjectDisposedException)
        {
            baseUrl = null;
        }

        OpenCodeInsightSection<OpenCodeCapabilitySnapshot> capabilities = OpenCodeInsightSection<OpenCodeCapabilitySnapshot>.NotLoaded;
        string? unavailable = null;
        if (!SourceReady)
        {
            unavailable = "OpenCode 连接尚未就绪，未查询 MCP / 插件状态。";
        }
        else if (_insights is not { } insights || !TryCaptureInsightsBinding(out var binding))
        {
            unavailable = "选择一个会话后显示 MCP / 插件状态（这些目录按会话所在目录查询）。";
        }
        else if (string.Equals(insights.Snapshot.SessionId, binding!.SessionId, StringComparison.Ordinal))
        {
            capabilities = insights.Snapshot.Capabilities;
        }

        var details = new OpenCodeConnectionDetails(text, _connectionStatusDetail, baseUrl, CurrentServerInfo(), capabilities, unavailable)
        {
            ServerDotKey = ConnectionDotKey(text),
            McpBusy = _mcpBusy.ToArray(),
            McpActionError = _mcpActionError,
        };
        ConnectionFlyout.Content = OpenCodeConnectionFlyout.Build(details, RefreshConnectionDetails, _connectionDetailKey,
            SelectConnectionDetail, (server, request) => _ = ControlMcpAsync(server, request));
    }

    /// <summary>与页头连接圆点相同的语义色（按页头已显示的状态文字映射，不另行推断）。</summary>
    private static string ConnectionDotKey(string statusText) => statusText switch
    {
        "已连接" => OpenCodeInsightsTheme.Success,
        "连接失败" => OpenCodeInsightsTheme.Critical,
        _ => OpenCodeInsightsTheme.Neutral,
    };

    private void SelectConnectionDetail(string? key)
    {
        _connectionDetailKey = key;
        _mcpActionError = null;
        RenderConnectionFlyoutIfOpen();
    }

    /// <summary>
    /// MCP 运行时连接 / 断开 / 重新连接（OpenCode v2 实验接口，HTTP 204）。只在用户点击详情页动作时执行；同一服务器单飞；
    /// 起发经来源 TryExecute 并绑定概况绑定（来源 / 代际 / 会话），每次 await 后复核；完成后显式重新读取一次 MCP / 插件状态。
    /// 失败只在该服务器详情页显示简短原因，不弹出原始错误体；不自动重试。
    /// </summary>
    private async Task ControlMcpAsync(string server, OpenCodeMcpRequest request)
    {
        if (!_pageActive || _mcpBusy.Contains(server) || _insights is not { } insights || !TryCaptureInsightsBinding(out var captured))
        {
            return;
        }

        var binding = captured!;
        var directory = insights.Snapshot.Capabilities.Value?.Scope.Directory;
        var steps = request switch
        {
            OpenCodeMcpRequest.Connect => new[] { OpenCodeMcpControlAction.Connect },
            OpenCodeMcpRequest.Disconnect => new[] { OpenCodeMcpControlAction.Disconnect },
            _ => new[] { OpenCodeMcpControlAction.Disconnect, OpenCodeMcpControlAction.Connect },
        };

        _mcpBusy.Add(server);
        _mcpActionError = null;
        RenderConnectionFlyoutIfOpen();
        string? failure = null;
        try
        {
            using var operation = CreateOperationToken(binding.Source);
            var ct = operation.Token;
            foreach (var step in steps)
            {
                if (ct.IsCancellationRequested || !IsCurrentInsightsBinding(binding) || binding.Source.IsRevoked)
                {
                    return;
                }

                Task? pending = null;
                if (!binding.Source.TryExecute(ct, () => pending = binding.Client.ControlMcpServerAsync(server, step, directory, ct)) || pending is null)
                {
                    failure = "未能发起请求（连接已变化）。";
                    break;
                }

                await pending;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (OpenCodeApiException api)
        {
            failure = api.StatusCode == 404 ? "服务端没有找到该 MCP 服务器，或当前 OpenCode 不支持此操作（HTTP 404）。"
                : "操作未完成（HTTP " + api.StatusCode + "）。";
        }
        catch (OpenCodeTimeoutException)
        {
            failure = "操作超时；没有自动重试。";
        }
        catch (Exception)
        {
            failure = "操作未完成。";
        }
        finally
        {
            _mcpBusy.Remove(server);
        }

        if (!_pageActive || !IsCurrentInsightsBinding(binding))
        {
            RenderConnectionFlyoutIfOpen();
            return;
        }

        _mcpActionError = failure is null ? null : (server, failure);
        RenderConnectionFlyoutIfOpen();
        // 显式刷新一次 MCP / 插件状态（与“重新读取”同一路径）。
        _ = LoadSessionInsightsAsync();
    }

    private OpenCodeServerInfoView CurrentServerInfo()
        => _serverInfoOwner is { } owner && IsCurrentServerInfoOwner(owner) ? _serverInfo : OpenCodeServerInfoView.NotLoaded;

    private bool IsCurrentServerInfoOwner(ServerInfoOwner owner)
        => _pageActive && !owner.Source.IsRevoked && ReferenceEquals(owner.Source, _source) && owner.Revision == _sourceRevision
        && owner.SourceGeneration == _sourceGeneration && ReferenceEquals(owner.Client, _client);

    private async Task ReadServerInfoAsync()
    {
        if (!_pageActive || !SourceReady || _source is not { } source || _client is not { } client)
        {
            return;
        }

        var owner = new ServerInfoOwner(source, _sourceRevision, _sourceGeneration, client);
        var request = ++_serverInfoRequest;
        _serverInfoOwner = owner;
        _serverInfo = OpenCodeServerInfoView.Loading;
        RenderConnectionFlyoutIfOpen();

        // 不链接会话令牌：版本属于来源，切换会话不应取消；来源撤销 / 页面卸载会取消。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(source.RevocationToken, _pageCts?.Token ?? CancellationToken.None);
        var ct = cts.Token;
        bool Current() => request == _serverInfoRequest && IsCurrentServerInfoOwner(owner) && !ct.IsCancellationRequested;

        Task<OpenCodeServerInfo>? pending = null;
        if (!source.TryExecute(ct, () =>
            {
                if (!Current()) return;
                pending = client.GetServerInfoAsync(ct);
            }) || pending is null)
        {
            if (Current())
            {
                _serverInfo = new(OpenCodeInsightState.Failed, null, "未能发起读取（连接已变化）。");
            }

            RenderConnectionFlyoutIfOpen();
            return;
        }

        OpenCodeServerInfoView result;
        try
        {
            var info = await pending;
            result = new(OpenCodeInsightState.Loaded, info.Version, "采样时刻的服务版本。");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (OpenCodeApiException api)
        {
            result = new(OpenCodeInsightState.Failed, null, $"服务未提供版本信息（HTTP {api.StatusCode}）；不影响其他功能。");
        }
        catch (OpenCodeTimeoutException)
        {
            result = new(OpenCodeInsightState.Failed, null, "读取服务版本超时；没有自动重试。");
        }
        catch (OpenCodeException)
        {
            result = new(OpenCodeInsightState.Failed, null, "无法确认服务版本。");
        }
        catch (Exception)
        {
            result = new(OpenCodeInsightState.Failed, null, "无法确认服务版本。");
        }

        if (!Current())
        {
            return; // 迟到 / 换源：丢弃。
        }

        _serverInfo = result;
        RenderConnectionFlyoutIfOpen();
    }
}
