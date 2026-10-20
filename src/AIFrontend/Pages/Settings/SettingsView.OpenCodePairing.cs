using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

/// <summary>
/// SettingsView 的 OpenCode 配对链接入口（需求 D 第二入口）：粘贴 V2 完整配对链接 →
/// 纯解析（不联网）→ 用户显式点击兑换（单次 GET，一次性兑换码）→ 用兑换结果验证后端 →
/// 凭据写入 DPAPI 密文存储 → 配置引用更新；三步全部成功后才显示“连接成功”。
/// </summary>
/// <remarks>
/// 安全与并发：
/// <list type="bullet">
/// <item><description>兑换只在显式点击时发生且单飞（进行中忽略再次点击）；超时/取消/失败都不自动重试，
/// 也不自动重新兑换（兑换码可能已消耗）。</description></item>
/// <item><description>兑换成功后的验证或保存失败：结果只保留在内存（<c>_pairingResult</c>），
/// 供用户显式“重试保存”（只重跑验证 + 保存，绝不再次兑换）；放弃或离开界面即丢弃，需获取新链接。</description></item>
/// <item><description>链接内容、兑换码、token/密码绝不写入日志、配置、状态文本或异常展示；配置只保存
/// BaseUrl / Basic / 用户名与 CredentialId，密码仅以 DPAPI 密文保存。</description></item>
/// </list>
/// </remarks>
public sealed partial class SettingsView
{
    private UIElement BuildOpenCodePairingSection()
    {
        _openCodePairLinkBox = new TextBox
        {
            Header = "V2 配对链接（形如 https://主机/auth/connect/兑换码；仅在点击“兑换并连接”时联网）",
            PlaceholderText = "https://主机/auth/connect/…",
        };
        AutomationProperties.SetAutomationId(_openCodePairLinkBox, "SettingsOpenCodePairLink");
        _openCodePairLinkBox.TextChanged += OnOpenCodeFieldChanged;

        _openCodePairButton = new Button { Content = "兑换并连接" };
        _openCodePairButton.Click += OnOpenCodePairExchangeClick;
        AutomationProperties.SetAutomationId(_openCodePairButton, "SettingsOpenCodePairExchange");

        _openCodePairDiscardButton = new Button { Content = "放弃配对结果", Visibility = Visibility.Collapsed };
        _openCodePairDiscardButton.Click += OnOpenCodePairDiscardClick;
        AutomationProperties.SetAutomationId(_openCodePairDiscardButton, "SettingsOpenCodePairDiscard");

        _openCodePairProgress = new ProgressRing { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(_openCodePairButton);
        buttonRow.Children.Add(_openCodePairDiscardButton);
        buttonRow.Children.Add(_openCodePairProgress);

        _openCodePairStatus = CreateSubtleText(
            "配对兑换码短时有效且单次使用：兑换成功后若验证或保存失败，本页会在内存中保留兑换结果供显式重试保存；" +
            "放弃或离开本页后需获取新链接。");

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(CreateSubtleText(
            "配对链接兑换得到的凭据为 Basic 认证（用户名固定 opencode，密码为兑换返回的 token）：" +
            "仅验证 + 安全保存 + 配置更新全部成功后才会显示“连接成功”。"));
        body.Children.Add(_openCodePairLinkBox);
        body.Children.Add(buttonRow);
        body.Children.Add(_openCodePairStatus);

        return new Expander
        {
            Header = "使用配对链接配置（粘贴 V2 完整配对链接）",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = body,
        };
    }

    /// <summary>同步配对入口的按钮文案与“放弃配对结果”可见性（结果在内存中时提供显式重试保存）。</summary>
    private void UpdatePairingUiState()
    {
        if (_openCodePairButton is null)
        {
            return;
        }

        bool hasResult = _pairingResult is not null;
        _openCodePairButton.Content = hasResult ? "重试保存配对结果" : "兑换并连接";
        _openCodePairDiscardButton.Visibility = hasResult ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>清空配对链接输入（程序性写入：不触发输入事件；清空后按当前状态刷新按钮可用态）。</summary>
    private void ClearPairLinkBox()
    {
        if (_openCodePairLinkBox is null)
        {
            return;
        }

        bool wasSuppressed = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            _openCodePairLinkBox.Text = string.Empty;
        }
        finally
        {
            _suppressUiEvents = wasSuppressed;
        }

        UpdateOpenCodeDirtyState();
    }

    private void SetPairingStatus(string message) => _openCodePairStatus.Text = message;

    /// <summary>仅在未卸载且代际未变（未被取消/重启）时写入配对状态文本。</summary>
    private void SetPairingStatus(int generation, string message)
    {
        if (_unloaded || generation != _pairingGeneration)
        {
            return;
        }

        SetPairingStatus(message);
    }

    private void OnOpenCodePairDiscardClick(object sender, RoutedEventArgs e)
    {
        if (_unloaded || _pairingResult is null)
        {
            return;
        }

        _pairingResult = null;
        UpdatePairingUiState();
        UpdateOpenCodeDirtyState();
        SetPairingStatus("已放弃已兑换未保存的配对结果（如需继续请获取新链接）。");
    }

    /// <summary>
    /// 配对入口唯一按钮：无内存结果时执行“解析 → 单次兑换 → 验证 → 保存”；
    /// 有内存结果时只执行“验证 → 保存”（绝不再次兑换，避免重复消耗一次性兑换码）。
    /// </summary>
    private async void OnOpenCodePairExchangeClick(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _unloaded)
        {
            return;
        }

        // 单飞：已有一次兑换/保存在进行中则忽略本次点击（按钮本已禁用，此处为键盘/程序化触发的兜底）。
        if (_pairingCts is not null)
        {
            SetPairingStatus("已有一次配对兑换在进行中，请等待其结束。");
            return;
        }

        // 内存中已有兑换结果：只重试验证 + 保存（不重新兑换）。
        if (_pairingResult is not null)
        {
            await RetryCommitPairingResultAsync();
            return;
        }

        // 调用方在 Parse 前显式去除两端空白；Parse 自身不做宽松修改、不联网。
        string text = _openCodePairLinkBox.Text?.Trim() ?? string.Empty;
        OpenCodePairingLink link;
        try
        {
            link = OpenCodePairingService.Parse(text);
        }
        catch (OpenCodeConfigurationException ex)
        {
            // 纯解析失败（未联网、未耗码）：保留输入供修正；异常消息为固定模板，不含链接内容。
            SetPairingStatus("配对链接无效：" + ex.Message);
            return;
        }

        int generation = ++_pairingGeneration;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _pairingCts = cts;
        _openCodePairButton.IsEnabled = false;
        _openCodePairProgress.Visibility = Visibility.Visible;
        _openCodePairProgress.IsActive = true;
        SetPairingStatus(generation, "正在兑换配对链接（一次性兑换码；不会自动重试，请勿重复提交）…");

        try
        {
            using var service = new OpenCodePairingService();
            OpenCodePairingResult result = await service.ExchangeAsync(link, cts.Token);

            if (_unloaded || generation != _pairingGeneration)
            {
                return; // 已卸载/代际失效：不触碰界面；结果仅存在于局部变量，随即丢弃。
            }

            // 兑换码已使用：立即清空链接输入，防止对已消耗的码再次提交。
            _pairingResult = result;
            ClearPairLinkBox();
            UpdatePairingUiState();
            UpdateOpenCodeDirtyState();
            SetPairingStatus(generation, "兑换成功，正在验证并保存…");
            await TryCommitPairingResultAsync(result, generation, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 取消不是失败：不自动重试；兑换码可能已消耗。
            if (!_unloaded)
            {
                ClearPairLinkBox();
            }

            SetPairingStatus(generation, "兑换已取消。兑换码可能已消耗；不会自动重试。如需继续请获取新链接。");
        }
        catch (OpenCodeTimeoutException ex)
        {
            if (!_unloaded)
            {
                ClearPairLinkBox();
            }

            SetPairingStatus(generation, "兑换超时：" + ex.Message);
        }
        catch (OpenCodeException ex)
        {
            // 异常消息为固定模板（仅状态码/固定描述），不含链接、兑换码、token 或响应体。
            if (!_unloaded)
            {
                ClearPairLinkBox();
            }

            SetPairingStatus(generation, "兑换失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            if (!_unloaded)
            {
                ClearPairLinkBox();
            }

            SetPairingStatus(generation, "兑换失败：" + DescribeSafeError(ex));
        }
        finally
        {
            // 无论成功、失败还是取消，都结束 spinner 并恢复按钮（卸载时不再触碰界面）。
            if (ReferenceEquals(_pairingCts, cts))
            {
                _pairingCts = null;
                if (!_unloaded)
                {
                    _openCodePairButton.IsEnabled = true;
                    _openCodePairProgress.IsActive = false;
                    _openCodePairProgress.Visibility = Visibility.Collapsed;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// 对内存中已兑换的结果显式重试“验证 + 保存”（绝不重新兑换）：
    /// 独立 CTS + 代际隔离 + 单飞（与兑换共用互斥），所有路径收敛 spinner。
    /// </summary>
    private async Task<bool> RetryCommitPairingResultAsync()
    {
        OpenCodePairingResult? result = _pairingResult;
        if (result is null || _unloaded)
        {
            return false;
        }

        if (_pairingCts is not null)
        {
            SetPairingStatus("已有一次配对操作在进行中，请等待其结束。");
            return false;
        }

        int generation = ++_pairingGeneration;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _pairingCts = cts;
        _openCodePairButton.IsEnabled = false;
        _openCodePairProgress.Visibility = Visibility.Visible;
        _openCodePairProgress.IsActive = true;
        SetPairingStatus(generation, "正在重试验证并保存已兑换的配对结果（不会重新兑换）…");

        try
        {
            return await TryCommitPairingResultAsync(result, generation, cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_pairingCts, cts))
            {
                _pairingCts = null;
                if (!_unloaded)
                {
                    _openCodePairButton.IsEnabled = true;
                    _openCodePairProgress.IsActive = false;
                    _openCodePairProgress.Visibility = Visibility.Collapsed;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// 已兑换结果的“验证 + 保存”流水线（不联网兑换）：仅当三步全部成功才显示“连接成功”。
    /// 1) 用 <c>result.ToClientOptions()</c> 构建客户端并只读验证（GET api/location，30 秒超时）；
    /// 2) 密码写入 <c>ISecretStore</c>（DPAPI 密文，CredentialId 引用）；
    /// 3) 配置更新（BaseUrl / Basic / opencode / CredentialId）。
    /// 失败/取消时保留内存结果供显式重试；绝不自动重试、绝不输出链接/兑换码/密码。
    /// </summary>
    private async Task<bool> TryCommitPairingResultAsync(
        OpenCodePairingResult result, int generation, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new OpenCodeClient(result.ToClientOptions());
            await client.GetLocationAsync(null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SetPairingStatus(generation, "配对结果验证已取消；结果已保留，可显式重试保存（不会重新兑换）。");
            return false;
        }
        catch (Exception ex)
        {
            SetPairingStatus(generation,
                "配对已兑换，但后端验证失败：" + DescribeSafeError(ex) +
                "（兑换码可能已消耗；不会自动重试。可重试保存或获取新链接。）");
            return false;
        }

        try
        {
            await _secretStore.SetAsync(OpenCodeCredentialId, result.Secret, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SetPairingStatus(generation, "配对密码保存已取消（未写入；取消不是保存失败）。结果已保留，可显式重试保存。");
            return false;
        }
        catch (Exception ex)
        {
            SetPairingStatus(generation, "配对已兑换，但密码保存失败：" + DescribeSafeError(ex) + "（结果已保留，可显式重试保存。）");
            return false;
        }

        var openCode = _settings.OpenCode with
        {
            BaseUrl = result.BaseUrl,
            AuthenticationMode = OpenCodeAuthenticationMode.Basic,
            Username = result.Username,
            CredentialId = OpenCodeCredentialId,
        };

        if (!await SaveAsync(_settings with { OpenCode = openCode }, "已保存配对配置。"))
        {
            SetPairingStatus(generation, "配对已兑换，但配置写入失败（详见上方状态条）。结果已保留，可显式重试保存。");
            return false;
        }

        // 三步全部成功：清除内存结果并显示“连接成功”（此前绝不显示）。
        _pairingResult = null;
        _openCodeDirty = false;
        ClearPairLinkBox();
        ApplyOpenCodeToUi(preserveEdits: false);
        SetPairingStatus(generation, "连接成功：配对凭据已通过后端验证、密码已安全保存（DPAPI 密文），配置已更新（认证方式：Basic）。");

        // 配对密码已写入 DPAPI 密文且配置保存成功：显式刷新后台 owner（同 CredentialId 的密文更新无法由配置差异推断）。
        RaiseOpenCodeCredentialsChanged();

        return true;
    }
}
