using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private bool _blankComposerPlacement;
    private OpenCodeBlankDraftPreferences? _blankPreferences;
    private readonly Dictionary<(string SourceIdentity, Guid BlankSlotId), string?> _blankCreationResults = new();
    private bool _blankPickerPending;
    private long _blankPickerEpoch;
    private string? _blankPreferenceError;
    private IReadOnlyList<OpenCodeAgentInfo> _blankAgents = Array.Empty<OpenCodeAgentInfo>();
    private IReadOnlyList<OpenCodeModelInfo> _blankModels = Array.Empty<OpenCodeModelInfo>();
    private string? _blankMetadataRuntime, _blankMetadataDirectory;
    private Guid _blankMetadataDraft;
    private Guid _blankMetadataSlotId;
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
        if (_composerController is null || !_newSessionDraftActive || _homeActive || !SourceReady
            || _activeBlankSlotId is not { } blankId || !_blankTabs.TryGetValue(blankId, out var blank)
            || blank.Epoch != _draftSourceEpoch || _activeDraftSlot is not { } slot || slot.Key.BlankSlotId != blankId) return;
        var draft = _composerController.Snapshot.Token.Draft;
        if (_blankPreferences is null || _blankPreferences.BlankSlotId != blankId)
        {
            _blankModelChosen = false;
            _blankPreferences = new(_homeRuntimeSource, draft, slot.Key.Directory) { BlankSlotId = blankId };
        }
        else if (_blankPreferences.Draft != draft) _blankPreferences = _blankPreferences with { Draft = draft };
        RefreshBlankPreferenceLabels();
        if (_blankPreferences is { } basis && basis.Matches(_homeRuntimeSource, draft)
            && (!HasBlankMetadata(basis) || !slot.DefaultsInitialized) && !_blankPickerPending)
            _ = LoadBlankMetadataAsync(basis, _composerController.Snapshot.Token);
    }

    private bool HasBlankMetadata(OpenCodeBlankDraftPreferences value) => value.RuntimeSourceId == _blankMetadataRuntime
        && value.Directory == _blankMetadataDirectory && value.BlankSlotId == _blankMetadataSlotId;

    private void OwnBlankMetadata(OpenCodeBlankDraftPreferences value)
    {
        if (!HasBlankMetadata(value)) _blankModels = Array.Empty<OpenCodeModelInfo>();
        _blankMetadataRuntime = value.RuntimeSourceId; _blankMetadataDirectory = value.Directory; _blankMetadataDraft = value.Draft;
        _blankMetadataSlotId = value.BlankSlotId;
    }

    private void AcceptBlankAgentPresentation(OpenCodeBlankDraftPreferences basis, IReadOnlyList<OpenCodeAgentInfo> agents)
    {
        if (_activeDraftSlot is not { } slot || slot.Key.SourceEpoch != _draftSourceEpoch
            || slot.Key.BlankSlotId != basis.BlankSlotId || slot.Key.Directory != basis.Directory) return;
        _blankAgents = agents;
        slot.Agents = agents;
        slot.AgentPresentationDirectory = basis.Directory;
    }

    private bool HasBlankAgentPresentation(OpenCodeBlankDraftPreferences value)
        => IsDraftSourceCurrent(_source) && _activeDraftSlot is { } slot && slot.Key.SourceEpoch == _draftSourceEpoch
        && slot.Key.BlankSlotId == value.BlankSlotId && _activeBlankSlotId == value.BlankSlotId
        && slot.Key.Directory == value.Directory && slot.AgentPresentationDirectory == value.Directory;

    private bool OwnsBlankMetadata(OpenCodeBlankDraftPreferences basis, OpenCodeComposerToken token)
        => !_homeActive && _newSessionDraftActive && _blankPreferences == basis && _composerController is { } controller
        // Background default metadata does not apply text/files. Its captured activation and exact
        // preference record/version must survive; ordinary typing need not strand initial defaults.
        && controller.Snapshot.Token.Scope == token.Scope && controller.Snapshot.Token.Draft == token.Draft
        && _activeBlankSlotId == basis.BlankSlotId
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
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankMetadata(basis, token)
                || agents.Location?.Directory != basis.Directory) return;
            AcceptBlankAgentPresentation(basis, agents.Data!);
            RefreshBlankPreferenceLabels(); // Agent presentation never waits for unrelated models/config.
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
        if (_blankPreferences != basis || basis.BlankSlotId != _activeBlankSlotId
            || !basis.Matches(_homeRuntimeSource, _composerController?.Snapshot.Token.Draft ?? Guid.Empty)) return;
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
        if (_activeDraftSlot is { } slot) { slot.DefaultsInitialized = true; SaveBlankSlotPreferences(slot); }
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
        OpenCodeAgentPresentation.Apply(BlankAgentButton, color);
    }

    private static bool IsBlankAgentSelectable(OpenCodeAgentInfo agent)
        => !agent.Hidden && agent.Mode != OpenCodeAgentModes.Subagent;

    private static string BlankStrengthText(OpenCodeModelInfo? model, OpenCodeModelRef? selected)
        => selected?.Variant is { } variant && (model?.Variants?.Any(offered => offered.Id == variant) ?? false) ? OpenCodeModelPresentation.VariantLabel(variant) : "默认";

    private void RefreshBlankPreferenceLabels()
    {
        var value = _blankPreferences;
        var current = !_draftRecoveryPending && !_homeActive && _newSessionDraftActive && value is not null && _composerController is not null
            && value.Matches(_homeRuntimeSource, _composerController.Snapshot.Token.Draft);
        var directory = current ? value!.Directory : null;
        var (folderName, folderParent) = OpenCodeDirectoryPresentation.Split(directory);
        BlankProjectName.Text = directory is null ? OpenCodeDirectoryPresentation.Placeholder : folderName;
        BlankProjectPath.Text = folderParent;
        BlankProjectPath.Visibility = folderParent.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(BlankProjectButton, OpenCodeDirectoryPresentation.AutomationName(directory));
        AutomationProperties.SetHelpText(BlankProjectButton, directory ?? OpenCodeDirectoryPresentation.PlaceholderToolTip);
        ToolTipService.SetToolTip(BlankProjectButton, directory ?? OpenCodeDirectoryPresentation.PlaceholderToolTip);
        var metadata = current && HasBlankMetadata(value!);
        var agent = current && HasBlankAgentPresentation(value!) ? _activeDraftSlot!.Agents.FirstOrDefault(agent => agent.Id == value!.Agent) : null;
        var model = metadata ? _blankModels.FirstOrDefault(model => model.Id == value!.Model?.Id && model.ProviderId == value.Model?.ProviderId) : null;
        var agentName = agent?.Name is { Length: > 0 } name ? name : _blankPickerPending ? "加载中…" : "未就绪";
        AutomationProperties.SetName(BlankAgentButton, "智能体：" + agentName);
        AutomationProperties.SetHelpText(BlankAgentButton, agent?.Description ?? string.Empty);
        ToolTipService.SetToolTip(BlankAgentButton, agent?.Description);
        ToolTipService.SetToolTip(BlankModelButton, model?.Name);
        var shownVariant = BlankStrengthText(model, value?.Model);
        AutomationProperties.SetName(BlankStrengthButton, shownVariant.Length == 0 ? "选择推理强度" : "推理强度：" + shownVariant);
        BlankPreferencesHint.Text = !current ? "目录尚未就绪或草稿来自其他来源。请明确选择当前来源的目录；草稿不会被清空。"
            : _source is { } source && _blankCreationResults.ContainsKey((source.Identity, value!.BlankSlotId))
                ? "此草稿已发起过创建；草稿已保留，不会再次创建。请先检查首页会话列表。"
                : _blankPreferenceError ?? "选择仅用于此草稿的首次发送，不修改全局或已有会话。";
        BlankPreferencesHint.Visibility = _blankPreferenceError is not null || current && _source is { } liveSource
            && _blankCreationResults.ContainsKey((liveSource.Identity, value!.BlankSlotId)) ? Visibility.Visible : Visibility.Collapsed;
        BlankAgentButton.IsEnabled = current && !_newSessionCreatePending && !_blankPickerPending;
        BlankModelButton.IsEnabled = current && !_newSessionCreatePending && !_blankPickerPending;
        BlankStrengthButton.IsEnabled = current && model is not null && !_newSessionCreatePending && !_blankPickerPending;
        BlankProjectButton.IsEnabled = !_draftRecoveryPending && _newSessionDraftActive && SourceReady && !_newSessionCreatePending && !_blankPickerPending;
        QueueSessionControlCardRefresh();
    }

    private OpenCodeBlankDraftPreferences? CaptureBlankPreferences(OpenCodeComposerToken token)
    {
        // Exact draft/source/ABA checks precede creation. Unknown creation cannot be retried by editing its text/choices.
        if (_homeActive || _blankPickerPending || _blankPreferences is not { } value
            || !value.Matches(_homeRuntimeSource, token.Draft) || value.BlankSlotId != _activeBlankSlotId
            || _activeDraftSlot is not { DefaultsInitialized: true } || !_homeProjects.ContainsKey(value.Directory)
            || _source is not { } source || _blankCreationResults.ContainsKey((source.Identity, value.BlankSlotId)))
        {
            ShowMessage(InfoBarSeverity.Warning, "尚未创建", "请选择当前来源的项目目录；若上次创建结果未确认，请先检查会话列表。草稿已保留。");
            return null;
        }
        return value with { Model = value.Model is { } model ? model with { } : null };
    }

    private bool OwnsBlankPreferences(OpenCodeBlankDraftPreferences value, OpenCodeComposerToken token)
        => !_homeActive && _newSessionDraftActive && _composerController?.Snapshot.Token == token
        && _blankPreferences == value && value.BlankSlotId == _activeBlankSlotId && value.Matches(_homeRuntimeSource, token.Draft);

    private void BlankProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_newSessionDraftActive || _newSessionCreatePending || _blankPickerPending || _composerController is null) return;
        var runtime = _homeRuntimeSource;
        var token = _composerController.Snapshot.Token;
        var current = _blankPreferences?.Directory;
        var source = _source;
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var anchorY = BlankProjectButton.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point()).Y;
        var available = Math.Max(anchorY, XamlRoot.Size.Height - anchorY - BlankProjectButton.ActualHeight);
        var panel = new Grid { RowSpacing = 10, Width = Math.Max(1, Math.Min(440, XamlRoot.Size.Width - 64)),
            Height = Math.Max(180, Math.Min(420, available - 40)) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            panel.RowDefinitions.Add(new RowDefinition { Height = height });
        panel.Children.Add(new TextBlock { Text = "工作目录", Style = OpenCodeInteractionUi.TryStyle("BodyStrongTextBlockStyle") });
        var search = new AutoSuggestBox { PlaceholderText = "搜索项目或路径", QueryIcon = new SymbolIcon(Symbol.Find) };
        Grid.SetRow(search, 1);
        panel.Children.Add(search);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
        Grid.SetRow(list, 2);
        AutomationProperties.SetName(list, "工作目录");
        void Populate()
        {
            list.Items.Clear();
            foreach (var project in _homeProjects.Values.Where(project => string.IsNullOrWhiteSpace(search.Text)
                || project.DisplayName.Contains(search.Text, StringComparison.OrdinalIgnoreCase)
                || project.Key.Directory.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = project.DisplayName == project.Key.Directory
                    ? OpenCodeDirectoryPresentation.Split(project.Key.Directory).Name : project.DisplayName,
                    TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = project.Key.Directory, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    Style = OpenCodeInteractionUi.TryStyle("CaptionTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis });
                var item = new ListViewItem { Content = row, Tag = project.Key.Directory };
                ToolTipService.SetToolTip(item, project.Key.Directory);
                list.Items.Add(item);
                if (project.Key.Directory == current) list.SelectedItem = item;
            }
        }
        bool Current() => _pageActive && SourceReady && !_homeActive && _newSessionDraftActive && ReferenceEquals(source, _source)
            && runtime == _homeRuntimeSource && !_newSessionCreatePending && _composerController.Snapshot.Token == token;
        list.ItemClick += (_, args) =>
        {
            if (!Current() || args.ClickedItem is not ListViewItem { Tag: string directory } || !_homeProjects.ContainsKey(directory)) return;
            flyout.Hide();
            if (_blankPreferences?.Directory == directory) { RefreshBlankPreferenceLabels(); return; }
            ChangeBlankSlotDirectory(directory);
        };
        search.TextChanged += (_, _) => Populate();
        Populate();
        panel.Children.Add(list);
        var add = new HyperlinkButton { Content = "添加项目文件夹…" };
        Grid.SetRow(add, 3);
        add.Click += async (_, _) => { if (!Current()) return; flyout.Hide(); await AddHomeDirectoryAsync(); };
        panel.Children.Add(add);
        flyout.Content = panel;
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
            AcceptBlankAgentPresentation(basis, response.Data!);
            var rows = response.Data!.Where(IsBlankAgentSelectable)
                .Select(agent => new OpenCodeAgentPickerRow(agent.Id!, agent.Name!, null, true, null, agent.Color)).ToArray();
            var selected = await OpenCodeAgentPickerDialog.ShowAsync(XamlRoot, rows, basis.Agent, null, null,
                anchor: sender as FrameworkElement);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token)
                || _newSessionCreatePending || selected is null || !rows.Any(row => row.Id == selected)) return;
            _blankPreferences = basis with { Agent = selected, Model = _blankModelChosen ? basis.Model : null, Version = basis.Version + 1 };
            _blankPreferenceError = null;
            _blankMetadataDirectory = null;
            // Hand directly to the metadata lifetime before publishing an idle picker state.
            // A second click must not race the chosen agent's model/default refresh.
            _blankPickerPending = false;
            await LoadBlankMetadataAsync(_blankPreferences, token);
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
        await ChooseBlankModelAsync(basis, token, anchor: sender as FrameworkElement);
    }

    private async void BlankStrengthButton_Click(object sender, RoutedEventArgs e)
    {
        if (_blankPickerPending || _newSessionCreatePending || _blankPreferences is not { Model: not null } basis || _source is not { } source
            || _client is not { } client || _homeCancellation is null || _composerController is null) return;
        var token = _composerController.Snapshot.Token;
        if (!OwnsBlankPreferences(basis, token)) return;
        _blankPickerPending = true;
        var operation = ++_blankPickerEpoch;
        OpenCodeModelPickerResult? result = null;
        RefreshBlankPreferenceLabels();
        try
        {
            var response = await client.ListModelsAsync(basis.Directory, _homeCancellation.Token);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token) || response.Location?.Directory != basis.Directory) return;
            var model = response.Data!.SingleOrDefault(model => model.Id == basis.Model.Id && model.ProviderId == basis.Model.ProviderId);
            if (model is null || model.Enabled == false) { _blankPreferenceError = "模型已不再可选，请重新选择。"; return; }
            OwnBlankMetadata(basis);
            _blankModels = response.Data!;
            var row = new OpenCodeModelPickerRow(model.ProviderId!, model.Id!, model.Name!, null, true,
                (model.Variants ?? new()).Select(variant => variant.Id!).ToArray(), model.Status);
            var presentation = new OpenCodeModelPickerPresentation("新会话", new[] { row },
                new OpenCodeModelCurrent.Explicit(new(model.ProviderId!, model.Id!, basis.Model.Variant), true), null, basis);
            result = await OpenCodeVariantPickerDialog.ShowAsync(XamlRoot, presentation, anchor: sender as FrameworkElement);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (OwnsHome(source, client, basis.RuntimeSourceId) && operation == _blankPickerEpoch) _blankPreferenceError = "强度信息未能读取；当前选择未变。"; }
        finally { if (operation == _blankPickerEpoch) { _blankPickerPending = false; RefreshBlankPreferenceLabels(); } }
        if (operation == _blankPickerEpoch && OwnsHome(source, client, basis.RuntimeSourceId)
            && OwnsBlankPreferences(basis, token) && !_newSessionCreatePending
            && result is { Outcome: OpenCodeModelPickerOutcome.Confirmed, Choice: { } choice }
            && choice.ProviderId == basis.Model.ProviderId && choice.ModelId == basis.Model.Id
            && choice.Variant != basis.Model.Variant)
            await SetBlankVariantAsync(basis, token, choice.Variant);
    }

    internal const string BlankVariantRadioGroup = "OpenCodeBlankReasoningEffort";

    // Default (null wire value) first, then only catalog-offered raw IDs; labels are display-only.
    internal static IReadOnlyList<(string? Id, string Label, bool IsChecked)> BlankVariantMenuEntries(OpenCodeModelInfo model, string? current)
    {
        var ids = new List<string?> { null };
        foreach (var variant in model.Variants ?? new())
            if (variant.Id is { } id && !ids.Contains(id)) ids.Add(id);
        return ids.Select(id => (id, OpenCodeModelPresentation.VariantLabel(id), id == current)).ToArray();
    }

    private async Task ChooseBlankModelAsync(OpenCodeBlankDraftPreferences basis, OpenCodeComposerToken token, FrameworkElement? anchor = null,
        OpenCodeModelChoiceKey? selectedChoice = null)
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
            await OpenCodeRecentModels.Shared.LoadAsync();
            if (operation != _blankPickerEpoch || !OwnsHome(source, client, basis.RuntimeSourceId)
                || !OwnsBlankPreferences(basis, token) || _newSessionCreatePending) return;
            var rows = response.Data!.Select(model => new OpenCodeModelPickerRow(model.ProviderId!, model.Id!, model.Name!, null,
                model.Enabled != false, (model.Variants ?? new()).Select(variant => variant.Id!).ToArray(), model.Status)).ToArray();
            var current = basis.Model is { } selected ? (OpenCodeModelCurrent)new OpenCodeModelCurrent.Explicit(
                new(selected.ProviderId!, selected.Id!, selected.Variant), rows.Any(row => row.Available && row.ProviderId == selected.ProviderId && row.ModelId == selected.Id))
                : new OpenCodeModelCurrent.Unknown();
            var result = selectedChoice is { } requested
                ? new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Confirmed, requested, null)
                : await OpenCodeModelPickerDialog.ShowAsync(XamlRoot,
                    new("新对话 · " + basis.Directory, rows, current, null, basis), sourceKey: source.Identity, allowStrengthSelection: false, anchor: anchor,
                    favoriteClient: client, favoriteDirectory: basis.Directory, favoriteCancellation: _homeCancellation.Token);
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

    private async Task SetBlankVariantAsync(OpenCodeBlankDraftPreferences basis, OpenCodeComposerToken token, string? variantId)
    {
        if (basis.Model?.Variant == variantId) return;
        if (_blankPickerPending || _newSessionCreatePending || basis.Model is null || _source is not { } source
            || _client is not { } client || _homeCancellation is null || !OwnsBlankPreferences(basis, token)) return;
        _blankPickerPending = true;
        var operation = ++_blankPickerEpoch;
        RefreshBlankPreferenceLabels();
        try
        {
            var fresh = await client.ListModelsAsync(basis.Directory, _homeCancellation.Token);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token)
                || _newSessionCreatePending || fresh.Location?.Directory != basis.Directory) return;
            var model = fresh.Data!.SingleOrDefault(m => m.Id == basis.Model.Id && m.ProviderId == basis.Model.ProviderId);
            if (!OpenCodeModelPresentation.VariantAvailable(model, variantId))
            { _blankPreferenceError = "模型或强度已不再可选；当前选择未变，请重新选择。"; return; }
            _blankPreferences = basis with { Model = basis.Model with { Variant = variantId }, Version = basis.Version + 1 };
            OwnBlankMetadata(basis);
            _blankModels = fresh.Data!;
            _blankModelChosen = true;
            _blankPreferenceError = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (OwnsHome(source, client, basis.RuntimeSourceId) && operation == _blankPickerEpoch) _blankPreferenceError = "强度信息未能读取；当前选择未变。"; }
        finally { if (operation == _blankPickerEpoch) { _blankPickerPending = false; RefreshBlankPreferenceLabels(); } }
    }

    private async Task SetBlankAgentAsync(OpenCodeBlankDraftPreferences basis, OpenCodeComposerToken token, string selected)
    {
        if (selected == basis.Agent || _blankPickerPending || _newSessionCreatePending || _source is not { } source
            || _client is not { } client || _homeCancellation is null || !OwnsBlankPreferences(basis, token)) return;
        _blankPickerPending = true;
        var operation = ++_blankPickerEpoch;
        RefreshBlankPreferenceLabels();
        try
        {
            var response = await client.ListAgentsAsync(basis.Directory, _homeCancellation.Token);
            if (!OwnsHome(source, client, basis.RuntimeSourceId) || !OwnsBlankPreferences(basis, token)
                || _newSessionCreatePending || response.Location?.Directory != basis.Directory
                || !response.Data!.Any(a => IsBlankAgentSelectable(a) && a.Id == selected)) return;
            AcceptBlankAgentPresentation(basis, response.Data!);
            _blankPreferences = basis with { Agent = selected, Model = _blankModelChosen ? basis.Model : null, Version = basis.Version + 1 };
            _blankPreferenceError = null;
            _blankMetadataDirectory = null;
            _blankPickerPending = false;
            await LoadBlankMetadataAsync(_blankPreferences, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (OwnsHome(source, client, basis.RuntimeSourceId) && operation == _blankPickerEpoch) _blankPreferenceError = "智能体列表未能读取；现有选择与草稿未变。"; }
        finally { if (operation == _blankPickerEpoch) { _blankPickerPending = false; RefreshBlankPreferenceLabels(); } }
    }
}
