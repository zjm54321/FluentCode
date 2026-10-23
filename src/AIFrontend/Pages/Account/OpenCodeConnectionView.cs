using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.OpenCode.Connection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Account;

/// <summary>账户入口中的唯一连接表单。秘密仅留在密码控件/内存中；离开即清空，不自动兑换或重试。</summary>
/// <remarks>
/// 视觉层级（仅原生控件 + 主题画刷）：当前连接卡片 → 三种连接方式（原生 RadioButtons，一眼可见）→ 所选方式的表单
/// → 唯一主操作“连接 OpenCode”（强调色按钮）→ 状态条（失败 / 待保存 / 成功按 InfoBar 语义着色，并配图标，不只靠颜色）。
/// 说明文字一律使用次要 Caption 样式降低密度；滚动交给 ContentDialog 自带的滚动视图（不再嵌套第二个 ScrollViewer）。
/// </remarks>
public sealed class OpenCodeConnectionView : UserControl
{
    private readonly SettingsService _settings;
    private readonly OpenCodeConnectionCommitter _committer;
    private readonly WslOpenCodeServiceLocator _locator = new(new WslUncFileSystem());
    private readonly RadioButtons _flow = new() { Header = "连接方式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _distributions = new() { Header = "WSL 发行版", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _services = new() { Header = "发现多个服务，请选择一个（只连接此实例）", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly Button _refresh = new() { Content = "刷新 WSL 发行版" };
    private readonly TextBox _address = new() { Header = "远程地址（绝对 http/https URL）", PlaceholderText = "https://服务器" };
    private readonly TextBox _username = new() { Header = "用户名" };
    private readonly PasswordBox _password = new() { Header = "密码（不回显已保存凭据）", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly PasswordBox _pairLink = new() { Header = "远程配对链接", PlaceholderText = "https://服务器/auth/connect/…", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBlock _summary = Text(string.Empty, OpenCodeInteractionUi.BodyStrongStyleKey);
    private readonly TextBlock _status = Text(string.Empty, OpenCodeInteractionUi.BodyStyleKey);
    private readonly FontIcon _statusIcon = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Border _statusCard = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 8, 12, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock _pairHint = Hint("一次性链接只在点击连接时兑换；失败不自动重试。兑换成功后可在本窗口重试验证与保存，不再兑换。");
    private readonly Border _pairPending = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 8, 12, 8) };
    private readonly Button _connect = new() { Content = "连接 OpenCode", MinWidth = 120, Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle") };
    private readonly Button _discardPair = new() { Content = "放弃配对结果", Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "取消操作", Visibility = Visibility.Collapsed };
    private readonly ProgressRing _progress = new() { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _wsl = new() { Spacing = 12 };
    private readonly StackPanel _remote = new() { Spacing = 12 };
    private readonly StackPanel _pairing = new() { Spacing = 12 };
    private CancellationTokenSource? _operation;
    private OpenCodePairingResult? _pairingResult;
    private bool _closed;
    private bool _loaded;

    public bool IsBusy => _operation is not null;

    public OpenCodeConnectionView(SettingsService settings, DpapiSecretStore secrets)
    {
        _settings = settings;
        _committer = new(OpenCodeConnectionCommitter.VerifyWithOpenCodeAsync, secrets.SetAsync,
            settings.UpdateAsync, async (id, ct) => { await secrets.RemoveAsync(id, ct); });
        AutomationProperties.SetAutomationId(this, "AccountOpenCodeConnection");
        AutomationProperties.SetAutomationId(_flow, "AccountOpenCodeFlow");
        AutomationProperties.SetAutomationId(_distributions, "AccountOpenCodeDistribution");
        AutomationProperties.SetAutomationId(_services, "AccountOpenCodeService");
        AutomationProperties.SetAutomationId(_address, "AccountOpenCodeAddress");
        AutomationProperties.SetAutomationId(_username, "AccountOpenCodeUsername");
        AutomationProperties.SetAutomationId(_password, "AccountOpenCodePassword");
        AutomationProperties.SetAutomationId(_pairLink, "AccountOpenCodePairLink");
        AutomationProperties.SetAutomationId(_connect, "AccountOpenCodeConnect");
        AutomationProperties.SetAutomationId(_status, "AccountOpenCodeStatus");

        foreach (string name in new[] { "WSL 自动发现", "远程地址 + 用户名 + 密码", "远程配对链接" }) _flow.Items.Add(name);

        // ① WSL：发行版选择与刷新同一行（刷新按钮与输入框底边对齐），说明为次要 Caption。
        _wsl.Children.Add(Hint("只读发现已部署服务的标准 service*.json 登记，不猜端口，不启动或修改 OpenCode。NixOS 根据系统元数据识别，不按发行版名称猜测。"));
        var distributionRow = new Grid { ColumnSpacing = 8 };
        distributionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        distributionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _refresh.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_refresh, 1);
        distributionRow.Children.Add(_distributions);
        distributionRow.Children.Add(_refresh);
        _wsl.Children.Add(distributionRow);
        _wsl.Children.Add(_services);
        _wsl.Children.Add(Hint("当前支持默认 ~/.local/state/opencode 登记。自定义 XDG_STATE_HOME、无登记服务或 WSL localhost 转发不可用时无法自动连接；可改用远程方式。访问发行版文件可能唤醒 WSL。"));

        // ② 远程密码。
        _remote.Children.Add(_address);
        _remote.Children.Add(_username);
        _remote.Children.Add(_password);
        _remote.Children.Add(Hint("密码仅以 Windows DPAPI 密文保存；推荐 HTTPS。HTTP 不加密网络传输。"));

        // ③ 配对链接：提示平时为次要说明；“已兑换、待保存”时变为注意色卡片并显示“放弃配对结果”。
        _pairPending.Child = _pairHint;
        _pairing.Children.Add(_pairLink);
        _pairing.Children.Add(_pairPending);
        _pairing.Children.Add(_discardPair);

        // 当前连接：卡片中“当前配置”为强调正文，单账号规则为次要说明。
        var currentPanel = new StackPanel { Spacing = 4 };
        currentPanel.Children.Add(_summary);
        currentPanel.Children.Add(Hint("仅保留一个 OpenCode 账号 / 实例。连接成功会替换当前连接；验证或保存失败保留原连接。旧配置及 DPAPI 凭据保持原样，不需要重新登录。关闭此窗口会丢弃输入与未保存的配对结果。"));
        var currentCard = new Border { Child = currentPanel, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 10) };
        OpenCodeToolTheme.Apply(currentCard, OpenCodeToolTheme.Border("CardBackgroundFillColorDefaultBrush", OpenCodeToolTheme.CardStroke));

        // 唯一主操作（强调色）在左，取消与进度环跟随其后。
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_connect);
        actions.Children.Add(_cancel);
        actions.Children.Add(_progress);

        // 状态条：图标 + 文本（文本保持主文字色，着色只在底色与图标上，与 Fluent InfoBar 一致）。
        var statusRow = new Grid { ColumnSpacing = 10 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetAccessibilityView(_statusIcon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Grid.SetColumn(_status, 1);
        statusRow.Children.Add(_statusIcon);
        statusRow.Children.Add(_status);
        _statusCard.Child = statusRow;

        var body = new StackPanel { Spacing = 16, MaxWidth = 520 };
        body.Children.Add(currentCard);
        body.Children.Add(_flow);
        body.Children.Add(_wsl);
        body.Children.Add(_remote);
        body.Children.Add(_pairing);
        body.Children.Add(actions);
        body.Children.Add(_statusCard);
        // ContentDialog 内容区自带纵向滚动视图：窄 / 矮窗口时整页滚动，不出现嵌套滚动条。
        Content = body;

        _flow.SelectionChanged += (_, _) => UpdateFlow();
        _distributions.SelectionChanged += (_, _) => { _services.Items.Clear(); _services.Visibility = Visibility.Collapsed; };
        _refresh.Click += async (_, _) => await RunAsync(RefreshDistributionsAsync);
        _connect.Click += async (_, _) => await RunAsync(ConnectAsync);
        _cancel.Click += (_, _) => _operation?.Cancel();
        _discardPair.Click += (_, _) => { _pairingResult = null; UpdatePairing(); SetStatus("已放弃配对结果；继续配对需要新链接。"); };
        _flow.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await RunAsync(async ct =>
            {
                var current = await _settings.LoadAsync(ct);
                ShowSummary(current.OpenCode);
                _address.Text = SafeAddress(current.OpenCode.BaseUrl);
                _username.Text = current.OpenCode.Username ?? string.Empty;
                await RefreshDistributionsAsync(ct);
            });
        };
        Unloaded += (_, _) => Close();
    }

    private static TextBlock Text(string value, string styleKey)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        if (OpenCodeInteractionUi.TryStyle(styleKey) is { } style) text.Style = style;
        return text;
    }

    /// <summary>说明文字：次要 Caption（主题次要文字色，Light / Dark 均 ≥ 4.5:1；高对比度下为系统文字色）。</summary>
    private static TextBlock Hint(string value) => Text(value, OpenCodeInteractionUi.CaptionSubtleStyleKey);

    private enum StatusTone { Neutral, Success, Warning, Error }

    /// <summary>
    /// 仅视觉：同一状态文本按语义着色。Neutral（进行中 / 提示）无底色、次要文字；Success / Warning / Error 使用 Fluent InfoBar
    /// 同款背景画刷 + 语义色图标（不只靠颜色），文本保持主文字色以确保对比度。空文本隐藏状态条。
    /// </summary>
    private void SetStatus(string text, StatusTone tone = StatusTone.Neutral)
    {
        _status.Text = text;
        _statusCard.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        var (background, iconKey, glyph) = tone switch
        {
            StatusTone.Success => (OpenCodeToolTheme.SuccessBackground, OpenCodeToolTheme.Success, "\uEC61"),
            StatusTone.Warning => (OpenCodeToolTheme.CautionBackground, OpenCodeToolTheme.Caution, "\uE7BA"),
            StatusTone.Error => (OpenCodeToolTheme.CriticalBackground, OpenCodeToolTheme.Critical, "\uEB90"),
            _ => ((string?)null, OpenCodeToolTheme.TextSecondary, "\uE946"),
        };
        if (background is null)
        {
            _statusCard.ClearValue(Border.BackgroundProperty);
            _statusCard.Padding = new Thickness(0);
            if (OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.BodySubtleStyleKey) is { } subtle) _status.Style = subtle;
        }
        else
        {
            OpenCodeToolTheme.Apply(_statusCard, OpenCodeToolTheme.Border(background));
            _statusCard.Padding = new Thickness(12, 8, 12, 8);
            if (OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.BodyStyleKey) is { } body) _status.Style = body;
        }

        _statusIcon.Glyph = glyph;
        OpenCodeToolTheme.Apply(_statusIcon, OpenCodeToolTheme.Icon(iconKey));
    }

    private static string SafeAddress(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? url! : string.Empty;

    private void ShowSummary(OpenCodeSettings settings)
    {
        var summary = OpenCodeConnectionProfile.Describe(settings);
        _summary.Text = "当前配置：" + summary.DisplayText + (summary.IsLegacy ? "（兼容旧配置，未改写）" : string.Empty);
    }

    private void UpdateFlow()
    {
        _wsl.Visibility = _flow.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        _remote.Visibility = _flow.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _pairing.Visibility = _flow.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(string.Empty);
    }

    private void UpdatePairing()
    {
        _pairLink.IsEnabled = _pairingResult is null;
        _discardPair.Visibility = _pairingResult is null ? Visibility.Collapsed : Visibility.Visible;
        _pairHint.Text = _pairingResult is null
            ? "一次性链接只在点击连接时兑换；不会自动重试。取消、超时或网络失败可能已耗码，需获取新链接。"
            : "配对已兑换但尚未保存。点击“连接 OpenCode”仅重试验证与保存，不会再次兑换；关闭窗口将丢弃此结果。";
        // 待保存：注意色卡片 + 正文字号；平时：无底色的次要说明。
        if (_pairingResult is null)
        {
            _pairPending.ClearValue(Border.BackgroundProperty);
            _pairPending.Padding = new Thickness(0);
            if (OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey) is { } hint) _pairHint.Style = hint;
        }
        else
        {
            OpenCodeToolTheme.Apply(_pairPending, OpenCodeToolTheme.Border(OpenCodeToolTheme.CautionBackground));
            _pairPending.Padding = new Thickness(12, 8, 12, 8);
            if (OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.BodyStyleKey) is { } body) _pairHint.Style = body;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _closed) return;
        using var cts = new CancellationTokenSource();
        _operation = cts;
        SetBusy(true);
        try { await action(cts.Token); }
        catch (OperationCanceledException) { if (!_closed) SetStatus("操作已取消；不会自动重试。"); }
        catch (Exception ex) { if (!_closed) SetStatus("操作失败：" + OpenCodeConnectionCommitter.Describe(ex), StatusTone.Error); }
        finally
        {
            _operation = null;
            if (!_closed) { SetBusy(false); UpdatePairing(); }
        }
    }

    private void SetBusy(bool busy)
    {
        _flow.IsEnabled = !busy;
        foreach (var control in new Control[] { _distributions, _services, _refresh, _address, _username, _password, _pairLink, _discardPair })
            control.IsEnabled = !busy;
        _connect.IsEnabled = !busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsActive = busy;
    }

    private async Task RefreshDistributionsAsync(CancellationToken ct)
    {
        SetStatus("正在枚举 WSL 发行版…");
        var items = await Task.Run(async () =>
        {
            var list = await WslDistributionCatalog.CreateDefault().ListAsync(ct);
            return list.Select(d => (Distribution: d, NixOS: _locator.IsNixOS(d))).ToArray();
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (_closed) return;
        _distributions.Items.Clear();
        _services.Items.Clear();
        _services.Visibility = Visibility.Collapsed;
        foreach (var item in items)
            _distributions.Items.Add(new ComboBoxItem { Content = item.Distribution.Name + (item.NixOS ? " · NixOS" : string.Empty) + (item.Distribution.IsDefault ? " · 默认" : string.Empty), Tag = item.Distribution });
        if (items.Length > 0) _distributions.SelectedIndex = 0;
        if (items.Length == 0) SetStatus("未找到可用的 WSL 发行版。请确认 WSL 已安装，或使用远程连接。", StatusTone.Warning);
        else SetStatus("选择发行版后点击“连接 OpenCode”，将自动发现并验证服务。");
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        OpenCodeClientOptions options;
        string? distribution = null;
        bool pairing = _flow.SelectedIndex == 2;
        var kind = _flow.SelectedIndex == 0 ? OpenCodeConnectionKind.Wsl : OpenCodeConnectionKind.Remote;
        if (kind == OpenCodeConnectionKind.Wsl)
        {
            if (_distributions.SelectedItem is not ComboBoxItem { Tag: WslDistributionInfo distro })
            { SetStatus("请先选择可用的 WSL 发行版。", StatusTone.Warning); return; }
            distribution = distro.Name;
            WslOpenCodeService? selected = (_services.SelectedItem as ComboBoxItem)?.Tag as WslOpenCodeService;
            if (selected is null)
            {
                SetStatus("正在发现所选 WSL 发行版中的 OpenCode 服务…");
                var found = await _locator.DiscoverAsync(distro, ct);
                ct.ThrowIfCancellationRequested();
                if (_closed) return;
                if (found.Count == 0)
                { SetStatus("未发现标准位置的 OpenCode 服务登记。服务可能未运行、登记不可读，或使用自定义 XDG_STATE_HOME。自动连接不可用；可使用远程方式。本客户端不会启动后端。", StatusTone.Error); return; }
                if (found.Count > 1)
                {
                    _services.Items.Clear();
                    foreach (var service in found) _services.Items.Add(new ComboBoxItem { Content = service.User + " · " + service.Channel, Tag = service });
                    _services.Visibility = Visibility.Visible;
                    SetStatus("发现多个服务；请选择一个后点击“连接 OpenCode”。这不会添加第二个账号。", StatusTone.Warning);
                    return;
                }
                selected = found[0];
            }
            options = selected.ToClientOptions();
        }
        else if (pairing)
        {
            if (_pairingResult is null)
            {
                // Parse before clearing: invalid local input can be corrected without consuming a code.
                var link = OpenCodePairingService.Parse(_pairLink.Password.Trim());
                _pairLink.Password = string.Empty;
                SetStatus("正在单次兑换配对链接（不会自动重试）…");
                using var service = new OpenCodePairingService();
                try { _pairingResult = await service.ExchangeAsync(link, ct); }
                catch { if (!_closed) SetStatus("兑换失败或取消；兑换码可能已消耗，请获取新链接。", StatusTone.Error); throw; }
                if (_closed) { _pairingResult = null; return; }
                UpdatePairing();
            }
            options = _pairingResult.ToClientOptions();
        }
        else
        {
            options = new OpenCodeClientOptions { BaseUrl = _address.Text.Trim(), AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = _username.Text.Trim(), Secret = _password.Password };
        }

        SetStatus("正在验证后端并安全保存（成功后替换当前唯一连接）…");
        var outcome = await _committer.CommitAsync(kind, options, distribution, ct);
        if (_closed) return;
        if (!outcome.Succeeded)
        {
            SetStatus(outcome.Message + (pairing && _pairingResult is not null ? " 配对结果已保留；可点击连接重试验证与保存，不会再次兑换。" : string.Empty), StatusTone.Error);
            return;
        }

        SetStatus(outcome.Message, StatusTone.Success);
        _password.Password = string.Empty;
        _pairingResult = null;
        _pairLink.Password = string.Empty;
        _services.Items.Clear(); // Drop discovered in-memory secrets after a successful connection.
        _services.Visibility = Visibility.Collapsed;
        if (_settings.Current is { } current) ShowSummary(current.OpenCode);
        // The new credential reference causes the existing App settings owner to reconnect exactly once.
    }

    public void Close()
    {
        _closed = true;
        _operation?.Cancel();
        _pairingResult = null;
        _password.Password = string.Empty;
        _pairLink.Password = string.Empty;
        _services.Items.Clear();
    }
}
