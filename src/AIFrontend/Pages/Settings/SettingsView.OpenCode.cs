using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

/// <summary>
/// SettingsView 的 OpenCode 后端分区（地址、认证方式 Basic/Bearer/None、用户名与凭据、测试连接）。
/// 配对链接第二入口见 SettingsView.OpenCodePairing.cs。
/// </summary>
public sealed partial class SettingsView
{
    private UIElement BuildOpenCodeSection()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(CreateSubtleText(
            "本客户端面向 OpenCode V2 HTTP API（如 GET api/location）；不兼容的 V1 后端会以协议/传输错误呈现，" +
            "不会被当作 V2。此处凭据是 OpenCode 后端/账户的访问凭据（与 provider 的模型 API key 不同）；" +
            "只以 DPAPI 密文保存，绝不写入 settings.json、界面状态或日志；不会回显明文。" +
            "“保存”只写入配置，不建立连接。"));

        // 认证方式（需求 D 入口一）：Basic 手动（用户名+密码）；Bearer 旧配置兼容与 None 为高级选项。
        // 默认显示已保存配置的认证方式；旧配置（无该字段）默认 Bearer，不被强制迁移。
        _openCodeAuthModeCombo = new ComboBox { Header = "认证方式", MinWidth = 320 };
        _openCodeAuthModeCombo.Items.Add("Basic（用户名 + 密码）");
        _openCodeAuthModeCombo.Items.Add("Bearer（旧配置兼容；高级）");
        _openCodeAuthModeCombo.Items.Add("None（不发送认证头；高级）");
        AutomationProperties.SetAutomationId(_openCodeAuthModeCombo, "SettingsOpenCodeAuthMode");
        _openCodeAuthModeCombo.SelectionChanged += OnOpenCodeAuthModeChanged;

        _openCodeAuthModeHint = CreateSubtleText(string.Empty);

        _openCodeUrlBox = new TextBox
        {
            Header = "后端地址（绝对 http/https；可含 IP:端口）",
            PlaceholderText = "http://主机或IP:端口",
        };
        AutomationProperties.SetAutomationId(_openCodeUrlBox, "SettingsOpenCodeUrl");
        _openCodeUrlBox.TextChanged += OnOpenCodeFieldChanged;

        _openCodeUsernameBox = new TextBox
        {
            Header = "Basic 用户名（仅 Basic 使用；密码单独输入，不会把用户名当凭据保存）",
        };
        AutomationProperties.SetAutomationId(_openCodeUsernameBox, "SettingsOpenCodeUsername");
        _openCodeUsernameBox.TextChanged += OnOpenCodeFieldChanged;

        // 单一凭据槽：Bearer 下为 token、Basic 下为密码；表头由 UpdateOpenCodeAuthUi 按模式设置。
        _openCodeTokenBox = new PasswordBox
        {
            Header = string.Empty,
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        AutomationProperties.SetAutomationId(_openCodeTokenBox, "SettingsOpenCodeToken");
        _openCodeTokenBox.PasswordChanged += OnOpenCodeTokenChanged;

        _openCodeTokenStatus = CreateSubtleText(string.Empty);

        _openCodeSaveButton = new Button { Content = "保存" };
        _openCodeSaveButton.Click += OnOpenCodeSaveClick;
        AutomationProperties.SetAutomationId(_openCodeSaveButton, "SettingsOpenCodeSave");

        // 放弃未提交编辑：还原为已保存状态；不建立连接、不写盘。默认禁用，仅在存在未提交输入后启用。
        _openCodeCancelButton = new Button { Content = "放弃修改", IsEnabled = false };
        _openCodeCancelButton.Click += OnOpenCodeCancelClick;
        AutomationProperties.SetAutomationId(_openCodeCancelButton, "SettingsOpenCodeCancel");

        _openCodeTestButton = new Button { Content = "测试连接" };
        _openCodeTestButton.Click += OnOpenCodeTestClick;
        AutomationProperties.SetAutomationId(_openCodeTestButton, "SettingsOpenCodeTest");

        var clearTokenButton = new Button { Content = "清除已保存凭据" };
        clearTokenButton.Click += OnOpenCodeClearTokenClick;
        AutomationProperties.SetAutomationId(clearTokenButton, "SettingsOpenCodeClearToken");

        _openCodeProgress = new ProgressRing { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(_openCodeSaveButton);
        buttonRow.Children.Add(_openCodeCancelButton);
        buttonRow.Children.Add(_openCodeTestButton);
        buttonRow.Children.Add(clearTokenButton);
        buttonRow.Children.Add(_openCodeProgress);

        _openCodeStatus = CreateSubtleText(string.Empty);

        panel.Children.Add(_openCodeAuthModeCombo);
        panel.Children.Add(_openCodeAuthModeHint);
        panel.Children.Add(_openCodeUrlBox);
        panel.Children.Add(_openCodeUsernameBox);
        panel.Children.Add(_openCodeTokenBox);
        panel.Children.Add(_openCodeTokenStatus);
        panel.Children.Add(buttonRow);
        panel.Children.Add(_openCodeStatus);

        // 第二入口：粘贴 V2 配对链接（兑换 → 验证 → 保存；实现见 SettingsView.OpenCodePairing.cs）。
        panel.Children.Add(BuildOpenCodePairingSection());

        // 初始表头/说明（此时下拉尚未选择，按默认 Bearer 呈现；LoadAsync 后按已保存配置刷新）。
        UpdateOpenCodeAuthUi();

        return panel;
    }

    private void ApplyOpenCodeToUi(bool preserveEdits)
    {
        // 保留未提交输入（地址/认证方式/用户名）：仅当没有暂存编辑（或显式刷新）时用持久化值覆盖。
        bool keepInputs = _openCodeDirty && preserveEdits;
        if (!keepInputs)
        {
            _openCodeUrlBox.Text = _settings.OpenCode.BaseUrl ?? string.Empty;
            _openCodeAuthModeCombo.SelectedIndex = AuthModeToIndex(_settings.OpenCode.AuthenticationMode);
            _openCodeUsernameBox.Text = _settings.OpenCode.Username ?? string.Empty;
            if (!preserveEdits)
            {
                // 赋值会触发 TextChanged/SelectionChanged 处理器重算；显式刷新后复位，避免“已保存”状态被误标为未保存。
                _openCodeDirty = false;
            }
        }

        UpdateOpenCodeAuthUi();

        _openCodeTokenStatus.Text = _settings.OpenCode.CredentialId is null
            ? "未保存后端凭据（token/密码）。"
            : $"已保存后端凭据（{DescribeAuthMode(_settings.OpenCode.AuthenticationMode)}；不回显明文）。";

        UpdateOpenCodeDirtyState();
        UpdatePairingUiState();
    }

    /// <summary>
    /// OpenCode 分区是否存在未提交输入：当前界面输入与已保存配置存在差异（地址/认证方式/用户名/
    /// 新凭据/配对链接）或存在已兑换未保存的配对结果（仅内存）。按当前状态实时计算，不依赖缓存标记。
    /// </summary>
    private bool HasOpenCodePendingInput => ComputeOpenCodeDirty() || _pairingResult is not null;

    /// <summary>
    /// 按当前界面输入与已保存配置重算是否存在未提交差异（绝不读取或比较已存明文秘密）：
    /// 地址与用户名按保存语义去除两端空白后比较；认证方式按呈现映射比较（未定义值本就按 Bearer
    /// 呈现，不因此误标，也不自动改写已存配置）；新输入的凭据仅判断非空，配对链接输入与已兑换
    /// 未保存的结果视为待提交。重算基于当前控件值而非事件载荷：迟到的 TextChanged /
    /// SelectionChanged / PasswordChanged 重算后与当前输入一致，不会把已还原或已保存的状态误标为
    /// 未保存，也不会忽略真实新编辑。
    /// </summary>
    private bool ComputeOpenCodeDirty()
    {
        // 新输入的凭据（明文不读取、不比较；保存/放弃/清除后即清空，非空必为新输入）与配对暂存输入。
        if (_openCodeTokenBox.Password.Length > 0 ||
            !string.IsNullOrWhiteSpace(_openCodePairLinkBox.Text))
        {
            return true;
        }

        string url = (_openCodeUrlBox.Text ?? string.Empty).Trim();
        string savedUrl = (_settings.OpenCode.BaseUrl ?? string.Empty).Trim();
        if (!string.Equals(url, savedUrl, StringComparison.Ordinal))
        {
            return true;
        }

        if (AuthModeToIndex(AuthModeFromIndex(_openCodeAuthModeCombo.SelectedIndex)) !=
            AuthModeToIndex(_settings.OpenCode.AuthenticationMode))
        {
            return true;
        }

        string username = (_openCodeUsernameBox.Text ?? string.Empty).Trim();
        string savedUsername = (_settings.OpenCode.Username ?? string.Empty).Trim();
        return !string.Equals(username, savedUsername, StringComparison.Ordinal);
    }

    /// <summary>重算暂存标记并同步“放弃修改”按钮可用态（输入事件到达时调用）。</summary>
    private void RecomputeOpenCodeDirty()
    {
        _openCodeDirty = ComputeOpenCodeDirty();
        UpdateOpenCodeDirtyState();
    }

    /// <summary>同步“放弃修改”按钮可用态与当前暂存输入状态。</summary>
    private void UpdateOpenCodeDirtyState()
    {
        if (_openCodeCancelButton is not null)
        {
            _openCodeCancelButton.IsEnabled = HasOpenCodePendingInput;
        }
    }

    /// <summary>按当前认证方式选择更新字段可见性与说明文本（不改动输入内容）。</summary>
    private void UpdateOpenCodeAuthUi()
    {
        OpenCodeAuthenticationMode mode = AuthModeFromIndex(_openCodeAuthModeCombo.SelectedIndex);

        _openCodeAuthModeHint.Text = mode switch
        {
            OpenCodeAuthenticationMode.Basic =>
                "Basic：请求发送 Basic Base64(用户名:密码)；密码经 DPAPI 密文保存（配置只存 CredentialId，不存明文）。",
            OpenCodeAuthenticationMode.None =>
                "None：不发送任何认证头（忽略已存用户名/凭据；如填写凭据仍会安全保存但不发送）。",
            _ =>
                "Bearer（旧配置兼容）：请求发送 Authorization: Bearer <token>；无 token 时不发送认证头。" +
                "服务密码不会自动当作 Bearer 发送，也不根据 401 自动切换认证方式。",
        };

        _openCodeTokenBox.Header = mode switch
        {
            OpenCodeAuthenticationMode.Basic => "密码（Basic；密码型；留空保存则保留现有凭据）",
            OpenCodeAuthenticationMode.None => "后端凭据（None 下不发送；填写仍会安全保存）",
            _ => "后端 token（Bearer；密码型；留空保存则保留现有凭据）",
        };

        _openCodeUsernameBox.Visibility = mode == OpenCodeAuthenticationMode.Basic
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static int AuthModeToIndex(OpenCodeAuthenticationMode mode) => mode switch
    {
        OpenCodeAuthenticationMode.Basic => 0,
        OpenCodeAuthenticationMode.None => 2,
        _ => 1, // Bearer 与任何未定义值按 Bearer 呈现（不自动改写已存配置）
    };

    private static OpenCodeAuthenticationMode AuthModeFromIndex(int index) => index switch
    {
        0 => OpenCodeAuthenticationMode.Basic,
        2 => OpenCodeAuthenticationMode.None,
        _ => OpenCodeAuthenticationMode.Bearer,
    };

    private static string DescribeAuthMode(OpenCodeAuthenticationMode mode) => mode switch
    {
        OpenCodeAuthenticationMode.Basic => "Basic",
        OpenCodeAuthenticationMode.None => "None",
        _ => "Bearer",
    };

    private void OnOpenCodeAuthModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressUiEvents)
        {
            RecomputeOpenCodeDirty();
        }

        UpdateOpenCodeAuthUi();
    }

    private void OnOpenCodeFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressUiEvents)
        {
            RecomputeOpenCodeDirty();
        }
    }

    private void OnOpenCodeTokenChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressUiEvents)
        {
            RecomputeOpenCodeDirty();
        }
    }

    private void SetOpenCodeStatus(string message) => _openCodeStatus.Text = message;

    /// <summary>
    /// 放弃未提交的 OpenCode 输入（地址/认证方式/用户名/凭据/配对链接与已兑换未保存的结果）：
    /// 还原为已持久化状态并清除暂存标记。不写盘、不建立连接；仅在确有未保存输入时生效。
    /// </summary>
    private void OnOpenCodeCancelClick(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        if (!HasOpenCodePendingInput)
        {
            SetOpenCodeStatus("没有未保存的修改。");
            return;
        }

        bool hadPairingResult = _pairingResult is not null;
        ResetOpenCodeInputs();
        SetOpenCodeStatus(hadPairingResult
            ? "已放弃未保存的修改；已兑换未保存的配对结果已丢弃（如需继续请获取新链接）。"
            : "已放弃未保存的修改（地址、认证方式与凭据输入已还原为已保存状态）。");
    }

    /// <summary>还原 OpenCode 分区全部暂存输入（含配对链接与内存中的兑换结果）；不写盘、不建立连接。</summary>
    private void ResetOpenCodeInputs()
    {
        _openCodeTokenBox.Password = string.Empty;
        ClearPairLinkBox();
        _pairingResult = null;
        _openCodeDirty = false;
        ApplyOpenCodeToUi(preserveEdits: false);
    }

    private async void OnOpenCodeSaveClick(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        await TrySaveOpenCodeAsync();
    }

    /// <summary>
    /// 提交暂存的 OpenCode 配置（地址 / 认证方式 / 用户名 / 凭据）。成功返回 <c>true</c>；
    /// 校验或写入失败返回 <c>false</c>（界面保留，不默默丢失）。
    /// 若存在“已兑换未保存”的配对结果，则只重试“验证 + 保存”该结果，绝不重新兑换（避免再次消耗一次性兑换码）。
    /// “保存”只写入配置，不建立连接；成功提示绝不暗示已连接。
    /// </summary>
    private async Task<bool> TrySaveOpenCodeAsync()
    {
        if (_pairingResult is not null)
        {
            // 已兑换未保存的配对结果：走配对入口的显式重试（单飞、独立 CTS、不重新兑换）。
            return await RetryCommitPairingResultAsync();
        }

        string url = _openCodeUrlBox.Text?.Trim() ?? string.Empty;
        if (url.Length > 0 && !IsAbsoluteHttpUrl(url))
        {
            SetOpenCodeStatus("后端地址必须是绝对的 http/https URL，且不含 userinfo、query 或 fragment。");
            return false;
        }

        // 配对链接尚未兑换：不自动兑换（一次性兑换码），要求用户显式操作或放弃。
        if (!string.IsNullOrWhiteSpace(_openCodePairLinkBox.Text))
        {
            SetOpenCodeStatus("配对链接尚未兑换：请点击“兑换并连接”，或使用“放弃修改”。本页不会自动兑换（避免消耗一次性兑换码）。");
            return false;
        }

        OpenCodeAuthenticationMode mode = AuthModeFromIndex(_openCodeAuthModeCombo.SelectedIndex);
        string username = _openCodeUsernameBox.Text?.Trim() ?? string.Empty;
        string secret = _openCodeTokenBox.Password;
        bool modeChanged = mode != _settings.OpenCode.AuthenticationMode;
        string? credentialId = _settings.OpenCode.CredentialId;

        if (mode == OpenCodeAuthenticationMode.Basic)
        {
            if (username.Length == 0)
            {
                SetOpenCodeStatus("Basic 认证需要用户名。");
                return false;
            }

            if (username.IndexOf(':') >= 0 || username.Contains('\r') || username.Contains('\n'))
            {
                SetOpenCodeStatus("Basic 用户名不能包含冒号或换行符。");
                return false;
            }
        }

        // 仅有凭据、无地址：无法建立连接；明确拒绝，而不是静默写入一条孤立凭据。
        if (url.Length == 0 && secret.Length > 0)
        {
            SetOpenCodeStatus("请先填写后端地址（绝对 http/https）；仅保存凭据不会建立连接。");
            return false;
        }

        // 切换认证方式且未输入新凭据：拒绝，不把旧 token 静默当作新模式的密码/token。
        if (modeChanged && secret.Length == 0 && mode != OpenCodeAuthenticationMode.None && credentialId is not null)
        {
            SetOpenCodeStatus("切换认证方式需要输入对应的凭据（Basic 密码 / Bearer token）；留空保留仅适用于认证方式不变时。");
            return false;
        }

        if (mode == OpenCodeAuthenticationMode.Basic && secret.Length == 0 && credentialId is null)
        {
            SetOpenCodeStatus("Basic 认证需要密码：请输入密码后保存（密码只写入 DPAPI 密文存储）。");
            return false;
        }

        if (secret.Length > 0)
        {
            try
            {
                await _secretStore.SetAsync(OpenCodeCredentialId, secret, _lifetimeCts.Token);
                credentialId = OpenCodeCredentialId;
            }
            catch (OperationCanceledException)
            {
                // 取消不是保存失败：不写入、不改变界面，仅如实说明。
                SetOpenCodeStatus("凭据保存已取消（未写入；取消不是保存失败）。");
                return false;
            }
            catch (Exception ex)
            {
                // 明确阶段（凭据保存），只展示安全类型 / 阶段 / HRESULT，绝不输出密钥。
                SetOpenCodeStatus("凭据保存失败：" + DescribeSafeError(ex));
                return false;
            }
        }

        // 显式持久化认证方式与用户名（需求 D：两字段已有模型支持）；地址/凭据沿用既有语义
        // （空地址=清除地址；凭据留空=保留现有凭据）。
        var openCode = _settings.OpenCode with
        {
            BaseUrl = url.Length == 0 ? null : url,
            CredentialId = credentialId,
            AuthenticationMode = mode,
            Username = mode == OpenCodeAuthenticationMode.Basic ? username : _settings.OpenCode.Username,
        };

        string successMessage = url.Length == 0
            ? "已清除 OpenCode 后端地址（凭据保留）。"
            : $"已保存 OpenCode 配置（认证方式：{DescribeAuthMode(mode)}）。";

        if (await SaveAsync(_settings with { OpenCode = openCode }, successMessage))
        {
            _openCodeTokenBox.Password = string.Empty; // 不保留明文
            _openCodeDirty = false;
            ApplyOpenCodeToUi(preserveEdits: false);

            // 明确下一步：本页不建立连接，真正的连接发生在 OpenCode 页面（消费同一共享设置）。
            SetOpenCodeStatus(url.Length == 0
                ? "后端地址已清除；OpenCode 页面将显示“未配置”。如需删除已存凭据，请用“清除已保存凭据”。"
                : "已保存。本页不建立连接——前往 OpenCode 页面即使用该地址与认证方式连接（该页已打开时，重新进入以应用新配置）。");

            // 凭据已真实写入 DPAPI 密文（即使 CredentialId 不变）：显式刷新后台 owner，使其重读凭据。
            if (secret.Length > 0)
            {
                RaiseOpenCodeCredentialsChanged();
            }

            return true;
        }

        return false;
    }

    private async void OnOpenCodeClearTokenClick(object sender, RoutedEventArgs e)
    {
        if (_settings.OpenCode.CredentialId is null && _openCodeTokenBox.Password.Length == 0)
        {
            SetOpenCodeStatus("当前没有已保存的后端凭据。");
            return;
        }

        bool confirmed = await ConfirmAsync(
            "清除已保存凭据",
            "将从 DPAPI 密文存储中删除 OpenCode 后端凭据（Bearer token / Basic 密码）。后端地址与认证方式保留。",
            "清除");
        if (!confirmed)
        {
            return;
        }

        if (_settings.OpenCode.CredentialId is not null)
        {
            try
            {
                await _secretStore.RemoveAsync(_settings.OpenCode.CredentialId, _lifetimeCts.Token);
            }
            catch (Exception ex)
            {
                SetOpenCodeStatus("清除凭据失败：" + DescribeSafeError(ex));
                return;
            }
        }

        _openCodeTokenBox.Password = string.Empty;
        var openCode = _settings.OpenCode with { CredentialId = null };
        if (await SaveAsync(_settings with { OpenCode = openCode }, "已清除后端凭据。"))
        {
            _openCodeDirty = false;
            ApplyOpenCodeToUi(preserveEdits: false);

            // 凭据已被真实清除：显式刷新后台 owner，使其不再使用旧凭据的客户端。
            RaiseOpenCodeCredentialsChanged();
        }
    }

    /// <summary>
    /// 测试连接（只读 GET api/location，沿用 30 秒超时；不发 prompt、不自动重试）。
    /// 使用界面当前输入（地址/认证方式/用户名/凭据）；凭据留空且认证方式未变时读取已保存凭据。
    /// 并发与生命周期：单飞（进行中忽略再次点击并提示）、按钮禁用、独立 CTS（卸载时取消）、
    /// 代际隔离（旧代际的完成不再写界面），所有完成/失败/取消路径都结束 spinner。
    /// 凭据读取失败、Basic 缺密码或取消时停止测试并给出可诊断但无秘密的提示，绝不降级为匿名请求。
    /// </summary>
    private async void OnOpenCodeTestClick(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _unloaded)
        {
            return;
        }

        // 单飞：已有测试进行中则忽略本次点击（按钮本已禁用，此处为键盘/程序化触发的兜底）。
        if (_openCodeTestCts is not null)
        {
            SetOpenCodeStatus("已有一次连接测试在进行中，请等待其结束。");
            return;
        }

        string url = _openCodeUrlBox.Text?.Trim() ?? string.Empty;
        if (!IsAbsoluteHttpUrl(url))
        {
            SetOpenCodeStatus("测试前请先填写合法的后端地址（绝对 http/https，不含 query/fragment）。");
            return;
        }

        OpenCodeAuthenticationMode mode = AuthModeFromIndex(_openCodeAuthModeCombo.SelectedIndex);
        string username = _openCodeUsernameBox.Text?.Trim() ?? string.Empty;
        if (mode == OpenCodeAuthenticationMode.Basic)
        {
            if (username.Length == 0)
            {
                SetOpenCodeStatus("Basic 认证需要用户名，测试已停止。");
                return;
            }

            if (username.IndexOf(':') >= 0 || username.Contains('\r') || username.Contains('\n'))
            {
                SetOpenCodeStatus("Basic 用户名不能包含冒号或换行符，测试已停止。");
                return;
            }
        }

        int generation = ++_openCodeTestGeneration;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _openCodeTestCts = cts;
        _openCodeTestButton.IsEnabled = false;
        _openCodeProgress.Visibility = Visibility.Visible;
        _openCodeProgress.IsActive = true;
        SetOpenCodeStatus("正在测试 / 加载…");

        try
        {
            string? secret;
            try
            {
                if (_openCodeTokenBox.Password.Length > 0)
                {
                    secret = _openCodeTokenBox.Password;
                }
                else if (mode == _settings.OpenCode.AuthenticationMode &&
                         _settings.OpenCode.CredentialId is { } credentialId)
                {
                    secret = await ResolveOpenCodeTokenAsync(credentialId, cts.Token);
                }
                else
                {
                    // 认证方式已切换且未输入新凭据：不使用旧凭据（避免把旧 token/密码错当新模式凭据）。
                    secret = null;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 交由外层取消分支处理（测试已取消）。
            }
            catch (Exception ex)
            {
                // 凭据读取失败：停止测试，给出可诊断但无秘密的提示；不降级为匿名请求。
                SetOpenCodeTestStatus(generation, "无法读取已保存的凭据，测试已停止：" + DescribeSafeError(ex));
                return;
            }

            if (mode == OpenCodeAuthenticationMode.Basic && string.IsNullOrEmpty(secret))
            {
                SetOpenCodeTestStatus(generation,
                    "Basic 认证需要密码：请输入密码，或先保存凭据后再测试。测试已停止（不会降级为匿名请求）。");
                return;
            }

            // 使用界面当前选择构建测试客户端（与保存后的配置语义一致）；不根据失败自动切换认证方式。
            var options = new OpenCodeClientOptions
            {
                BaseUrl = url,
                AuthenticationMode = mode,
                Username = mode == OpenCodeAuthenticationMode.Basic ? username : _settings.OpenCode.Username,
                Secret = secret,
            };
            using var client = new OpenCodeClient(options);
            await client.GetLocationAsync(null, cts.Token);
            SetOpenCodeTestStatus(generation, "连接成功（后端已响应位置查询）。");
        }
        catch (OperationCanceledException)
        {
            SetOpenCodeTestStatus(generation, "测试已取消。");
        }
        catch (OpenCodeException ex)
        {
            // OpenCode 异常消息为固定端点模板，不含 token / 响应体 / 请求头，可安全展示。
            SetOpenCodeTestStatus(generation, "连接失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            SetOpenCodeTestStatus(generation, "连接失败：" + DescribeSafeError(ex));
        }
        finally
        {
            // 无论成功、失败还是取消，都结束 spinner 并恢复按钮（卸载时不再触碰界面）。
            if (ReferenceEquals(_openCodeTestCts, cts))
            {
                _openCodeTestCts = null;
                if (!_unloaded)
                {
                    _openCodeTestButton.IsEnabled = true;
                    _openCodeProgress.IsActive = false;
                    _openCodeProgress.Visibility = Visibility.Collapsed;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>仅在未卸载且代际未变（未被取消/重启）时写入测试状态文本。</summary>
    private void SetOpenCodeTestStatus(int generation, string message)
    {
        if (_unloaded || generation != _openCodeTestGeneration)
        {
            return;
        }

        SetOpenCodeStatus(message);
    }

    /// <summary>
    /// 读取已保存的后端凭据。**不吞**凭据读取失败或取消：<see cref="SecretStoreException"/> 与
    /// <see cref="OperationCanceledException"/> 均向上抛出，由调用方停止测试，绝不降级为匿名请求。
    /// </summary>
    private Task<string?> ResolveOpenCodeTokenAsync(string credentialId, CancellationToken cancellationToken)
        => _secretStore.GetAsync(credentialId, cancellationToken);
}
