using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

/// <summary>SettingsView 的模型分区（选中、手动添加/删除、发现加入、能力编辑）。</summary>
public sealed partial class SettingsView
{
    private UIElement BuildModelsSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(CreateSubtleText(
            "模型条目全部来自手动添加或 /models 发现；不猜测具体模型名。发现请求只在点击“发现模型”时发出，" +
            "不会自动访问任意后端。能力未知时不会默认发送可选思考参数。"));

        // 内联能力编辑器宿主：点击“能力设置”时就地展开，不使用模态 ContentDialog。
        _capabilityEditorHost = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        panel.Children.Add(_capabilityEditorHost);

        _selectedModelCombo = new ComboBox { Header = "当前选中模型", MinWidth = 320 };
        AutomationProperties.SetAutomationId(_selectedModelCombo, "SettingsSelectedModel");
        _selectedModelCombo.SelectionChanged += OnSelectedModelChanged;
        panel.Children.Add(_selectedModelCombo);

        panel.Children.Add(BuildManualModelForm());
        panel.Children.Add(BuildDiscoverForm());

        _modelListPanel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_modelListPanel);

        return panel;
    }

    private UIElement BuildManualModelForm()
    {
        _modelProviderCombo = new ComboBox { Header = "provider", MinWidth = 280 };
        _modelIdBox = new TextBox { Header = "模型 ID（原样传给 provider）" };
        _modelDisplayNameBox = new TextBox { Header = "展示名（可选）" };
        _modelFormStatus = CreateSubtleText(string.Empty);

        var addButton = new Button { Content = "手动添加" };
        addButton.Click += OnModelAddClick;
        AutomationProperties.SetAutomationId(addButton, "SettingsModelAdd");

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(_modelProviderCombo);
        form.Children.Add(_modelIdBox);
        form.Children.Add(_modelDisplayNameBox);
        form.Children.Add(addButton);
        form.Children.Add(_modelFormStatus);

        return new Expander
        {
            Header = "手动添加模型",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = form,
        };
    }

    private UIElement BuildDiscoverForm()
    {
        _discoverProviderCombo = new ComboBox { Header = "provider", MinWidth = 280 };
        _discoverButton = new Button { Content = "发现模型" };
        _discoverButton.Click += OnDiscoverClick;
        AutomationProperties.SetAutomationId(_discoverButton, "SettingsDiscoverModels");

        _discoverProgress = new ProgressRing { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
        _discoverStatus = CreateSubtleText(string.Empty);
        _discoverResultsPanel = new StackPanel { Spacing = 4 };
        _discoverAddButton = new Button { Content = "加入所选", IsEnabled = false };
        _discoverAddButton.Click += OnDiscoverAddClick;
        AutomationProperties.SetAutomationId(_discoverAddButton, "SettingsDiscoverAdd");

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(_discoverButton);
        headerRow.Children.Add(_discoverProgress);

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(_discoverProviderCombo);
        form.Children.Add(new TextBlock
        {
            Text = "点击“发现模型”会向所选 provider 的 /v1/models 发出一次请求（使用其已保存密钥）。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        form.Children.Add(headerRow);
        form.Children.Add(_discoverStatus);
        form.Children.Add(_discoverResultsPanel);
        form.Children.Add(_discoverAddButton);

        return new Expander
        {
            Header = "发现模型（GET models）",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = form,
        };
    }

    private void ApplyModelsToUi()
    {
        FillProviderCombo(_modelProviderCombo, null);
        FillProviderCombo(_discoverProviderCombo, null);

        _selectedModelCombo.Items.Clear();
        _selectedModelCombo.Items.Add("（未选择）");
        foreach (ModelDescriptor model in _settings.Models)
        {
            _selectedModelCombo.Items.Add($"{model.DisplayName ?? model.ModelId}（{model.ProviderId}/{model.ModelId}）");
        }

        int selected = -1;
        if (_settings.SelectedModel is { } reference)
        {
            selected = IndexOfModel(reference.ProviderId, reference.ModelId);
        }

        _selectedModelCombo.SelectedIndex = selected >= 0 ? selected + 1 : 0;
        _selectedModelCombo.IsEnabled = _settings.Models.Count > 0;

        RebuildModelList();
    }

    private int IndexOfModel(string providerId, string modelId)
    {
        for (int i = 0; i < _settings.Models.Count; i++)
        {
            ModelDescriptor model = _settings.Models[i];
            if (model.ProviderId == providerId && model.ModelId == modelId)
            {
                return i;
            }
        }

        return -1;
    }

    private void RebuildModelList()
    {
        _modelListPanel.Children.Clear();

        if (_settings.Models.Count == 0)
        {
            _modelListPanel.Children.Add(CreateSubtleText("尚无模型条目。"));
            return;
        }

        foreach (ModelDescriptor model in _settings.Models)
        {
            _modelListPanel.Children.Add(BuildModelRow(model));
        }
    }

    private UIElement BuildModelRow(ModelDescriptor model)
    {
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = model.DisplayName ?? model.ModelId, FontSize = 14 });
        info.Children.Add(CreateSubtleText(
            $"ProviderId：{model.ProviderId}　ModelId：{model.ModelId}　来源：{(model.Origin == ModelOrigin.Manual ? "手动" : "发现")}"));
        info.Children.Add(CreateSubtleText(DescribeCapabilities(model)));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var capabilityButton = new Button { Content = "能力设置", Tag = model };
        capabilityButton.Click += OnModelCapabilityClick;
        var deleteButton = new Button { Content = "删除", Tag = model };
        deleteButton.Click += OnModelDeleteClick;
        buttons.Children.Add(capabilityButton);
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

    private static string DescribeCapabilities(ModelDescriptor model)
    {
        ModelCapabilities capabilities = model.Capabilities ?? ModelCapabilities.Unknown;
        if (capabilities.Source == CapabilitySource.Unknown || capabilities.Reasoning is null)
        {
            return "能力：未知（不发送可选思考参数）";
        }

        ReasoningCapabilities reasoning = capabilities.Reasoning;
        string effort = reasoning.EffortValues.Count == 0 ? "不支持" : string.Join(", ", reasoning.EffortValues);
        string budget = reasoning.MinThinkingBudgetTokens is null && reasoning.MaxThinkingBudgetTokens is null
            ? "未指定"
            : $"{reasoning.MinThinkingBudgetTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"}~{reasoning.MaxThinkingBudgetTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
        string source = capabilities.Source == CapabilitySource.Manual ? "手动指定" : "发现";
        return $"能力（{source}）：effort {effort}；adaptive {(reasoning.SupportsThinkingAdaptive ? "支持" : "不支持")}；" +
               $"enabled {(reasoning.SupportsThinkingEnabled ? "支持" : "不支持")}；预算 {budget}";
    }

    private async void OnSelectedModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        ModelRef? target = _selectedModelCombo.SelectedIndex <= 0
            ? null
            : new ModelRef(
                _settings.Models[_selectedModelCombo.SelectedIndex - 1].ProviderId,
                _settings.Models[_selectedModelCombo.SelectedIndex - 1].ModelId);

        if (target == _settings.SelectedModel)
        {
            return;
        }

        await SaveAsync(settings => settings with { SelectedModel = target }, "已更新选中模型。");
    }

    /// <summary>手动添加表单是否存在未提交的输入（模型 ID 或展示名非空）。</summary>
    private bool IsModelFormDirty()
        => _modelIdBox is not null &&
           (!string.IsNullOrEmpty(_modelIdBox.Text) || !string.IsNullOrEmpty(_modelDisplayNameBox.Text));

    /// <summary>清空手动添加模型表单（不改变选中模型）。</summary>
    private void ResetModelForm()
    {
        if (_modelIdBox is null)
        {
            return;
        }

        _modelIdBox.Text = string.Empty;
        _modelDisplayNameBox.Text = string.Empty;
        _modelFormStatus.Text = string.Empty;
    }

    private async void OnModelAddClick(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        await TryAddModelAsync();
    }

    /// <summary>提交手动添加模型表单。成功返回 <c>true</c> 并清空表单；校验/保存失败返回 <c>false</c>（表单保留）。</summary>
    private async Task<bool> TryAddModelAsync()
    {
        if (_settings.Providers.Count == 0)
        {
            _modelFormStatus.Text = "请先添加 provider。";
            return false;
        }

        int index = _modelProviderCombo.SelectedIndex;
        if (index < 0 || index >= _settings.Providers.Count)
        {
            _modelFormStatus.Text = "请选择 provider。";
            return false;
        }

        ProviderEndpoint provider = _settings.Providers[index];
        string modelId = _modelIdBox.Text?.Trim() ?? string.Empty;
        string displayName = _modelDisplayNameBox.Text?.Trim() ?? string.Empty;

        if (modelId.Length == 0)
        {
            _modelFormStatus.Text = "请填写模型 ID。";
            return false;
        }

        if (_settings.FindModel(provider.Id, modelId) is not null)
        {
            _modelFormStatus.Text = $"模型“{provider.Id}/{modelId}”已存在。";
            return false;
        }

        var model = new ModelDescriptor
        {
            ProviderId = provider.Id,
            ModelId = modelId,
            DisplayName = displayName.Length == 0 ? null : displayName,
            Origin = ModelOrigin.Manual,
            Capabilities = ModelCapabilities.Unknown,
        };

        if (await SaveAsync(settings => settings with { Models = settings.Models.Append(model).ToArray() }, "已添加模型。"))
        {
            ResetModelForm();
            return true;
        }

        return false;
    }

    private async void OnModelDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModelDescriptor model })
        {
            return;
        }

        bool confirmed = await ConfirmAsync("删除模型", $"将删除模型“{model.ProviderId}/{model.ModelId}”。", "删除");
        if (!confirmed)
        {
            return;
        }

        await SaveAsync(settings =>
        {
            ModelDescriptor[] remaining = settings.Models
                .Where(m => !(m.ProviderId == model.ProviderId && m.ModelId == model.ModelId))
                .ToArray();
            ModelRef? selected = settings.SelectedModel is { } sel &&
                remaining.Any(m => m.ProviderId == sel.ProviderId && m.ModelId == sel.ModelId)
                ? settings.SelectedModel
                : null;
            return settings with { Models = remaining, SelectedModel = selected };
        }, "已删除模型。");
    }

    // ---- 发现 ----

    private async void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        if (_settings.Providers.Count == 0)
        {
            _discoverStatus.Text = "请先添加 provider。";
            return;
        }

        int index = _discoverProviderCombo.SelectedIndex;
        if (index < 0 || index >= _settings.Providers.Count)
        {
            _discoverStatus.Text = "请选择 provider。";
            return;
        }

        ProviderEndpoint provider = _settings.Providers[index];

        _discoverResultsPanel.Children.Clear();
        _discoverResults.Clear();
        _discoverChecks.Clear();
        _discoverAddButton.IsEnabled = false;
        _discoverProgress.Visibility = Visibility.Visible;
        _discoverProgress.IsActive = true;
        _discoverStatus.Text = $"正在向 {provider.DisplayName} 的 /models 发出发现请求…";

        try
        {
            string? apiKey = await ResolveApiKeyAsync(provider, _lifetimeCts.Token);
            IReadOnlyList<ModelDescriptor> discovered = await _modelCatalog.ListModelsAsync(provider, apiKey, _lifetimeCts.Token);
            if (_unloaded)
            {
                return;
            }

            ShowDiscoveryResults(provider, discovered);
        }
        catch (OperationCanceledException)
        {
            if (!_unloaded)
            {
                _discoverStatus.Text = "发现已取消。";
            }
        }
        catch (Exception ex)
        {
            if (!_unloaded)
            {
                _discoverStatus.Text = $"发现失败：{DescribeSafeError(ex)}";
            }
        }
        finally
        {
            if (!_unloaded)
            {
                _discoverProgress.IsActive = false;
                _discoverProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowDiscoveryResults(ProviderEndpoint provider, IReadOnlyList<ModelDescriptor> discovered)
    {
        int shown = 0;
        int selectable = 0;
        foreach (ModelDescriptor model in discovered)
        {
            bool exists = _settings.FindModel(model.ProviderId, model.ModelId) is not null;
            var check = new CheckBox
            {
                Content = model.DisplayName is { Length: > 0 } displayName
                    ? $"{displayName}（{model.ModelId}）"
                    : model.ModelId,
                IsChecked = !exists,
                IsEnabled = !exists,
                Tag = model,
            };
            _discoverResultsPanel.Children.Add(check);
            _discoverChecks.Add(check);
            _discoverResults.Add(model);
            shown++;
            if (!exists)
            {
                selectable++;
            }
        }

        _discoverAddButton.IsEnabled = selectable > 0;

        _discoverStatus.Text = shown == 0
            ? $"{provider.DisplayName} 未返回任何模型。"
            : $"发现 {shown} 个模型（{selectable} 个可加入；已存在的条目不可重复加入；能力保持未知，不推断支持）。";
    }

    private async void OnDiscoverAddClick(object sender, RoutedEventArgs e)
    {
        var toAdd = new List<ModelDescriptor>();
        for (int i = 0; i < _discoverResults.Count && i < _discoverChecks.Count; i++)
        {
            CheckBox check = _discoverChecks[i];
            if (check.IsEnabled && check.IsChecked == true)
            {
                toAdd.Add(_discoverResults[i] with { Origin = ModelOrigin.Discovered });
            }
        }

        if (toAdd.Count == 0)
        {
            _discoverStatus.Text = "请至少勾选一个要加入的模型。";
            return;
        }

        // 合并策略（契约 §7.4）：保留该 provider 的 Manual 条目，加入新的 Discovered 条目并去重。
        string providerId = toAdd[0].ProviderId;
        ModelDescriptor[] added = toAdd
            .Where(m => !_settings.Models.Any(o => o.ProviderId == m.ProviderId && o.ModelId == m.ModelId))
            .ToArray();

        if (await SaveAsync(settings =>
        {
            ModelDescriptor[] latestRetained = settings.Models
                .Where(m => m.ProviderId != providerId || m.Origin == ModelOrigin.Manual)
                .ToArray();
            ModelDescriptor[] latestAdded = toAdd
                .Where(m => !latestRetained.Any(o => o.ProviderId == m.ProviderId && o.ModelId == m.ModelId))
                .ToArray();
            return settings with { Models = latestRetained.Concat(latestAdded).ToArray() };
        }, $"已加入 {added.Length} 个模型。"))
        {
            _discoverResultsPanel.Children.Clear();
            _discoverResults.Clear();
            _discoverChecks.Clear();
            _discoverAddButton.IsEnabled = false;
            _discoverStatus.Text = "已加入所选模型。";
        }
    }

    // ---- 能力编辑（内联子视图，替代嵌套 ContentDialog） ----

    /// <summary>能力编辑子视图是否处于展开态（用于离开前的未保存提示）。</summary>
    private bool IsCapabilityEditorOpen()
        => _capabilityEditorHost is not null && _capabilityEditorHost.Visibility == Visibility.Visible;

    private void OnModelCapabilityClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModelDescriptor model })
        {
            ShowCapabilityEditor(model);
        }
    }

    /// <summary>
    /// 在模型分区内就地展开能力编辑子视图。不使用模态 <c>ContentDialog</c>：本控件由应用内
    /// 设置页承载，编辑一律就地内联展开，不叠加对话框。
    /// 校验在点击“保存”时进行；校验失败保持子视图打开并显示错误（等价旧对话框的 <c>args.Cancel</c>）。
    /// </summary>
    private void ShowCapabilityEditor(ModelDescriptor model)
    {
        if (_unloaded)
        {
            return;
        }

        _capabilityEditingModel = model;

        ModelCapabilities current = model.Capabilities ?? ModelCapabilities.Unknown;
        ReasoningCapabilities? reasoning = current.Reasoning;

        _capabilitySourceCombo = new ComboBox { Header = "能力来源", MinWidth = 240 };
        _capabilitySourceCombo.Items.Add("未知（不发送任何可选思考参数）");
        _capabilitySourceCombo.Items.Add("手动指定");
        _capabilitySourceCombo.SelectedIndex = current.Source == CapabilitySource.Manual ? 1 : 0;

        _capabilityEffortBox = new TextBox
        {
            Header = "effort 取值（逗号分隔；原样保存不归一化；留空表示明确不支持 effort）",
            PlaceholderText = "low, medium, high",
            Text = reasoning is null ? string.Empty : string.Join(", ", reasoning.EffortValues),
        };
        _capabilityAdaptiveCheck = new CheckBox
        {
            Content = "支持自适应思考（thinking.type = adaptive）",
            IsChecked = reasoning?.SupportsThinkingAdaptive == true,
        };
        _capabilityEnabledCheck = new CheckBox
        {
            Content = "支持显式启用思考（thinking.type = enabled + budget_tokens）",
            IsChecked = reasoning?.SupportsThinkingEnabled == true,
        };
        _capabilityMinBox = new TextBox
        {
            Header = "预算下限（可选，正整数）",
            Text = reasoning?.MinThinkingBudgetTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        };
        _capabilityMaxBox = new TextBox
        {
            Header = "预算上限（可选，正整数）",
            Text = reasoning?.MaxThinkingBudgetTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        };
        _capabilityError = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var saveButton = new Button { Content = "保存" };
        AutomationProperties.SetAutomationId(saveButton, "SettingsCapabilitySave");
        var cancelButton = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(cancelButton, "SettingsCapabilityCancel");

        saveButton.Click += async (sender, args) => await TrySaveCapabilityEditorAsync();
        cancelButton.Click += (sender, args) => CloseCapabilityEditor();

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"模型能力：{model.ProviderId}/{model.ModelId}",
            FontSize = 14,
        });
        body.Children.Add(CreateSubtleText(
            "未知能力不会默认发送可选参数；只有明确支持的值才会在发送时被采纳。预算仅适用于 Anthropic 的 enabled 思考，需勾选上方对应项。"));
        body.Children.Add(_capabilitySourceCombo);
        body.Children.Add(_capabilityEffortBox);
        body.Children.Add(_capabilityAdaptiveCheck);
        body.Children.Add(_capabilityEnabledCheck);
        body.Children.Add(_capabilityMinBox);
        body.Children.Add(_capabilityMaxBox);
        body.Children.Add(_capabilityError);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(saveButton);
        buttonRow.Children.Add(cancelButton);
        body.Children.Add(buttonRow);

        _capabilityEditorHost.Children.Clear();
        _capabilityEditorHost.Children.Add(body);
        _capabilityEditorHost.Visibility = Visibility.Visible;
        _capabilityEditorHost.StartBringIntoView();
    }

    /// <summary>
    /// 提交能力编辑子视图。校验失败时保持子视图打开并显示错误、返回 <c>false</c>；
    /// 成功（或目标条目已被外部移除）时关闭子视图并返回保存结果。
    /// </summary>
    private async Task<bool> TrySaveCapabilityEditorAsync()
    {
        if (!IsCapabilityEditorOpen() || _capabilityEditingModel is null)
        {
            return true;
        }

        ModelCapabilities result;
        if (_capabilitySourceCombo.SelectedIndex == 0)
        {
            result = ModelCapabilities.Unknown;
        }
        else
        {
            if (!TryParseReasoning(
                    _capabilityEffortBox.Text,
                    _capabilityAdaptiveCheck.IsChecked == true,
                    _capabilityEnabledCheck.IsChecked == true,
                    _capabilityMinBox.Text,
                    _capabilityMaxBox.Text,
                    out ReasoningCapabilities? parsed,
                    out string? validationError))
            {
                // 校验失败：保持子视图打开并显示错误，不写入。
                _capabilityError.Text = validationError ?? "输入无效。";
                return false;
            }

            result = new ModelCapabilities { Source = CapabilitySource.Manual, Reasoning = parsed };
        }

        ModelDescriptor editing = _capabilityEditingModel;
        CloseCapabilityEditor();
        if (_unloaded || _settings.FindModel(editing.ProviderId, editing.ModelId) is null)
        {
            // 目标条目已在外部变更中被移除：不写回陈旧数据。
            return true;
        }

        ModelDescriptor updated = editing with { Capabilities = result };
        return await SaveAsync(settings => settings with
        {
            Models = settings.Models
                .Select(m => m.ProviderId == editing.ProviderId && m.ModelId == editing.ModelId ? updated : m)
                .ToArray(),
        }, "已保存模型能力。");
    }

    private void CloseCapabilityEditor()
    {
        _capabilityEditingModel = null;
        _capabilityEditorHost.Children.Clear();
        _capabilityEditorHost.Visibility = Visibility.Collapsed;
    }

    private static bool TryParseReasoning(
        string? effortText,
        bool adaptive,
        bool enabled,
        string? minText,
        string? maxText,
        out ReasoningCapabilities? reasoning,
        out string? error)
    {
        reasoning = null;
        error = null;

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(effortText))
        {
            foreach (string part in effortText.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = part.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(value))
                {
                    error = $"effort 取值重复：'{value}'。";
                    return false;
                }

                values.Add(value);
            }
        }

        if (!TryParsePositiveInt(minText, out int? min, out error))
        {
            return false;
        }

        if (!TryParsePositiveInt(maxText, out int? max, out error))
        {
            return false;
        }

        if (min is { } lower && max is { } upper && lower > upper)
        {
            error = "预算下限不能大于上限。";
            return false;
        }

        bool hasBudget = min is not null || max is not null;
        if (hasBudget && !enabled)
        {
            error = "预算仅在勾选“支持显式启用思考（enabled）”时适用（协议层会拒绝孤立预算）。";
            return false;
        }

        reasoning = new ReasoningCapabilities
        {
            EffortValues = values.ToArray(),
            SupportsThinkingAdaptive = adaptive,
            SupportsThinkingEnabled = enabled,
            MinThinkingBudgetTokens = min,
            MaxThinkingBudgetTokens = max,
        };
        return true;
    }

    private static bool TryParsePositiveInt(string? text, out int? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
        {
            value = null;
            error = "预算必须是正整数（留空表示未指定）。";
            return false;
        }

        value = parsed;
        return true;
    }
}
