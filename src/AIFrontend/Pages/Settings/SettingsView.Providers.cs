using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

/// <summary>SettingsView 的 provider 配置分区（添加 / 编辑 / 删除 / 测试连接）。</summary>
public sealed partial class SettingsView
{
    private UIElement BuildProvidersSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(CreateSubtleText(
            "provider 的“标识 ID”同时用作其凭据存储 ID。删除 provider 会连带删除其名下模型与该密钥。" +
            "API key 仅以 DPAPI 密文保存，绝不写入 settings.json 或日志。"));

        _providerFormTitle = new TextBlock { Text = "添加 provider", FontSize = 14 };
        _providerIdBox = new TextBox
        {
            Header = "标识 ID（可选；留空自动生成，仅字母/数字/._-）",
            PlaceholderText = "如 my-openai-compatible",
        };
        _providerNameBox = new TextBox { Header = "显示名称" };
        _providerUrlBox = new TextBox
        {
            Header = "Base URL（绝对 http/https，不含 query/fragment）",
            PlaceholderText = "https://api.example.com/v1",
        };
        _providerProtocolCombo = new ComboBox { Header = "协议", MinWidth = 280 };
        _providerProtocolCombo.Items.Add("Responses（OpenAI Responses）");
        _providerProtocolCombo.Items.Add("Chat Completions（OpenAI Chat Completions）");
        _providerProtocolCombo.Items.Add("Anthropic Messages");
        _providerProtocolCombo.SelectedIndex = 0;
        _providerKeyBox = new PasswordBox
        {
            Header = "API key（可选；编辑时留空则保留现有密钥）",
            PasswordRevealMode = PasswordRevealMode.Peek,
        };

        AutomationProperties.SetAutomationId(_providerIdBox, "SettingsProviderId");
        AutomationProperties.SetAutomationId(_providerNameBox, "SettingsProviderName");
        AutomationProperties.SetAutomationId(_providerUrlBox, "SettingsProviderUrl");
        AutomationProperties.SetAutomationId(_providerProtocolCombo, "SettingsProviderProtocol");
        AutomationProperties.SetAutomationId(_providerKeyBox, "SettingsProviderKey");

        _providerSubmitButton = new Button { Content = "添加" };
        _providerSubmitButton.Click += OnProviderSubmitClick;
        AutomationProperties.SetAutomationId(_providerSubmitButton, "SettingsProviderSubmit");

        _providerCancelButton = new Button { Content = "取消编辑", Visibility = Visibility.Collapsed };
        _providerCancelButton.Click += OnProviderCancelClick;

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(_providerSubmitButton);
        buttonRow.Children.Add(_providerCancelButton);

        _providerFormStatus = CreateSubtleText(string.Empty);

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(_providerFormTitle);
        form.Children.Add(_providerIdBox);
        form.Children.Add(_providerNameBox);
        form.Children.Add(_providerUrlBox);
        form.Children.Add(_providerProtocolCombo);
        form.Children.Add(_providerKeyBox);
        form.Children.Add(buttonRow);
        form.Children.Add(_providerFormStatus);

        _providerFormExpander = new Expander
        {
            Header = "添加 / 编辑 provider",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = form,
        };
        panel.Children.Add(_providerFormExpander);

        _providerListPanel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_providerListPanel);

        _providerListStatus = CreateSubtleText(string.Empty);
        panel.Children.Add(_providerListStatus);

        return panel;
    }

    private void ApplyProvidersToUi()
    {
        _providerListPanel.Children.Clear();

        if (_settings.Providers.Count == 0)
        {
            _providerListPanel.Children.Add(CreateSubtleText("尚未配置任何 provider。"));
            return;
        }

        foreach (ProviderEndpoint provider in _settings.Providers)
        {
            _providerListPanel.Children.Add(BuildProviderRow(provider));
        }
    }

    private UIElement BuildProviderRow(ProviderEndpoint provider)
    {
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = provider.DisplayName, FontSize = 14 });
        info.Children.Add(CreateSubtleText(
            $"ID：{provider.Id}　协议：{DescribeProtocol(provider.Protocol)}　密钥：{(provider.CredentialId is null ? "无" : "已保存")}"));
        info.Children.Add(CreateSubtleText(provider.BaseUrl));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var testButton = new Button { Content = "测试连接", Tag = provider };
        testButton.Click += OnProviderTestClick;
        var editButton = new Button { Content = "编辑", Tag = provider };
        editButton.Click += OnProviderEditClick;
        var clearButton = new Button
        {
            Content = "清除密钥",
            Tag = provider,
            IsEnabled = provider.CredentialId is not null,
            Visibility = provider.CredentialId is null ? Visibility.Collapsed : Visibility.Visible,
        };
        clearButton.Click += OnProviderClearKeyClick;
        var deleteButton = new Button { Content = "删除", Tag = provider };
        deleteButton.Click += OnProviderDeleteClick;

        buttons.Children.Add(testButton);
        buttons.Children.Add(editButton);
        buttons.Children.Add(clearButton);
        buttons.Children.Add(deleteButton);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(info, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(info);
        row.Children.Add(buttons);

        return row;
    }

    private void StartEditProvider(ProviderEndpoint provider)
    {
        _editingProviderId = provider.Id;
        _providerIdBox.Text = provider.Id;
        _providerIdBox.IsEnabled = false;
        _providerNameBox.Text = provider.DisplayName;
        _providerUrlBox.Text = provider.BaseUrl;
        _providerProtocolCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ProtocolOrder, provider.Protocol));
        _providerKeyBox.Password = string.Empty;
        _providerFormTitle.Text = $"编辑 provider：{provider.DisplayName}";
        _providerSubmitButton.Content = "保存修改";
        _providerCancelButton.Visibility = Visibility.Visible;
        SetFormStatus("留空 API key 将保留现有密钥；如需删除请使用列表中的“清除密钥”。");
        _providerFormExpander.IsExpanded = true;
    }

    private void ResetProviderForm()
    {
        _editingProviderId = null;
        _providerIdBox.Text = string.Empty;
        _providerIdBox.IsEnabled = true;
        _providerNameBox.Text = string.Empty;
        _providerUrlBox.Text = string.Empty;
        _providerProtocolCombo.SelectedIndex = 0;
        _providerKeyBox.Password = string.Empty;
        _providerFormTitle.Text = "添加 provider";
        _providerSubmitButton.Content = "添加";
        _providerCancelButton.Visibility = Visibility.Collapsed;
        SetFormStatus(string.Empty);
    }

    private void SetFormStatus(string message) => _providerFormStatus.Text = message;

    /// <summary>provider 表单是否存在未提交的输入（新增表单任一字段非空；编辑表单字段偏离被编辑项或填了新密钥）。</summary>
    private bool IsProviderFormDirty()
    {
        if (_providerIdBox is null)
        {
            return false;
        }

        if (_editingProviderId is null)
        {
            return !string.IsNullOrEmpty(_providerIdBox.Text)
                || !string.IsNullOrEmpty(_providerNameBox.Text)
                || !string.IsNullOrEmpty(_providerUrlBox.Text)
                || _providerKeyBox.Password.Length > 0;
        }

        ProviderEndpoint? provider = _settings.FindProvider(_editingProviderId);
        if (provider is null)
        {
            return false;
        }

        ChatProtocol protocol = ProtocolOrder[Math.Clamp(_providerProtocolCombo.SelectedIndex, 0, ProtocolOrder.Length - 1)];
        return !string.Equals(_providerNameBox.Text?.Trim(), provider.DisplayName, StringComparison.Ordinal)
            || !string.Equals(_providerUrlBox.Text?.Trim(), provider.BaseUrl, StringComparison.Ordinal)
            || protocol != provider.Protocol
            || _providerKeyBox.Password.Length > 0;
    }

    private async void OnProviderSubmitClick(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        await TrySubmitProviderAsync();
    }

    /// <summary>提交 provider 表单（新增或编辑）。成功返回 <c>true</c> 并复位表单；校验/保存失败返回 <c>false</c>（表单保留）。</summary>
    private async Task<bool> TrySubmitProviderAsync()
    {
        string displayName = _providerNameBox.Text?.Trim() ?? string.Empty;
        string baseUrl = _providerUrlBox.Text?.Trim() ?? string.Empty;
        string idInput = _providerIdBox.Text?.Trim() ?? string.Empty;
        string keyInput = _providerKeyBox.Password;
        ChatProtocol protocol = ProtocolOrder[Math.Clamp(_providerProtocolCombo.SelectedIndex, 0, ProtocolOrder.Length - 1)];
        bool editing = _editingProviderId is not null;

        if (displayName.Length == 0)
        {
            SetFormStatus("请填写显示名称。");
            return false;
        }

        if (!IsAbsoluteHttpUrl(baseUrl))
        {
            SetFormStatus("Base URL 必须是绝对的 http/https 地址，且不含 userinfo、query 或 fragment。");
            return false;
        }

        string id;
        string? credentialId;
        if (editing)
        {
            id = _editingProviderId!;
            credentialId = _settings.FindProvider(id)?.CredentialId;
        }
        else
        {
            id = idInput.Length > 0 ? idInput : GenerateProviderId();
            if (!IsValidStorageId(id))
            {
                SetFormStatus("标识 ID 非法：1-64 个字符，仅字母/数字/'.'/'_'/'-'，且不能是 Windows 保留设备名。");
                return false;
            }

            if (_settings.FindProvider(id) is not null)
            {
                SetFormStatus($"标识 ID '{id}' 已存在，请换一个。");
                return false;
            }

            credentialId = null;
        }

        if (keyInput.Length > 0)
        {
            try
            {
                await _secretStore.SetAsync(id, keyInput, _lifetimeCts.Token);
                credentialId = id;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                // 明确阶段（密钥保存），只展示安全类型 / 阶段 / HRESULT。
                SetFormStatus("密钥保存失败：" + DescribeSafeError(ex));
                return false;
            }
        }

        var endpoint = new ProviderEndpoint
        {
            Id = id,
            DisplayName = displayName,
            BaseUrl = baseUrl,
            Protocol = protocol,
            CredentialId = credentialId,
        };

        if (await SaveAsync(settings => settings with
        {
            Providers = editing
                ? settings.Providers.Select(p => p.Id == id ? endpoint : p).ToArray()
                : settings.Providers.Append(endpoint).ToArray(),
        }, editing ? "已更新 provider。" : "已添加 provider。"))
        {
            ResetProviderForm();
            return true;
        }

        return false;
    }

    private void OnProviderCancelClick(object sender, RoutedEventArgs e) => ResetProviderForm();

    private void OnProviderEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProviderEndpoint provider })
        {
            StartEditProvider(provider);
        }
    }

    private async void OnProviderDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProviderEndpoint provider })
        {
            return;
        }

        bool confirmed = await ConfirmAsync(
            "删除 provider",
            $"将删除 provider“{provider.DisplayName}”及其名下全部模型；已保存的 API key 密文也会一并清除。此操作不可撤销。",
            "删除");
        if (!confirmed)
        {
            return;
        }

        if (provider.CredentialId is not null)
        {
            try
            {
                await _secretStore.RemoveAsync(provider.CredentialId, _lifetimeCts.Token);
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Warning, "provider 已删除，但密钥清除失败", DescribeSafeError(ex));
            }
        }

        await SaveAsync(
            settings =>
            {
                ProviderEndpoint[] remainingProviders = settings.Providers.Where(p => p.Id != provider.Id).ToArray();
                ModelDescriptor[] remainingModels = settings.Models.Where(m => m.ProviderId != provider.Id).ToArray();
                ModelRef? selected = settings.SelectedModel is { } sel &&
                    remainingModels.Any(m => m.ProviderId == sel.ProviderId && m.ModelId == sel.ModelId)
                    ? settings.SelectedModel
                    : null;
                return settings with { Providers = remainingProviders, Models = remainingModels, SelectedModel = selected };
            },
            "已删除 provider。");
    }

    private async void OnProviderClearKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProviderEndpoint provider } || provider.CredentialId is null)
        {
            return;
        }

        bool confirmed = await ConfirmAsync(
            "清除密钥",
            $"将从 DPAPI 密文存储中删除 provider“{provider.DisplayName}”的 API key。provider 配置本身保留。",
            "清除");
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _secretStore.RemoveAsync(provider.CredentialId, _lifetimeCts.Token);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "清除密钥失败", DescribeSafeError(ex));
            return;
        }

        await SaveAsync(settings => settings with
        {
            Providers = settings.Providers
                .Select(p => p.Id == provider.Id ? p with { CredentialId = null } : p)
                .ToArray(),
        }, "已清除该 provider 的密钥。");
    }

    private async void OnProviderTestClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProviderEndpoint provider })
        {
            return;
        }

        _providerListStatus.Text = $"正在测试连接：{provider.DisplayName}…";
        try
        {
            string? apiKey = await ResolveApiKeyAsync(provider, _lifetimeCts.Token);
            IReadOnlyList<ModelDescriptor> models = await _modelCatalog.ListModelsAsync(provider, apiKey, _lifetimeCts.Token);
            if (_unloaded)
            {
                return;
            }

            _providerListStatus.Text = $"连接成功：{provider.DisplayName} 返回 {models.Count} 个模型。";
        }
        catch (OperationCanceledException)
        {
            if (!_unloaded)
            {
                _providerListStatus.Text = "测试已取消。";
            }
        }
        catch (Exception ex)
        {
            if (!_unloaded)
            {
                _providerListStatus.Text = $"连接失败：{DescribeSafeError(ex)}";
            }
        }
    }
}
