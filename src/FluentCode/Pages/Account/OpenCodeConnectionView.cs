using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Configuration;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Connection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FluentCode.Pages.Account;

/// <summary>唯一 OpenCode 连接表单（设置 › 账号与连接、首次运行欢迎页）。秘密仅留在密码控件/内存中；离开即清空，不自动兑换或重试。</summary>
/// <remarks>
/// 视觉层级（仅原生控件 + 主题画刷）：当前连接卡片 → 连接方式（原生 RadioButtons，一眼可见）→ 所选方式的表单
/// → 唯一主操作“连接 OpenCode”（强调色按钮）→ 状态条（失败 / 待保存 / 成功按 InfoBar 语义着色，并配图标，不只靠颜色）。
/// 说明文字一律使用次要 Caption 样式降低密度；滚动交给 ContentDialog 自带的滚动视图（不再嵌套第二个 ScrollViewer）。
/// </remarks>
public sealed class OpenCodeConnectionView : UserControl
{
    private readonly SettingsService _settings;
    private readonly OpenCodeConnectionCommitter _committer;
    private readonly WslOpenCodeServiceLocator _locator = new(new WslOpenCodeCommandRunner());
    private readonly RadioButtons _flow = new() { Header = "连接方式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _distributions = new() { Header = "WSL 发行版", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _refresh = new() { Content = "刷新 WSL 发行版" };
    private const string SshHostPlaceholder = "选择或输入，如 myserver、user@host:22";
    private static readonly string SshClientMissingText = new SshOpenCodeException(SshFailureKind.MissingOpenSsh).Message;
    private readonly ComboBox _sshHost = new() { Header = "SSH 主机", IsEditable = true, PlaceholderText = SshHostPlaceholder, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _refreshSsh = new() { Content = "刷新 SSH 主机" };
    // 字段自带说明（原生 Description），提示与字段成组，而不是另起一段。
    private readonly TextBox _sshCommand = new() { Header = "远程 opencode 路径（可选）", PlaceholderText = "默认 opencode",
        Description = Hint("远程找不到 opencode 时再填写，例如 ~/.local/bin/opencode") };
    private readonly TextBox _address = new() { Header = "远程地址（绝对 http/https URL）", PlaceholderText = "https://服务器" };
    private readonly TextBox _username = new() { Header = "用户名" };
    private readonly PasswordBox _password = new() { Header = "密码（不回显已保存凭据）", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly PasswordBox _pairLink = new() { Header = "远程配对链接", PlaceholderText = "https://服务器/auth/connect/…", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBlock _summary = Text(string.Empty, OpenCodeInteractionUi.BodyStrongStyleKey);
    private readonly TextBlock _status = Text(string.Empty, OpenCodeInteractionUi.BodyStyleKey);
    private readonly HyperlinkButton _statusAction = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
    private Func<Task>? _statusActionHandler;
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
    private readonly StackPanel _ssh = new() { Spacing = 12 };
    private CancellationTokenSource? _operation;
    private OpenCodePairingResult? _pairingResult;
    private bool _closed;
    private bool _loaded;
    private bool _sshHostsLoaded;
    private bool _distributionsLoaded;
    private bool _hostKeyDeclined;

    public bool IsBusy => _operation is not null;

    /// <param name="showCurrentConnection">欢迎页（尚未添加任何 OpenCode）不显示“当前配置”卡片。</param>
    public OpenCodeConnectionView(SettingsService settings, DpapiSecretStore secrets, bool showCurrentConnection = true)
    {
        _settings = settings;
        _committer = new(OpenCodeConnectionCommitter.VerifyWithOpenCodeAsync, secrets.SetAsync,
            settings.UpdateAsync, async (id, ct) => { await secrets.RemoveAsync(id, ct); });
        AutomationProperties.SetAutomationId(this, "AccountOpenCodeConnection");
        AutomationProperties.SetAutomationId(_flow, "AccountOpenCodeFlow");
        AutomationProperties.SetAutomationId(_distributions, "AccountOpenCodeDistribution");
        AutomationProperties.SetAutomationId(_address, "AccountOpenCodeAddress");
        AutomationProperties.SetAutomationId(_username, "AccountOpenCodeUsername");
        AutomationProperties.SetAutomationId(_password, "AccountOpenCodePassword");
        AutomationProperties.SetAutomationId(_pairLink, "AccountOpenCodePairLink");
        AutomationProperties.SetAutomationId(_sshHost, "AccountOpenCodeSshHost");
        AutomationProperties.SetAutomationId(_sshCommand, "AccountOpenCodeSshCommand");
        AutomationProperties.SetAutomationId(_refreshSsh, "AccountOpenCodeSshRefresh");
        AutomationProperties.SetAutomationId(_connect, "AccountOpenCodeConnect");
        AutomationProperties.SetAutomationId(_status, "AccountOpenCodeStatus");
        // 进度与结果只在状态条里变化：设为实时区域，讲述人才能听到（SetStatus 中触发 LiveRegionChanged）。
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        foreach (string name in new[] { "WSL 自动发现", "远程地址 + 用户名 + 密码", "远程配对链接", "SSH 自动发现" }) _flow.Items.Add(name);

        // ① WSL：发行版选择与刷新同一行（刷新按钮与输入框底边对齐），说明为次要 Caption。
        _wsl.Children.Add(Hint("通过所选发行版中的 opencode 命令连接当前后台服务，不启动或修改 OpenCode。"));
        var distributionRow = new Grid { ColumnSpacing = 8 };
        distributionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        distributionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _refresh.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_refresh, 1);
        distributionRow.Children.Add(_distributions);
        distributionRow.Children.Add(_refresh);
        _wsl.Children.Add(distributionRow);

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

        // ④ SSH：沿用 WSL 的原生选择框 + 刷新行与同样的间距；不新增视觉样式。
        _ssh.Children.Add(Hint("用本机的 SSH 配置和密钥连接；远程 OpenCode 未运行时会自动启动。"));
        var sshRow = new Grid { ColumnSpacing = 8 };
        sshRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sshRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _refreshSsh.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_refreshSsh, 1);
        sshRow.Children.Add(_sshHost);
        sshRow.Children.Add(_refreshSsh);
        _ssh.Children.Add(sshRow);
        _ssh.Children.Add(_sshCommand);
        // BatchMode=yes：不会弹出 SSH 密码 / 密码短语输入，必须事先说明，而不是等认证失败。
        _ssh.Children.Add(Hint("只支持密钥登录，不能在这里输入 SSH 密码；带密码的密钥请先用 ssh-add 加载。首次连接新主机时需核对指纹。"));
        _ssh.Children.Add(Hint("通过 SSH 获取的 OpenCode 服务密码仅在内存中使用，不会保存。"));

        // 当前连接：卡片中“当前配置”为强调正文，单账号规则为次要说明。
        var currentPanel = new StackPanel { Spacing = 4 };
        currentPanel.Children.Add(_summary);
        currentPanel.Children.Add(Hint("连接成功后会替换当前连接；失败时保留原连接。"));
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
        var statusBody = new StackPanel();
        statusBody.Children.Add(_status);
        statusBody.Children.Add(_statusAction);
        Grid.SetColumn(statusBody, 1);
        statusRow.Children.Add(_statusIcon);
        statusRow.Children.Add(statusBody);
        _statusCard.Child = statusRow;
        _statusAction.Click += async (_, _) =>
        {
            if (_statusActionHandler is not { } action) return;
            try { await action(); }
            catch (Exception) { SetStatus("无法执行此操作，请在终端或 Windows 设置中手动处理。", StatusTone.Warning); }
        };

        var body = new StackPanel { Spacing = 16, MaxWidth = 520 };
        if (showCurrentConnection) body.Children.Add(currentCard);
        body.Children.Add(_flow);
        body.Children.Add(_wsl);
        body.Children.Add(_remote);
        body.Children.Add(_pairing);
        body.Children.Add(_ssh);
        body.Children.Add(actions);
        body.Children.Add(_statusCard);
        // 宿主（设置分类滚动区 / 欢迎页滚动区）负责纵向滚动，不出现嵌套滚动条。
        Content = body;

        _flow.SelectionChanged += (_, _) => UpdateFlow();
        _refresh.Click += async (_, _) => await RunAsync(async ct =>
        {
            FluentCode.Motion.ActionFeedback.Busy(_refresh);
            try
            {
                await RefreshDistributionsAsync(ct);
                if (!_closed) FluentCode.Motion.ActionFeedback.Success(_refresh, "已刷新 WSL 发行版");
            }
            catch (Exception)
            {
                if (!_closed) FluentCode.Motion.ActionFeedback.Failure(_refresh, "刷新失败");
                throw;
            }
        });
        _connect.Click += async (_, _) => await RunAsync(ConnectAsync);
        _refreshSsh.Click += async (_, _) => await RunAsync(async ct =>
        {
            FluentCode.Motion.ActionFeedback.Busy(_refreshSsh);
            try
            {
                await RefreshSshHostsAsync(ct);
                if (!_closed) FluentCode.Motion.ActionFeedback.Success(_refreshSsh, "已刷新 SSH 主机");
            }
            catch (Exception)
            {
                if (!_closed) FluentCode.Motion.ActionFeedback.Failure(_refreshSsh, "刷新失败");
                throw;
            }
        });
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
                _sshHost.Text = current.OpenCode.SshHost ?? string.Empty;
                _sshCommand.Text = current.OpenCode.SshCommand ?? string.Empty;
                // 只加载当前方式需要的列表；另一种方式在首次切换时再读取（UpdateFlow）。
                if (current.OpenCode.Kind == OpenCodeConnectionKind.Ssh)
                {
                    await RefreshSshHostsAsync(ct);
                    _flow.SelectedIndex = 3;
                }
                else await RefreshDistributionsAsync(ct);
            });
        };
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
    private void SetStatus(string text, StatusTone tone = StatusTone.Neutral, string? actionText = null, Func<Task>? action = null)
    {
        _status.Text = text;
        _statusAction.Content = actionText;
        _statusActionHandler = action;
        _statusAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
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
        if (!string.IsNullOrEmpty(text))
        {
            AutomationProperties.SetLiveSetting(_status, tone == StatusTone.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            (FrameworkElementAutomationPeer.FromElement(_status) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_status))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        // 结果（成功 / 警告 / 失败）出现在表单底部：滚动宿主中自动带入视野，避免在矮窗口里看不到。
        if (tone != StatusTone.Neutral && _statusCard.IsLoaded)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _statusCard.StartBringIntoView());
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
        _ssh.Visibility = _flow.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(string.Empty);
        _connect.IsEnabled = !IsBusy && !SshBlocked;
        if (_flow.SelectedIndex == 3)
        {
            // 与 WSL “未找到发行版”一致：缺少前提时先给出警告与修复路径，而不是连接后报通用错误。
            if (SshBlocked) { ShowSshFailure(new(SshFailureKind.MissingOpenSsh)); return; }
            if (_loaded && !_sshHostsLoaded && !IsBusy) _ = RunAsync(RefreshSshHostsAsync);
        }
        else if (_flow.SelectedIndex == 0 && _loaded && !_distributionsLoaded && !IsBusy) _ = RunAsync(RefreshDistributionsAsync);
    }

    private static bool SshClientMissing => !File.Exists(SshCommandBuilder.Executable());
    private bool SshBlocked => _flow.SelectedIndex == 3 && SshClientMissing;

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
        foreach (var control in new Control[] { _distributions, _refresh, _address, _username, _password, _pairLink, _discardPair, _sshHost, _sshCommand, _refreshSsh })
            control.IsEnabled = !busy;
        _connect.IsEnabled = !busy && !SshBlocked;
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
            return await Task.WhenAll(list.Select(async d => (Distribution: d, NixOS: await _locator.IsNixOSAsync(d, ct))));
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (_closed) return;
        _distributions.Items.Clear();
        foreach (var item in items)
            _distributions.Items.Add(new ComboBoxItem { Content = item.Distribution.Name + (item.NixOS ? " · NixOS" : string.Empty) + (item.Distribution.IsDefault ? " · 默认" : string.Empty), Tag = item.Distribution });
        if (items.Length > 0) _distributions.SelectedIndex = 0;
        _distributionsLoaded = true;
        if (items.Length == 0) SetStatus("未找到可用的 WSL 发行版。请确认 WSL 已安装，或使用远程连接。", StatusTone.Warning);
        else SetStatus("选择发行版后点击“连接 OpenCode”，将自动发现并验证服务。");
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_flow.SelectedIndex == 3) { await ConnectSshAsync(ct); return; }
        OpenCodeClientOptions options;
        WslOpenCodeService? discovered = null;
        string? distribution = null;
        bool pairing = _flow.SelectedIndex == 2;
        var kind = _flow.SelectedIndex == 0 ? OpenCodeConnectionKind.Wsl : OpenCodeConnectionKind.Remote;
        if (kind == OpenCodeConnectionKind.Wsl)
        {
            if (_distributions.SelectedItem is not ComboBoxItem { Tag: WslDistributionInfo distro })
            { SetStatus("请先选择可用的 WSL 发行版。", StatusTone.Warning); return; }
            distribution = distro.Name;
            SetStatus("正在通过 WSL 中的 opencode 命令连接后台服务…");
            var selected = await _locator.DiscoverAsync(distro, ct);
            discovered = selected;
            ct.ThrowIfCancellationRequested();
            if (_closed) return;
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
        if (discovered is not null && outcome.RefreshDiscovery)
            await _locator.InvalidateAsync(discovered);
        if (_closed) return;
        if (!outcome.Succeeded)
        {
            SetStatus(outcome.Message + (pairing && _pairingResult is not null ? " 配对结果已保留；可点击连接重试验证与保存，不会再次兑换。" : string.Empty), StatusTone.Error);
            return;
        }

        SetStatus(string.Empty);
        FluentCode.Motion.ActionFeedback.Success(_connect, "已保存并连接 OpenCode");
        _password.Password = string.Empty;
        _pairingResult = null;
        _pairLink.Password = string.Empty;
        if (_settings.Current is { } current) ShowSummary(current.OpenCode);
        // The new credential reference causes the existing App settings owner to reconnect exactly once.
    }

    private async Task RefreshSshHostsAsync(CancellationToken ct)
    {
        string text = _sshHost.Text;
        SetStatus("正在读取 SSH 配置中的主机…");
        var hosts = await Task.Run(SshConfigHostCatalog.ReadDefault, ct);
        ct.ThrowIfCancellationRequested();
        if (_closed) return;
        _sshHost.Items.Clear();
        foreach (string host in hosts) _sshHost.Items.Add(host);
        _sshHost.Text = text;
        _sshHostsLoaded = true;
        _sshHost.PlaceholderText = hosts.Count == 0 ? "输入 user@host 或 user@host:端口" : SshHostPlaceholder;
        SetStatus(hosts.Count == 0 ? "SSH 配置中没有主机别名，可直接输入 user@host。"
            : $"找到 {hosts.Count} 个 SSH 主机。选择主机后点击“连接 OpenCode”。");
    }

    private void ShowSshFailure(SshOpenCodeException error)
    {
        if (error.Kind == SshFailureKind.MissingOpenSsh)
            SetStatus(SshClientMissingText, StatusTone.Warning, "打开“可选功能”", async () =>
            {
                if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:optionalfeatures")))
                    SetStatus("无法打开“可选功能”。请在 Windows 设置中手动添加 OpenSSH 客户端。", StatusTone.Warning);
            });
        else if (error.Kind == SshFailureKind.ChangedHostKey && error.HostKeyRemovalCommand is { } command)
            SetStatus(error.Message, StatusTone.Error, "复制删除旧记录的命令", () =>
            {
                CopyText(command);
                FluentCode.Motion.ActionFeedback.Success(_statusAction, "已复制命令；请核对新指纹和 known_hosts 路径后再运行");
                return Task.CompletedTask;
            });
        else SetStatus(error.Message, StatusTone.Error);
    }

    private static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private async Task ConnectSshAsync(CancellationToken ct)
    {
        string host = _sshHost.Text.Trim();
        string? command = string.IsNullOrWhiteSpace(_sshCommand.Text) ? null : _sshCommand.Text.Trim();
        // 与 WSL “请先选择发行版”一致：本地可判断的问题用警告提示，不进入连接流程。
        if (host.Length == 0) { SetStatus("请先选择或输入 SSH 主机。", StatusTone.Warning); return; }
        if (!SshConnectionTarget.TryParse(host, out _))
        { SetStatus("主机格式不对。请填 SSH 配置中的别名，或 user@host、user@host:端口。", StatusTone.Warning); return; }
        if (!SshConnectionTarget.IsValidCommand(command))
        { SetStatus("路径只能是 opencode 程序名或路径，不能带参数或符号。", StatusTone.Warning); return; }
        if (SshClientMissing) { ShowSshFailure(new(SshFailureKind.MissingOpenSsh)); return; }
        var settings = new OpenCodeSettings { Kind = OpenCodeConnectionKind.Ssh, SshHost = host, SshCommand = command };
        _hostKeyDeclined = false;
        SshOpenCodeConnection opened;
        try
        {
            opened = await new SshOpenCodeConnector().ConnectAsync(settings, ConfirmSshHostKeyAsync,
                text => DispatcherQueue.TryEnqueue(() => { if (!_closed && !ct.IsCancellationRequested) SetStatus(text); }), ct);
        }
        catch (OperationCanceledException) when (_hostKeyDeclined && !_closed && !ct.IsCancellationRequested)
        { SetStatus("未信任此主机，已取消连接。"); return; }
        catch (SshOpenCodeException ex) when (!_closed)
        { ShowSshFailure(ex); return; }
        catch (OpenCodeConfigurationException ex) when (!_closed)
        { SetStatus(ex.Message, StatusTone.Error); return; }
        using var connection = opened;
        ct.ThrowIfCancellationRequested();
        if (_closed) return;
        SetStatus("正在保存连接…");
        var outcome = await _committer.CommitAsync(OpenCodeConnectionKind.Ssh, connection.ToClientOptions(), null, ct, host, command);
        if (_closed) return;
        if (!outcome.Succeeded) { SetStatus(outcome.Message, StatusTone.Error); return; }
        _password.Password = string.Empty;
        _pairLink.Password = string.Empty;
        _pairingResult = null;
        ShowSummary(_settings.Current!.OpenCode);
        string success = "已连接 " + host + "。关闭 FluentCode 不会停止远程 OpenCode。\n" + outcome.Message;
        SetStatus(success + (outcome.Warning is { } warning ? "\n" + warning : string.Empty),
            outcome.Warning is null ? StatusTone.Success : StatusTone.Warning);
        FluentCode.Motion.ActionFeedback.Success(_connect, "已保存并连接 OpenCode");
        // The app settings owner opens its own tunnel; this preflight tunnel is always disposed, including failures.
    }

    private Task<bool> ConfirmSshHostKeyAsync(SshHostKeyConfirmation confirmation, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_closed || ct.IsCancellationRequested || XamlRoot is null) { answer.TrySetResult(false); return; }
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "核对主机指纹", Content = HostKeyBody(confirmation),
                    PrimaryButtonText = "一致，信任并连接", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                    Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle") };
                AutomationProperties.SetAutomationId(dialog, "AccountOpenCodeSshHostKeyConfirmation");
                using var registration = ct.Register(() => DispatcherQueue.TryEnqueue(() => { try { dialog.Hide(); } catch (Exception) { } }));
                var result = await dialog.ShowAsync();
                bool accepted = result == ContentDialogResult.Primary && !_closed && !ct.IsCancellationRequested;
                // 仅用户主动拒绝时给出专门文案；“取消操作”/关闭窗口仍走通用取消。
                if (!accepted && !ct.IsCancellationRequested) _hostKeyDeclined = true;
                answer.TrySetResult(accepted);
            }
            catch (Exception) { answer.TrySetResult(false); }
        })) answer.TrySetResult(false);
        return answer.Task;
    }

    /// <summary>
    /// 指纹对话框：说明 → 主机 / 实际地址（标签-值两列）→ 每个公钥一张卡片（算法为次要说明，指纹为等宽、可选择文本）→ 记住后果。
    /// 等宽字体区分 l / I / 1 与 O / 0；完整 “SHA256:…” 保持一行（约 50 字符，ContentDialog 内约 420 epx），复制即为标准格式。
    /// </summary>
    private static StackPanel HostKeyBody(SshHostKeyConfirmation confirmation)
    {
        int count = confirmation.Fingerprints.Count;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Text("这是第一次连接 " + confirmation.Host + "。请向服务器管理员核对下面的"
            + (count > 1 ? "全部 " + count + " 个" : string.Empty) + "指纹，完全一致再继续。", OpenCodeInteractionUi.BodyStyleKey));

        var facts = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Fact(string label, string value)
        {
            int row = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = Hint(label);
            key.VerticalAlignment = VerticalAlignment.Center;
            var text = Text(value, OpenCodeInteractionUi.BodyStyleKey);
            text.IsTextSelectionEnabled = true;
            AutomationProperties.SetName(text, label + "：" + value);
            Grid.SetRow(key, row); Grid.SetRow(text, row); Grid.SetColumn(text, 1);
            facts.Children.Add(key); facts.Children.Add(text);
        }
        Fact("主机", confirmation.Host);
        if (confirmation.ResolvedHost is { Length: > 0 } resolved) Fact("实际地址", resolved);
        if (confirmation.HostKeyAlias is { Length: > 0 } alias) Fact("密钥别名", alias);
        body.Children.Add(facts);

        var prints = new StackPanel { Spacing = 8 };
        prints.Children.Add(Hint("主机指纹"));
        foreach (var fingerprint in confirmation.Fingerprints)
        {
            string algorithm = fingerprint.Algorithm;
            string value = fingerprint.Sha256;
            var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10, 12, 10) };
            OpenCodeToolTheme.Apply(card, OpenCodeToolTheme.Border("CardBackgroundFillColorSecondaryBrush", OpenCodeToolTheme.CardStroke));
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(Hint(algorithm));
            var mono = new TextBlock { Text = value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14,
                IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(mono, algorithm + " 指纹：" + value);
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new Button { Content = new FontIcon { Glyph = "\uE8C8", FontSize = 16 }, Width = 32, Height = 32,
                Style = OpenCodeInteractionUi.TryStyle("SubtleButtonStyle") };
            AutomationProperties.SetName(copy, "复制 " + algorithm + " 指纹");
            ToolTipService.SetToolTip(copy, "复制指纹");
            copy.Click += (_, _) =>
            {
                try { CopyText(value); FluentCode.Motion.ActionFeedback.Success(copy, "已复制指纹"); }
                catch (Exception) { FluentCode.Motion.ActionFeedback.Failure(copy, "复制失败；可选择指纹后手动复制"); }
            };
            Grid.SetColumn(copy, 1);
            row.Children.Add(mono);
            row.Children.Add(copy);
            stack.Children.Add(row);
            card.Child = stack;
            prints.Children.Add(card);
        }
        body.Children.Add(prints);
        body.Children.Add(Hint("继续后会把此主机记入 known_hosts，以后不再询问。指纹不一致请选“取消”。"));
        return body;
    }

    // ---- Opt-in shell capture (FC_SHELL_CAPTURE_TEST, isolated startup-smoke profile only) ----
    internal int TestDistributionCount => _distributions.Items.Count;
    internal string TestStatusText => _status.Text;
    internal void TestFillRemote(string address, string username, string password)
    {
        _flow.SelectedIndex = 1;
        _address.Text = address;
        _username.Text = username;
        _password.Password = password;
    }
    internal void TestSelectWsl() => _flow.SelectedIndex = 0;
    internal void TestSelectSsh() => _flow.SelectedIndex = 3;
    internal Task TestConnectAsync() => RunAsync(ConnectAsync);

    public void Close()
    {
        _closed = true;
        _operation?.Cancel();
        _pairingResult = null;
        _password.Password = string.Empty;
        _pairLink.Password = string.Empty;
    }
}
