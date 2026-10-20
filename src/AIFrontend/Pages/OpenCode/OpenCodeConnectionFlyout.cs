using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>GET api/info 的显示状态（页面按来源代际持有；换源即作废）。</summary>
internal sealed record OpenCodeServerInfoView(OpenCodeInsightState State, string? Version, string Note)
{
    internal static OpenCodeServerInfoView NotLoaded { get; } = new(OpenCodeInsightState.NotLoaded, null, "打开此面板时读取一次。");
    internal static OpenCodeServerInfoView Loading { get; } = new(OpenCodeInsightState.Loading, null, "正在读取…");
}

/// <summary>连接详情 Flyout 的全部输入（页面在打开 / 数据变化时捕获；本类不发请求）。</summary>
internal sealed record OpenCodeConnectionDetails(
    string StatusText,
    string StatusDetail,
    string? BaseUrl,
    OpenCodeServerInfoView ServerInfo,
    OpenCodeInsightSection<OpenCodeCapabilitySnapshot> Capabilities,
    string? CapabilitiesUnavailableReason)
{
    /// <summary>服务行状态点的画刷键（与页头连接圆点相同的语义色）。</summary>
    internal string ServerDotKey { get; init; } = OpenCodeInsightsTheme.Neutral;

    /// <summary>正在执行连接/断开请求的 MCP 名称（行与详情显示进度、动作禁用）。</summary>
    internal IReadOnlyCollection<string> McpBusy { get; init; } = Array.Empty<string>();

    /// <summary>最近一次 MCP 连接控制失败的说明（只在对应详情页显示；真实错误，不是提示性说明）。</summary>
    internal (string Server, string Message)? McpActionError { get; init; }
}

/// <summary>MCP 详情页里的动作（“重新连接” = 先断开再连接）。</summary>
internal enum OpenCodeMcpRequest { Connect, Disconnect, Reconnect }

/// <summary>
/// 页头“已连接”状态按钮的原生 Flyout 内容（F10b，wave5 H6 精简）：列表页按“服务 / MCP / 插件 / LSP”分组，
/// 每行只有“名称 + 右侧状态指示”（绿点 = 正常，红点 = 失败，黄点 = 需处理/未知，灰点 = 已禁用/不可用，进度环 = 连接中）；
/// 行是原生按钮，点开进入同一 Flyout 内的详情页（返回按钮回到列表）。纯渲染：每次打开或数据变化时整体重建，
/// 当前打开的详情页由页面持有的键（<c>selected</c>）在重建之间保持。
/// </summary>
/// <remarks>
/// 插件只显示非内置项：OpenAPI <c>Plugin.Source</c> 是带 <c>type</c> 判别的联合（builtin / package / local / sdk），
/// <see cref="OpenCodeCapabilityStatus.IsBuiltIn"/> 只按服务端声明的 <c>type == "builtin"</c> 静默隐藏，不按名称或路径猜测。
/// 插件详情显示所有已知字段：运行状态、来源类型、版本 / 有更新 / 更新中、包目标或本地路径、声明的功能与服务端错误文本。
/// MCP 详情显示连接状态与服务端错误文本，并提供连接 / 断开 / 重新连接（OpenCode v2 实验接口
/// <c>POST api/experimental/mcp/{server}/connect|disconnect</c>；动作经回调交给页面执行）。
/// 列表页不放说明性文字（“状态未观测”“已隐藏内置插件”之类）；真实的读取失败原因仍如实显示。
/// MCP / 插件数据复用会话概况的同一次显式读取（需要选中会话以确认其目录），这里不另建轮询。
/// </remarks>
internal static class OpenCodeConnectionFlyout
{
    internal const string ServerKey = "server";
    internal const string LspKey = "lsp";

    internal static string McpKey(string name) => "mcp:" + name;

    internal static string PluginKey(int index, OpenCodePluginInfo plugin) => "plugin:" + index + ":" + PluginName(plugin);

    internal static FrameworkElement Build(OpenCodeConnectionDetails details, Action? refresh,
        string? selected = null, Action<string?>? select = null, Action<string, OpenCodeMcpRequest>? mcp = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        var root = new StackPanel { Spacing = 16, Width = 320 };
        AutomationProperties.SetAutomationId(root, "OpenCodeConnectionDetails");
        AutomationProperties.SetName(root, "连接详情");

        if (selected is not null && select is not null && BuildDetail(details, selected, select, mcp) is { } detail)
        {
            root.Children.Add(detail);
        }
        else
        {
            BuildList(root, details, refresh, select);
        }

        return new ScrollViewer
        {
            Content = root,
            MaxHeight = 600,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    // ------------------------------------------------------------------
    // 列表页
    // ------------------------------------------------------------------

    private static void BuildList(StackPanel root, OpenCodeConnectionDetails details, Action? refresh, Action<string?>? select)
    {
        var (server, serverRows) = OpenCodeSessionInsightsView.Section("服务", details.ServerInfo.State, "OpenCodeConnectionServer");
        var version = details.ServerInfo.Version is { Length: > 0 } v ? v : null;
        serverRows.Children.Add(Row("OpenCodeConnectionServer.Row", "OpenCode", version,
            DotIndicator(details.ServerDotKey, details.StatusText), ServerKey, select,
            "OpenCode 服务：" + details.StatusText + (version is not null ? "，版本 " + version : "")));
        root.Children.Add(server);

        if (details.CapabilitiesUnavailableReason is { } reason)
        {
            var (panel, content) = OpenCodeSessionInsightsView.Section("MCP / 插件 / LSP", OpenCodeInsightState.NotLoaded, "OpenCodeConnectionMcp");
            content.Children.Add(OpenCodeSessionInsightsView.Caption(reason));
            root.Children.Add(panel);
        }
        else
        {
            root.Children.Add(McpGroup(details, select));
            root.Children.Add(PluginGroup(details.Capabilities, select));
            root.Children.Add(LspGroup(details.Capabilities, select));
        }

        if (refresh is not null)
        {
            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE72C", FontSize = 12 },
                        OpenCodeInteractionUi.StyledText("重新读取", OpenCodeInteractionUi.BodyStyleKey),
                    },
                },
                HorizontalAlignment = HorizontalAlignment.Right,
                IsEnabled = details.ServerInfo.State != OpenCodeInsightState.Loading
                    && details.Capabilities.State != OpenCodeInsightState.Loading,
            };
            AutomationProperties.SetAutomationId(button, "OpenCodeConnectionRefresh");
            AutomationProperties.SetName(button, "重新读取连接详情");
            ToolTipService.SetToolTip(button, "重新读取一次服务版本与 MCP / 插件状态");
            button.Click += (_, _) => refresh();
            root.Children.Add(button);
        }
    }

    private static UIElement McpGroup(OpenCodeConnectionDetails details, Action<string?>? select)
    {
        var section = details.Capabilities;
        var (panel, content) = OpenCodeSessionInsightsView.Section("MCP", section.State, "OpenCodeConnectionMcp");
        if (section.Value is not { } capabilities)
        {
            if (section.State is OpenCodeInsightState.Failed or OpenCodeInsightState.Unavailable)
            {
                content.Children.Add(OpenCodeSessionInsightsView.Caption(section.Note));
            }

            return panel;
        }

        if (capabilities.Mcp.Availability != OpenCodeCapabilityAvailability.Available)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption(capabilities.Mcp.Reason, 0.85));
        }
        else if (capabilities.McpServers.IsEmpty)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption("无", 0.85));
        }

        foreach (var entry in capabilities.McpServers)
        {
            var name = McpName(entry);
            var status = entry.Server.Status?.Status;
            content.Children.Add(Row("OpenCodeConnectionMcp." + name, name, null,
                McpIndicator(status, details.McpBusy.Contains(name)), McpKey(name), select,
                name + "：" + DescribeMcpStatus(status)));
        }

        return panel;
    }

    private static UIElement PluginGroup(OpenCodeInsightSection<OpenCodeCapabilitySnapshot> section, Action<string?>? select)
    {
        var (panel, content) = OpenCodeSessionInsightsView.Section("插件", section.State, "OpenCodeConnectionPlugins");
        if (section.Value is not { } capabilities)
        {
            if (section.State is OpenCodeInsightState.Failed or OpenCodeInsightState.Unavailable)
            {
                content.Children.Add(OpenCodeSessionInsightsView.Caption(section.Note));
            }

            return panel;
        }

        if (capabilities.Plugins.Availability != OpenCodeCapabilityAvailability.Available)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption(capabilities.Plugins.Reason, 0.85));
        }

        var visible = VisiblePlugins(capabilities);
        if (capabilities.Plugins.Availability == OpenCodeCapabilityAvailability.Available && visible.Count == 0)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption("无", 0.85));
        }

        // 内置插件静默隐藏（wave5 H6：不再显示“已隐藏 N 个”说明）。
        for (var index = 0; index < visible.Count; index++)
        {
            var entry = visible[index];
            var name = PluginName(entry.Plugin);
            var version = entry.Plugin.Source?.Version is { Length: > 0 } v ? v : null;
            content.Children.Add(Row("OpenCodeConnectionPlugin." + index, name, version,
                DotIndicator(DotKey(entry.Runtime.Availability), DescribePluginStatus(entry.Plugin.State?.Status)),
                PluginKey(index, entry.Plugin), select, name + "：" + DescribePluginStatus(entry.Plugin.State?.Status)));
        }

        return panel;
    }

    private static UIElement LspGroup(OpenCodeInsightSection<OpenCodeCapabilitySnapshot> section, Action<string?>? select)
    {
        var (panel, content) = OpenCodeSessionInsightsView.Section("LSP", OpenCodeInsightState.Loaded, "OpenCodeConnectionLsp");
        content.Children.Add(Row("OpenCodeConnectionLsp.Row", "LSP", null,
            DotIndicator(OpenCodeInsightsTheme.Neutral, "无法确认"), LspKey, select, "LSP：服务端未公开状态"));
        return panel;
    }

    /// <summary>一行：名称（+ 可选次要文字）左，状态指示右；原生透明按钮，点开详情。</summary>
    private static Button Row(string automationId, string name, string? secondary, FrameworkElement indicator,
        string key, Action<string?>? select, string automationName)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = OpenCodeInteractionUi.StyledText(name, OpenCodeInteractionUi.BodyStyleKey);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(title);
        if (secondary is not null)
        {
            var caption = OpenCodeInteractionUi.StyledText(secondary, OpenCodeInteractionUi.CaptionSubtleStyleKey);
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(caption, 1);
            grid.Children.Add(caption);
        }

        indicator.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(indicator, 2);
        grid.Children.Add(indicator);
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            IsEnabled = select is not null,
        };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } style)
        {
            button.Style = style;
        }

        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => select?.Invoke(key);
        return button;
    }

    private static FrameworkElement DotIndicator(string brushKey, string description)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8 };
        if (OpenCodeInsightsTheme.Dot(brushKey) is { } dotStyle)
        {
            dot.Style = dotStyle;
        }

        AutomationProperties.SetName(dot, description);
        return dot;
    }

    /// <summary>MCP 状态指示：连接中 / 请求进行中 = 原生 ProgressRing；其余为语义色圆点。</summary>
    internal static FrameworkElement McpIndicator(string? status, bool busy)
    {
        if (busy || status == "pending")
        {
            var ring = new ProgressRing { Width = 12, Height = 12, IsActive = true };
            AutomationProperties.SetName(ring, "正在连接");
            return ring;
        }

        return DotIndicator(McpDotKey(status), DescribeMcpStatus(status));
    }

    internal static string McpDotKey(string? status) => status switch
    {
        "connected" => OpenCodeInsightsTheme.Success,
        "failed" => OpenCodeInsightsTheme.Critical,
        "disabled" => OpenCodeInsightsTheme.Neutral,
        _ => OpenCodeInsightsTheme.Caution, // needs_auth / 未知 / 未识别：需要注意
    };

    private static string McpName(OpenCodeMcpStatusEntry entry) => entry.Server.Name is { Length: > 0 } n ? n : "未命名 MCP";

    // ------------------------------------------------------------------
    // 详情页
    // ------------------------------------------------------------------

    private static UIElement? BuildDetail(OpenCodeConnectionDetails details, string key, Action<string?> select,
        Action<string, OpenCodeMcpRequest>? mcp)
    {
        if (key == ServerKey)
        {
            var rows = new List<(string, string)>
            {
                ("连接", details.StatusText),
                ("服务地址", details.BaseUrl ?? "未配置"),
                ("OpenCode 版本", details.ServerInfo.Version ?? DescribeUnknown(details.ServerInfo.State)),
            };
            var page = DetailPage("OpenCodeConnectionServerDetail", "OpenCode", DotIndicator(details.ServerDotKey, details.StatusText), select);
            page.Children.Add(OpenCodeSessionInsightsView.Table(rows,
                label => (label == "服务地址" && details.BaseUrl is not null) || (label == "OpenCode 版本" && details.ServerInfo.Version is not null)));
            page.Children.Add(OpenCodeSessionInsightsView.Caption(details.StatusDetail, 0.75));
            if (details.ServerInfo.State is OpenCodeInsightState.Failed)
            {
                page.Children.Add(OpenCodeSessionInsightsView.Caption(details.ServerInfo.Note, 0.75));
            }

            return page;
        }

        if (key == LspKey)
        {
            var state = details.Capabilities.Value?.Lsp ?? new OpenCodeCapabilityState(OpenCodeCapabilityAvailability.Unavailable,
                OpenCodeCapabilityStatus.LspUnavailableReason);
            var page = DetailPage("OpenCodeConnectionLspDetail", "LSP", DotIndicator(OpenCodeInsightsTheme.Neutral, "无法确认"), select);
            page.Children.Add(OpenCodeSessionInsightsView.Caption(state.Reason + "；这里不根据连接或配置推测 LSP 是否运行。", 0.85));
            return page;
        }

        if (details.CapabilitiesUnavailableReason is not null || details.Capabilities.Value is not { } capabilities)
        {
            return null;
        }

        if (key.StartsWith("mcp:", StringComparison.Ordinal))
        {
            var name = key["mcp:".Length..];
            if (capabilities.McpServers.FirstOrDefault(entry => McpName(entry) == name) is not { } entry)
            {
                return null;
            }

            var status = entry.Server.Status?.Status;
            var busy = details.McpBusy.Contains(name);
            var page = DetailPage("OpenCodeConnectionMcpDetail", name, McpIndicator(status, busy), select);
            var rows = new List<(string, string)> { ("状态", DescribeMcpStatus(status)) };
            if (entry.Server.Status?.Error is { Length: > 0 } error)
            {
                rows.Add(("错误", error));
            }

            page.Children.Add(OpenCodeSessionInsightsView.Table(rows, IsMonospaceRow));
            if (mcp is not null)
            {
                page.Children.Add(McpActions(name, status, busy, mcp));
            }

            if (details.McpActionError is { } failure && failure.Server == name)
            {
                var note = OpenCodeSessionInsightsView.Caption(failure.Message, 0.85);
                AutomationProperties.SetAutomationId(note, "OpenCodeConnectionMcpActionError");
                page.Children.Add(note);
            }

            return page;
        }

        if (key.StartsWith("plugin:", StringComparison.Ordinal))
        {
            var visible = VisiblePlugins(capabilities);
            for (var index = 0; index < visible.Count; index++)
            {
                var entry = visible[index];
                if (PluginKey(index, entry.Plugin) != key)
                {
                    continue;
                }

                var plugin = entry.Plugin;
                var name = PluginName(plugin);
                var page = DetailPage("OpenCodeConnectionPluginDetail", name,
                    DotIndicator(DotKey(entry.Runtime.Availability), DescribePluginStatus(plugin.State?.Status)), select);
                var rows = new List<(string, string)>
                {
                    ("运行状态", DescribePluginStatus(plugin.State?.Status)),
                    ("来源", DescribeSourceType(plugin.Source?.Type)),
                };
                if (plugin.Source?.Target is { Length: > 0 } target) rows.Add(("包", target));
                if (plugin.Source?.Version is { Length: > 0 } version) rows.Add(("版本", version));
                if (plugin.Source?.Outdated == true) rows.Add(("更新", plugin.Source.Updating == true ? "正在更新" : "有新版本"));
                else if (plugin.Source?.Updating == true) rows.Add(("更新", "正在更新"));
                if (plugin.Source?.Path is { Length: > 0 } path) rows.Add(("路径", path));
                rows.Add(("功能", DescribeFeatures(plugin.Features)));
                if (plugin.State?.Error is { Length: > 0 } error) rows.Add(("错误", error));
                page.Children.Add(OpenCodeSessionInsightsView.Table(rows, IsMonospaceRow));
                if (entry.Runtime.Availability != OpenCodeCapabilityAvailability.Available)
                {
                    page.Children.Add(OpenCodeSessionInsightsView.Caption(entry.Runtime.Reason, 0.75));
                }

                return page;
            }
        }

        return null;
    }

    private static StackPanel DetailPage(string automationId, string title, FrameworkElement indicator, Action<string?> select)
    {
        var page = new StackPanel { Spacing = 12 };
        AutomationProperties.SetAutomationId(page, automationId);
        AutomationProperties.SetName(page, title);
        var back = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE72B", FontSize = 12 },
                    OpenCodeInteractionUi.StyledText("连接", OpenCodeInteractionUi.BodyStyleKey),
                },
            },
        };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } style)
        {
            back.Style = style;
        }

        AutomationProperties.SetAutomationId(back, "OpenCodeConnectionDetailBack");
        AutomationProperties.SetName(back, "返回连接列表");
        back.Click += (_, _) => select(null);
        page.Children.Add(back);

        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = OpenCodeInteractionUi.StyledText(title, OpenCodeInteractionUi.SubtitleStyleKey, selectable: true);
        heading.Children.Add(text);
        indicator.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(indicator, 1);
        heading.Children.Add(indicator);
        page.Children.Add(heading);
        return page;
    }

    private static UIElement McpActions(string name, string? status, bool busy, Action<string, OpenCodeMcpRequest> mcp)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AutomationProperties.SetAutomationId(actions, "OpenCodeConnectionMcpActions");
        if (status == "connected")
        {
            actions.Children.Add(ActionButton("OpenCodeConnectionMcpReconnect", "\uE72C", "重新连接", !busy, accent: true,
                () => mcp(name, OpenCodeMcpRequest.Reconnect)));
            actions.Children.Add(ActionButton("OpenCodeConnectionMcpDisconnect", "\uE8CD", "断开连接", !busy, accent: false,
                () => mcp(name, OpenCodeMcpRequest.Disconnect)));
        }
        else
        {
            actions.Children.Add(ActionButton("OpenCodeConnectionMcpConnect", "\uE703", "连接", !busy && status != "pending", accent: true,
                () => mcp(name, OpenCodeMcpRequest.Connect)));
        }

        return actions;
    }

    private static Button ActionButton(string automationId, string glyph, string text, bool enabled, bool accent, Action click)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 12 },
                    OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.BodyStyleKey),
                },
            },
            IsEnabled = enabled,
        };
        if (accent && OpenCodeInteractionUi.TryStyle("AccentButtonStyle") is { } style)
        {
            button.Style = style;
        }

        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    private static string DescribeUnknown(OpenCodeInsightState state) => state switch
    {
        OpenCodeInsightState.Loading => "读取中…",
        OpenCodeInsightState.NotLoaded => "尚未读取",
        _ => "无法确认",
    };

    /// <summary>非内置插件（保持服务端顺序）。</summary>
    internal static IReadOnlyList<OpenCodePluginStatusEntry> VisiblePlugins(OpenCodeCapabilitySnapshot capabilities)
        => capabilities.PluginEntries.Where(entry => !OpenCodeCapabilityStatus.IsBuiltIn(entry.Plugin)).ToList();

    internal static string PluginName(OpenCodePluginInfo plugin)
        => plugin.Id is { Length: > 0 } id ? id
            : plugin.Source?.Type == "package" && plugin.Source.Target is { Length: > 0 } target ? target
            : plugin.Source?.Type == "local" && plugin.Source.Path is { Length: > 0 } path ? LastSegment(path)
            : plugin.Source?.Type == "sdk" ? "SDK 插件（无公开名称）"
            : "本地插件（无公开名称）";

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var last = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return last.Length > 0 ? last : path;
    }

    /// <summary>版本 / 包目标 / 路径用等宽样式（ThemeResource，跟随用户等宽字体设置）。</summary>
    internal static bool IsMonospaceRow(string label) => label is "包" or "版本" or "路径";

    internal static string DotKey(OpenCodeCapabilityAvailability availability) => availability switch
    {
        OpenCodeCapabilityAvailability.Available => OpenCodeInsightsTheme.Success,
        OpenCodeCapabilityAvailability.Unavailable => OpenCodeInsightsTheme.Critical,
        _ => OpenCodeInsightsTheme.Caution,
    };

    internal static string DescribeMcpStatus(string? status) => status switch
    {
        "connected" => "已连接",
        "pending" => "正在连接",
        "disabled" => "已禁用",
        "failed" => "连接失败",
        "needs_auth" => "需要授权",
        null or "" => "未知",
        _ => "未识别（" + status + "）",
    };

    internal static string DescribePluginStatus(string? status) => status switch
    {
        "active" => "运行中（active）",
        "failed" => "加载失败",
        null or "" => "未知",
        _ => "未识别（" + status + "）",
    };

    internal static string DescribeSourceType(string? type) => type switch
    {
        "package" => "npm 包",
        "local" => "本地文件",
        "sdk" => "SDK 注册",
        "builtin" => "OpenCode 内置",
        null or "" => "未知",
        _ => "未识别（" + type + "）",
    };

    internal static string DescribeFeatures(OpenCodePluginFeatures? features)
    {
        var list = new List<string>();
        if (features?.Server == true) list.Add("服务器");
        if (features?.Tui == true) list.Add("TUI");
        if (features?.Rpc == true) list.Add("RPC");
        return list.Count == 0 ? "未声明" : string.Join(" / ", list);
    }
}
