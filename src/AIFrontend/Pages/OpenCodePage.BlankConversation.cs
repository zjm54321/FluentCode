using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private bool _blankComposerPlacement;
    private OpenCodeBlankDraftPreferences? _blankPreferences;
    private readonly Dictionary<(string SourceIdentity, Guid Draft), string?> _blankCreationResults = new();
    private bool _blankPickerPending;
    private long _blankPickerEpoch;
    private string? _blankPreferenceError;
    private IReadOnlyList<OpenCodeAgentInfo> _blankAgents = Array.Empty<OpenCodeAgentInfo>();
    private IReadOnlyList<OpenCodeModelInfo> _blankModels = Array.Empty<OpenCodeModelInfo>();
    private string? _blankMetadataRuntime, _blankMetadataDirectory;
    private Guid _blankMetadataDraft;
    private bool _blankModelChosen;

    private void SetBlankComposerPlacement(bool centered)
    {
        _blankComposerPlacement = centered;
        if (!centered) Composer.MinHeight = 40; // Restore normal chrome immediately, before the queued native measure.
        UpdateConversationColumns();
        QueueComposerLayout(); // The only height writer remains UpdateComposerLayout.
    }

    private void EnsureBlankPreferences()
    {
        if (_composerController is null || !_newSessionDraftActive || !SourceReady) return;
        var draft = _composerController.Snapshot.Token.Draft;
        if (_blankPreferences is null || _blankPreferences.Draft != draft)
        {
            _blankModelChosen = false;
            var directory = _homeView.SelectedProject?.Directory ?? _homeDefaultDirectory;
            if (directory is not null) _blankPreferences = new(_homeRuntimeSource, draft, directory);
        }
        RefreshBlankPreferenceLabels();
        if (_blankPreferences is { } basis && basis.Matches(_homeRuntimeSource, draft) && !HasBlankMetadata(basis) && !_blankPickerPending)
            _ = LoadBlankMetadataAsync(basis, _composerController.Snapshot.Token);
    }

    private bool HasBlankMetadata(OpenCodeBlankDraftPreferences value) => value.RuntimeSourceId == _blankMetadataRuntime
        && value.Directory == _blankMetadataDirectory && value.Draft == _blankMetadataDraft;

    private void OwnBlankMetadata(OpenCodeBlankDraftPreferences value)
    {
        if (!HasBlankMetadata(value)) { _blankAgents = Array.Empty<OpenCodeAgentInfo>(); _blankModels = Array.Empty<OpenCodeModelInfo>(); }
        _blankMetadataRuntime = value.RuntimeSourceId; _blankMetadataDirectory = value.Directory; _blankMetadataDraft = value.Draft;
    }

    private bool OwnsBlankMetadata(OpenCodeBlankDraftPreferences basis, OpenCodeComposerToken token)
        => !_homeActive && _newSessionDraftActive && _blankPreferences == basis && _composerController is { } controller
        && controller.Snapshot.Token.Scope == token.Scope && controller.Snapshot.Token.Draft == token.Draft
        && basis.Matches(_homeRuntimeSource, token.Draft);

    private async Task LoadBlankMetadataAsync(OpenCodeBlankDraftPreferences basis, OpenCodeComposerToken token)
    {
        if (_blankPickerPending || _source is not { } source || _client is not { } client || _homeCancellation is null
            || !OwnsBlankMetadata(basis, token)) return;
        var operation = ++_blankPickerEpoch;
        _blankPickerPending = true;
        RefreshBlankPreferenceLabels();
        try
        {
            var cancellation = _homeCancellation.Token;
            var agents = await client.ListAgentsAsync(basis.Directory, cancellation);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankMetadata(basis, token)) return;
            var models = await client.ListModelsAsync(basis.Directory, cancellation);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankMetadata(basis, token)
                || agents.Location?.Directory != basis.Directory || models.Location?.Directory != basis.Directory) return;
            OwnBlankMetadata(basis);
            _blankAgents = agents.Data!; _blankModels = models.Data!;
            var configured = await client.GetConfiguredSelectionAsync(basis.Directory, cancellation);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankMetadata(basis, token)) return;
            var agent = BlankSelectedAgent(basis, configured.DefaultAgentId);
            OpenCodeModelInfo? directoryModel = null;
            if (!(_blankModelChosen && basis.Model is not null) && (agent is null || ReadBlankAgentModel(agent) is null) && configured.Model is null)
            {
                var response = await client.GetDefaultModelInfoAsync(basis.Directory, cancellation);
                if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankMetadata(basis, token) || response.Location?.Directory != basis.Directory) return;
                directoryModel = response.Data;
            }
            if (OwnsHome(source, client, basis.RuntimeSourceId) && OwnsBlankMetadata(basis, token))
                ApplyBlankDefaults(basis, configured.DefaultAgentId, configured.Model, directoryModel);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (OwnsHome(source, client, basis.RuntimeSourceId) && operation == _blankPickerEpoch) _blankPreferenceError = "目录选择信息未就绪；请检查连接后重新选择。"; }
        finally { if (operation == _blankPickerEpoch) { _blankPickerPending = false; RefreshBlankPreferenceLabels(); } }
    }

    private static string BlankAgentName(string name) => name.Length == 0 ? name : name[..1].ToUpper(CultureInfo.CurrentUICulture) + name[1..];

    private OpenCodeAgentInfo? BlankSelectedAgent(OpenCodeBlankDraftPreferences basis, string? configuredAgentId)
    {
        var selectable = _blankAgents.Where(IsBlankAgentSelectable).ToArray();
        return basis.Agent is { } explicitAgent ? selectable.FirstOrDefault(agent => agent.Id == explicitAgent)
            : selectable.FirstOrDefault(agent => agent.Id == configuredAgentId)
                ?? selectable.FirstOrDefault(agent => agent.Id == "build") ?? selectable.FirstOrDefault();
    }

    private void ApplyBlankDefaults(OpenCodeBlankDraftPreferences basis, string? configuredAgentId,
        OpenCodeModelRef? configuredModel, OpenCodeModelInfo? directoryModel)
    {
        // Server selectedDefault semantics, verified against 2.0.32: configured selectable agent,
        // then the actual selectable build entry, then the first selectable entry in the server's list.
        if (_blankPreferences != basis || !basis.Matches(_homeRuntimeSource, _composerController?.Snapshot.Token.Draft ?? Guid.Empty)) return;
        var agent = BlankSelectedAgent(basis, configuredAgentId);
        if (agent is null) { _blankPreferenceError = "当前目录的智能体未就绪，请重新选择。"; return; }
        var reference = _blankModelChosen && basis.Model is not null ? basis.Model : ReadBlankAgentModel(agent)
            ?? configuredModel ?? (directoryModel is null ? null : new OpenCodeModelRef { Id = directoryModel.Id, ProviderId = directoryModel.ProviderId });
        var model = reference is null ? null : _blankModels.SingleOrDefault(model => model.Id == reference.Id && model.ProviderId == reference.ProviderId && model.Enabled != false);
        if (model is null) { _blankPreferenceError = "当前目录的模型未就绪，请重新选择。"; return; }
        var variant = reference!.Variant;
        if (variant is not null && !(model.Variants?.Any(offered => offered.Id == variant) ?? false))
        {
            if (_blankModelChosen) { _blankPreferenceError = "已选择的强度不再可用，请重新选择。"; return; }
            variant = null; // No made-up default/high level; an absent variant is actually omitted on the wire.
        }
        _blankPreferences = basis with { Agent = agent.Id,
            Model = new() { Id = model.Id, ProviderId = model.ProviderId, Variant = variant }, Version = basis.Version + 1 };
        _blankPreferenceError = null;
    }

    private static OpenCodeModelRef? ReadBlankAgentModel(OpenCodeAgentInfo agent)
    {
        if (agent.Model is not { } raw || raw.ValueKind == JsonValueKind.Null) return null;
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()) || !raw.TryGetProperty("providerID", out var provider)
            || provider.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(provider.GetString())) throw new InvalidOperationException("Agent model metadata is unavailable.");
        string? variant = null;
        if (raw.TryGetProperty("variant", out var supplied) && supplied.ValueKind != JsonValueKind.Null)
        {
            if (supplied.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(supplied.GetString())) throw new InvalidOperationException("Agent variant metadata is unavailable.");
            variant = supplied.GetString();
        }
        return new() { Id = id.GetString(), ProviderId = provider.GetString(), Variant = variant };
    }

    private void ApplyBlankAgentColor(string? color)
    {
        BlankAgentButton.ClearValue(Control.ForegroundProperty);
        // Only an actual unambiguous RGB literal. Symbolic server tokens require a verified palette, never an agent-name guess.
        if (color is { Length: 7 } && color[0] == '#' && uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            BlankAgentButton.Foreground = new SolidColorBrush(new Windows.UI.Color { A = 255, R = (byte)(rgb >> 16), G = (byte)(rgb >> 8), B = (byte)rgb });
    }

    private static bool IsBlankAgentSelectable(OpenCodeAgentInfo agent)
        => !agent.Hidden && agent.Mode != OpenCodeAgentModes.Subagent;

    private static string BlankStrengthText(OpenCodeModelInfo? model, OpenCodeModelRef? selected)
        => selected?.Variant is { } variant && (model?.Variants?.Any(offered => offered.Id == variant) ?? false) ? variant : string.Empty;

    private void RefreshBlankPreferenceLabels()
    {
        var value = _blankPreferences;
        var current = value is not null && _composerController is not null
            && value.Matches(_homeRuntimeSource, _composerController.Snapshot.Token.Draft);
        BlankProjectButton.Content = current ? value!.Directory : "选择当前来源的项目目录";
        var metadata = current && HasBlankMetadata(value!);
        var agent = metadata ? _blankAgents.FirstOrDefault(agent => agent.Id == value!.Agent) : null;
        var model = metadata ? _blankModels.FirstOrDefault(model => model.Id == value!.Model?.Id && model.ProviderId == value.Model?.ProviderId) : null;
        BlankAgentButton.Content = agent?.Name is { Length: > 0 } name ? BlankAgentName(name) : _blankPickerPending ? "加载中…" : "未就绪";
        ApplyBlankAgentColor(agent?.Color);
        ToolTipService.SetToolTip(BlankAgentButton, agent?.Description);
        BlankModelButton.Content = new TextBlock { Text = model?.Name ?? (_blankPickerPending ? "加载中…" : "模型未就绪"), TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(BlankModelButton, model?.Name);
        var shownVariant = BlankStrengthText(model, value?.Model);
        BlankStrengthButton.Content = model is null ? "强度未就绪" : shownVariant;
        AutomationProperties.SetName(BlankStrengthButton, shownVariant.Length == 0 ? "选择推理强度" : "推理强度：" + shownVariant);
        BlankPreferencesHint.Text = !current ? "目录尚未就绪或草稿来自其他来源。请明确选择当前来源的目录；草稿不会被清空。"
            : _source is { } source && _blankCreationResults.ContainsKey((source.Identity, value!.Draft))
                ? "此草稿已发起过创建；草稿已保留，不会再次创建。请先检查首页会话列表。"
                : _blankPreferenceError ?? "选择仅用于此草稿的首次发送，不修改全局或已有会话。";
        BlankAgentButton.IsEnabled = current && !_newSessionCreatePending && !_blankPickerPending;
        BlankModelButton.IsEnabled = current && !_newSessionCreatePending && !_blankPickerPending;
        BlankStrengthButton.IsEnabled = current && model is not null && !_newSessionCreatePending && !_blankPickerPending;
        BlankProjectButton.IsEnabled = SourceReady && !_newSessionCreatePending && !_blankPickerPending;
    }

    private OpenCodeBlankDraftPreferences? CaptureBlankPreferences(OpenCodeComposerToken token)
    {
        // Exact draft/source/ABA checks precede creation. Unknown creation cannot be retried by editing its text/choices.
        if (_homeActive || _blankPickerPending || _blankPreferences is not { } value
            || !value.Matches(_homeRuntimeSource, token.Draft) || !_homeProjects.ContainsKey(value.Directory)
            || _source is not { } source || _blankCreationResults.ContainsKey((source.Identity, token.Draft)))
        {
            ShowMessage(InfoBarSeverity.Warning, "尚未创建", "请选择当前来源的项目目录；若上次创建结果未确认，请先检查会话列表。草稿已保留。");
            return null;
        }
        return value with { Model = value.Model is { } model ? model with { } : null };
    }

    private bool OwnsBlankPreferences(OpenCodeBlankDraftPreferences value, OpenCodeComposerToken token)
        => !_homeActive && _newSessionDraftActive && _composerController?.Snapshot.Token == token
        && _blankPreferences == value && value.Matches(_homeRuntimeSource, token.Draft);

    private void BlankProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_newSessionDraftActive || _newSessionCreatePending || _blankPickerPending || _composerController is null) return;
        var runtime = _homeRuntimeSource;
        var token = _composerController.Snapshot.Token;
        var flyout = new MenuFlyout();
        foreach (var project in _homeProjects.Values)
        {
            var item = new MenuFlyoutItem { Text = project.DisplayName + " · " + project.Key.Directory };
            item.Click += (_, _) =>
            {
                if (!_pageActive || !SourceReady || _homeActive || !_newSessionDraftActive || runtime != _homeRuntimeSource
                    || _newSessionCreatePending || _composerController.Snapshot.Token != token) return;
                // Explicit directory choice resets directory-scoped preferences, never draft text/files or Source.
                _blankPreferences = new(runtime, token.Draft, project.Key.Directory, Version: (_blankPreferences?.Version ?? 0) + 1);
                _blankPreferenceError = null;
                _blankModelChosen = false;
                _blankMetadataDirectory = null;
                EnsureBlankPreferences();
            };
            flyout.Items.Add(item);
        }
        if (flyout.Items.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = "没有已读取的目录，请先在首页添加", IsEnabled = false });
        flyout.ShowAt(BlankProjectButton);
    }

    private async void BlankAgentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_blankPickerPending || _newSessionCreatePending || _blankPreferences is not { } basis || _source is not { } source
            || _client is not { } client || _homeCancellation is null || _composerController is null) return;
        var token = _composerController.Snapshot.Token;
        if (!OwnsBlankPreferences(basis, token)) return;
        _blankPickerPending = true;
        var operation = ++_blankPickerEpoch;
        _blankPreferenceError = null;
        RefreshBlankPreferenceLabels();
        try
        {
            var response = await client.ListAgentsAsync(basis.Directory, _homeCancellation.Token);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token)
                || response.Location?.Directory != basis.Directory) return;
            var flyout = new MenuFlyout();
            OwnBlankMetadata(basis);
            _blankAgents = response.Data!;
            void Add(OpenCodeAgentInfo agent)
            {
                var item = new MenuFlyoutItem { Text = BlankAgentName(agent.Name!) };
                ToolTipService.SetToolTip(item, agent.Description);
                item.Click += (_, _) =>
                {
                    if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token) || _newSessionCreatePending) return;
                    _blankPreferences = basis with { Agent = agent.Id, Model = _blankModelChosen ? basis.Model : null, Version = basis.Version + 1 };
                    _blankPreferenceError = null;
                    _blankMetadataDirectory = null;
                    EnsureBlankPreferences();
                };
                flyout.Items.Add(item);
            }
            foreach (var agent in response.Data!.Where(IsBlankAgentSelectable))
                Add(agent);
            flyout.ShowAt(BlankAgentButton);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (OwnsHome(source, client, basis.RuntimeSourceId) && operation == _blankPickerEpoch) _blankPreferenceError = "智能体列表未能读取；现有选择与草稿未变。"; }
        finally { if (operation == _blankPickerEpoch) { _blankPickerPending = false; RefreshBlankPreferenceLabels(); } }
    }

    private async void BlankModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_blankPickerPending || _newSessionCreatePending || _blankPreferences is not { } basis || _source is not { } source
            || _client is not { } client || _homeCancellation is null || _composerController is null) return;
        var token = _composerController.Snapshot.Token;
        if (!OwnsBlankPreferences(basis, token)) return;
        await ChooseBlankModelAsync(basis, token);
    }

    private async void BlankStrengthButton_Click(object sender, RoutedEventArgs e)
    {
        if (_blankPickerPending || _newSessionCreatePending || _blankPreferences is not { Model: not null } basis || _source is not { } source
            || _client is not { } client || _homeCancellation is null || _composerController is null) return;
        var token = _composerController.Snapshot.Token;
        if (!OwnsBlankPreferences(basis, token)) return;
        _blankPickerPending = true;
        var operation = ++_blankPickerEpoch;
        RefreshBlankPreferenceLabels();
        try
        {
            var response = await client.ListModelsAsync(basis.Directory, _homeCancellation.Token);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token) || response.Location?.Directory != basis.Directory) return;
            var model = response.Data!.SingleOrDefault(model => model.Id == basis.Model.Id && model.ProviderId == basis.Model.ProviderId);
            if (model is null || model.Enabled == false) { _blankPreferenceError = "模型已不再可选，请重新选择。"; return; }
            OwnBlankMetadata(basis);
            _blankModels = response.Data!;
            var flyout = new MenuFlyout();
            foreach (var variant in model.Variants ?? new())
            {
                var item = new ToggleMenuFlyoutItem { Text = variant.Id, IsChecked = variant.Id == basis.Model.Variant };
                item.Click += (_, _) =>
                {
                    if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token) || _newSessionCreatePending) return;
                    _blankPreferences = basis with { Model = basis.Model with { Variant = variant.Id }, Version = basis.Version + 1 };
                    _blankModelChosen = true;
                    _blankPreferenceError = null;
                    RefreshBlankPreferenceLabels();
                };
                flyout.Items.Add(item);
            }
            if (flyout.Items.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = "没有可选强度", IsEnabled = false });
            flyout.ShowAt(BlankStrengthButton);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (OwnsHome(source, client, basis.RuntimeSourceId) && operation == _blankPickerEpoch) _blankPreferenceError = "强度信息未能读取；当前选择未变。"; }
        finally { if (operation == _blankPickerEpoch) { _blankPickerPending = false; RefreshBlankPreferenceLabels(); } }
    }

    private async Task ChooseBlankModelAsync(OpenCodeBlankDraftPreferences basis, OpenCodeComposerToken token)
    {
        if (_blankPickerPending || _newSessionCreatePending || _source is not { } source || _client is not { } client
            || _homeCancellation is null || !OwnsBlankPreferences(basis, token)) return;
        _blankPickerPending = true;
        var operation = ++_blankPickerEpoch;
        _blankPreferenceError = null;
        RefreshBlankPreferenceLabels();
        try
        {
            var response = await client.ListModelsAsync(basis.Directory, _homeCancellation.Token);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token)
                || response.Location?.Directory != basis.Directory) return;
            var rows = response.Data!.Select(model => new OpenCodeModelPickerRow(model.ProviderId!, model.Id!, model.Name!, null,
                model.Enabled != false, (model.Variants ?? new()).Select(variant => variant.Id!).ToArray(), model.Status)).ToArray();
            var current = basis.Model is { } selected ? (OpenCodeModelCurrent)new OpenCodeModelCurrent.Explicit(
                new(selected.ProviderId!, selected.Id!, selected.Variant), rows.Any(row => row.Available && row.ProviderId == selected.ProviderId && row.ModelId == selected.Id))
                : new OpenCodeModelCurrent.Unknown();
            var result = await OpenCodeModelPickerDialog.ShowAsync(XamlRoot,
                new("新对话 · " + basis.Directory, rows, current, "仅用于此草稿首次创建；强度在输入卡工具栏单独选择。", basis), allowVariantSelection: false);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token) || _newSessionCreatePending
                || result.Outcome != OpenCodeModelPickerOutcome.Confirmed || result.Choice is not { } choice) return;
            // Re-read metadata after confirmation. Never choose a withdrawn/disabled model or fabricated variant.
            var fresh = await client.ListModelsAsync(basis.Directory, _homeCancellation.Token);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token) || fresh.Location?.Directory != basis.Directory) return;
            var offered = fresh.Data!.SingleOrDefault(model => model.Id == choice.ModelId && model.ProviderId == choice.ProviderId);
            if (offered is null || offered.Enabled == false || choice.Variant is { } variant && !(offered.Variants?.Any(v => v.Id == variant) ?? false))
            { _blankPreferenceError = "模型或变体已不再可选；原有选择未变，请重新选择。"; return; }
            _blankPreferences = basis with { Model = new() { Id = choice.ModelId, ProviderId = choice.ProviderId, Variant = choice.Variant }, Version = basis.Version + 1 };
            OwnBlankMetadata(basis);
            _blankModels = fresh.Data!;
            _blankModelChosen = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (OwnsHome(source, client, basis.RuntimeSourceId) && operation == _blankPickerEpoch) _blankPreferenceError = "模型列表未能读取；现有选择与草稿未变。"; }
        finally { if (operation == _blankPickerEpoch) { _blankPickerPending = false; RefreshBlankPreferenceLabels(); } }
    }
}
