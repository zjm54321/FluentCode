using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    private static OpenCodeModelPickerRow Row(string provider, string id, string name, bool available = true)
        => new(provider, id, name, "technical provider", available, new[] { "high", "extra_high", "opaque/V2" }, "active");
    private static void Main(string[] args)
    {
        foreach (var sample in new[] { ("gateway/google/gemini-2.5-pro", OpenCodeModelBrand.Gemini), ("gemini3", OpenCodeModelBrand.Gemini),
            ("proxy/anthropic.claude-sonnet-4", OpenCodeModelBrand.Claude), ("Claude Opus 4.5", OpenCodeModelBrand.Claude),
            ("azure/gpt-5.2", OpenCodeModelBrand.OpenAI), ("openrouter/o3-mini", OpenCodeModelBrand.OpenAI),
            ("o4-mini", OpenCodeModelBrand.OpenAI), ("chatgpt-latest", OpenCodeModelBrand.OpenAI),
            ("x-ai/grok-4", OpenCodeModelBrand.Grok), ("Grok 4 Fast", OpenCodeModelBrand.Grok),
            ("deepseek/deepseek-chat", OpenCodeModelBrand.DeepSeek), ("DeepSeek R1", OpenCodeModelBrand.DeepSeek),
            ("z-ai/glm-4.6", OpenCodeModelBrand.GLM), ("GLM-4.5-Air", OpenCodeModelBrand.GLM),
            ("THUDM/chatglm3-6b", OpenCodeModelBrand.GLM), ("glm4", OpenCodeModelBrand.GLM),
            ("moonshotai/kimi-k2", OpenCodeModelBrand.Kimi), ("moonshot-v1-128k", OpenCodeModelBrand.Kimi),
            ("Kimi K2 Thinking", OpenCodeModelBrand.Kimi), ("qwen/qwen3-coder", OpenCodeModelBrand.Qwen),
            ("QwQ-32B", OpenCodeModelBrand.Qwen), ("qvq-72b-preview", OpenCodeModelBrand.Qwen),
            ("anthropic/custom-model", OpenCodeModelBrand.Neutral), ("google/custom-model", OpenCodeModelBrand.Neutral),
            ("openai/custom-model", OpenCodeModelBrand.Neutral), ("notgpt-custom", OpenCodeModelBrand.Neutral),
            ("claudette", OpenCodeModelBrand.Neutral), ("geminization", OpenCodeModelBrand.Neutral),
            ("glmx", OpenCodeModelBrand.Neutral), ("kimichi", OpenCodeModelBrand.Neutral),
            ("grokking", OpenCodeModelBrand.Neutral), ("deepseeker", OpenCodeModelBrand.Neutral),
            ("qwench", OpenCodeModelBrand.Neutral), ("chatglmx", OpenCodeModelBrand.Neutral),
            ("moonshotting", OpenCodeModelBrand.Neutral), ("z-ai/custom-model", OpenCodeModelBrand.Neutral) })
            Check(OpenCodeModelPresentation.Brand(sample.Item1, null) == sample.Item2, "explicit proxy family/unknown fallback: " + sample.Item1);
        Check(OpenCodeModelPresentation.Brand("opaque-id", "Claude Sonnet") == OpenCodeModelBrand.Claude, "friendly family name works behind opaque catalog ID");
        foreach (var sample in new[] { ("Grok 4", OpenCodeModelBrand.Grok), ("DeepSeek Chat", OpenCodeModelBrand.DeepSeek),
            ("GLM 4.6", OpenCodeModelBrand.GLM), ("Kimi K2", OpenCodeModelBrand.Kimi), ("Qwen3 Coder", OpenCodeModelBrand.Qwen) })
            Check(OpenCodeModelPresentation.Brand("opaque-id", sample.Item1) == sample.Item2, "friendly family name: " + sample.Item1);
        Check(OpenCodeModelPresentation.Brand(null, null) == OpenCodeModelBrand.Neutral, "missing model has default icon");
        Check(OpenCodeModelPresentation.Asset(OpenCodeModelBrand.Neutral, false) is null
            && OpenCodeModelPresentation.Asset(OpenCodeModelBrand.Neutral, true) is null, "unknown models never borrow a brand asset");
        foreach (var pair in new (string? Id, string Label)[] { (null, "默认"), ("low", "低"), ("medium", "中"), ("high", "高"),
            ("xhigh", "极高"), ("extra high", "极高"), ("extra_high", "极高"), ("extra-high", "极高"), ("max", "最高"), ("HIGH", "高"), ("Opaque/V2", "Opaque/V2") })
            Check(OpenCodeModelPresentation.VariantLabel(pair.Id) == pair.Label, "variant display-only mapping");
        var rows = new[] { Row("z", "duplicate", "Zulu"), Row("b", "duplicate", "Alpha"), Row("a", "duplicate", "Alpha"), Row("q", "other", "Beta") };
        var recents = new OpenCodeRecentModels(Path.Combine(args[1], "synthetic-recents.json"));
        var sorted = OpenCodeModelPresentation.Order(rows, "source-a", recents);
        Check(sorted.Select(r => r.ProviderId).SequenceEqual(new[] { "a", "b", "q", "z" }), "flat alphabetical order with stable provider/id tie-break");
        var presentation = new OpenCodeModelPickerPresentation("unused session label", sorted, new OpenCodeModelCurrent.Explicit(new("b", "duplicate", "high"), true), "unused scope note", new object());
        Check(OpenCodeModelPresentation.CurrentName(presentation.Current, rows) == "Alpha", "header only current friendly name");
        Check(OpenCodeModelPresentation.CurrentRow(presentation)?.ProviderId == "b", "independent strength entry resolves actual current provider/model, not first duplicated ID");
        Check(OpenCodeModelPresentation.CurrentRow(presentation with { Current = new OpenCodeModelCurrent.Unknown() }) is null, "unknown current model cannot be edited by guessing");
        Check(OpenCodeModelPresentation.CurrentRow(presentation with { Current = new OpenCodeModelCurrent.Inherited(new("q", "other", null)) })?.ProviderId == "q", "inherited current default has a real independent strength target");
        Check(OpenCodeModelPresentation.InformativeNote("仅影响之后的回合；确认前会重新读取，状态变化时需要重新选择。") is null
            && OpenCodeModelPresentation.InformativeNote("仅用于此草稿首次创建；强度在输入卡工具栏单独选择。") is null, "only the two known scope boilerplates are removed");
        Check(OpenCodeModelPresentation.InformativeNote("模型读取失败，请重新连接。") == "模型读取失败，请重新连接。", "genuine caller diagnostic is preserved, not treated as scope copy");
        var state = new OpenCodeModelPickerState(presentation);
        Check(state.SelectModel("a", "duplicate") && state.SelectedRow?.ProviderId == "a", "same ID across providers remains exact identity");
        Check(state.SelectVariant("extra_high") && state.Choice?.Variant == "extra_high", "display translation never rewrites opaque variant");
        Check(!state.SelectVariant("极高"), "localized display is not accepted as fabricated wire ID");
        Check(state.SelectVariant(null) && state.Choice?.Variant is null && state.CanConfirm, "default clears to null");
        var result = state.TryFreezeConfirmation();
        Check(result?.Choice is { ProviderId: "a", ModelId: "duplicate", Variant: null }, "confirmation freezes exact provider/model/base key");
        Check(!File.Exists(Path.Combine(args[1], "synthetic-recents.json")), "UI ordering/selecting/confirming does not write recent usage");
        recents.RecordSuccessfulUseAsync("source-a", "z", "duplicate").GetAwaiter().GetResult();
        recents.RecordSuccessfulUseAsync("source-a", "b", "duplicate").GetAwaiter().GetResult();
        Check(OpenCodeModelPresentation.Order(rows, "source-a", recents).Select(r => r.ProviderId).SequenceEqual(new[] { "b", "z", "a", "q" }), "UI read adapter preserves MRU then stable names across providers");
        Check(OpenCodeModelPresentation.Order(rows, "source-b", recents).Select(r => r.ProviderId).SequenceEqual(new[] { "a", "b", "q", "z" }), "UI read adapter keeps source history isolated");
        Check(OpenCodeModelPresentation.Order(rows, "source-a", recents).First().Variants.SequenceEqual(rows[1].Variants), "ordering retains original presentation metadata without model/provider mixup");
        var strengthState = new OpenCodeModelPickerState(presentation);
        var currentRow = OpenCodeModelPresentation.CurrentRow(presentation)!;
        strengthState.SelectModel(currentRow.ProviderId, currentRow.ModelId);
        strengthState.SelectVariant("high");
        Check(!strengthState.CanConfirm, "unchanged independent normal strength is not confirmable");
        strengthState.SelectVariant(null);
        Check(strengthState.TryFreezeConfirmation()?.Choice is { ProviderId: "b", ModelId: "duplicate", Variant: null }, "independent normal strength default preserves same current model/provider");
        strengthState.SelectVariant("extra_high");
        Check(strengthState.TryFreezeConfirmation()?.Choice is { ProviderId: "b", ModelId: "duplicate", Variant: "extra_high" }, "independent normal strength returns same coordinator result contract with exact ID");
        strengthState.Replace(presentation with { Rows = rows.Where(r => r.ProviderId != "b").ToArray() });
        Check(!strengthState.CanConfirm, "independent normal strength loses confirmation when catalog withdraws its model");
        state.SelectVariant("opaque/V2");
        state.Replace(presentation with { Rows = sorted.Select(r => r.ProviderId == "a" ? r with { Variants = new[] { "high" } } : r).ToArray() });
        Check(state.SelectedRow is null && !state.CanConfirm && state.ChoiceWithdrawnNotice is not null, "withdrawn variant cannot be silently retargeted");
        state.SelectModel("a", "duplicate");
        state.Replace(presentation with { Rows = sorted.Select(r => r.ProviderId == "a" ? r with { Available = false } : r).ToArray() });
        Check(state.SelectedRow is null && !state.SelectModel("a", "duplicate"), "withdrawn/disabled model rejected");
        var model = new OpenCodeModelInfo { Id = "duplicate", ProviderId = "a", Variants = new() { new() { Id = "extra_high" } } };
        Check(OpenCodeModelPresentation.VariantAvailable(model, null), "base allowed without fabricated catalog default");
        Check(OpenCodeModelPresentation.VariantAvailable(model, "extra_high") && !OpenCodeModelPresentation.VariantAvailable(model, "xhigh"), "catalog exact spelling required despite synonymous display");
        Check(!OpenCodeModelPresentation.VariantAvailable(model with { Enabled = false }, null) && !OpenCodeModelPresentation.VariantAvailable(null, null), "default cannot revive removed/disabled model");
        var root = args[0];
        string Read(string path) => File.ReadAllText(Path.Combine(root, path));
        var dialog = Read("src/AIFrontend/Pages/OpenCode/OpenCodeComposerNativeDialogs.cs").Split("internal static class OpenCodeRollbackConfirmationDialog")[0];
        Check(dialog.Contains("row.ModelId.Contains(filter") && dialog.Contains("row.ProviderName ?? row.ProviderId") && dialog.Contains("row.Name.Contains(filter"), "search keeps true fields without exposing subtitles");
        Check(dialog.Contains("OpenCodeModelLogo.Label(row.ModelId, row.Name)") && dialog.Contains("Tag = row") && dialog.Contains("ListViewSelectionMode.Single"), "logo plus name preserves native selection identity/keyboard surface");
        Check(!dialog.Contains("OpenCodeCurrentStrengthEntry") && !dialog.Contains("strengthButton")
            && !dialog.Contains("OpenCodeVariantPickerDialog.ShowAsync"), "model dialog has no duplicate reasoning-strength entry or variant dialog handoff");
        Check(dialog.Contains("bool allowStrengthSelection = true") && !dialog.Contains("Visibility = allowStrengthSelection"), "legacy caller argument retained without enabling in-dialog strength selection");
        var rowView = dialog.Substring(dialog.IndexOf("private static ListViewItem RowItem", StringComparison.Ordinal));
        Check(!rowView.Contains("当前") && !rowView.Contains("marker") && !rowView.Contains("SemiBold"), "model rows contain only logo and name without current status marker or special emphasis");
        Check(!dialog.Contains("new ContentDialog") && !dialog.Contains("PrimaryButtonText") && !dialog.Contains("OpenCodeModelPickerRefresh")
            && !dialog.Contains("\"当前模型\"") && dialog.Contains("new AutoSuggestBox") && dialog.Contains("\"选择模型\""),
            "P1/P3 model flyout has title/search/list only, no current header, refresh or confirm footer");
        Check(dialog.Contains("OpenCodeModelPresentation.Order(presentation.Rows, sourceKey)") && !dialog.Contains("await OpenCodeRecentModels.Shared.LoadAsync()"), "flyout only reads in-memory shared MRU and never loads preferences while rendering");
        Check(dialog.Contains("list.ItemClick +=") && dialog.Contains("state.TryFreezeConfirmation()")
            && dialog.Contains("state.SelectVariant(current.Key.Variant)"), "model click freezes exact basis; clicking current model never resets effort");
        var flyout = Read("src/AIFrontend/Pages/OpenCode/OpenCodePickerFlyout.cs");
        Check(flyout.Contains("new Flyout") && flyout.Contains("FlyoutPlacementMode.TopEdgeAlignedLeft") && flyout.Contains("flyout.ShowAt(anchor)")
            && flyout.Contains("OpenCodeBlankModel") && flyout.Contains("anchor.Unloaded += Unloaded"), "anchored native flyout supports sidebar/composer callers and dismisses on owner unload");
        var blank = Read("src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs");
        Check(blank.Contains("OpenCodeModelLogo.Label(model?.Id, model?.Name") && blank.Contains("ToolTipService.SetToolTip(BlankModelButton, model?.Name)"), "composer brand label and clean friendly tooltip");
        Check(blank.Contains("sourceKey: source.Identity"), "blank picker uses captured stable source identity");
        Check(blank.Contains("allowStrengthSelection: false"), "blank picker uses its existing dedicated strength control instead of duplicate dialog entry");
        Check(blank.Contains("await OpenCodeRecentModels.Shared.LoadAsync();\n            if (operation != _blankPickerEpoch || !OwnsHome(source, client, basis.RuntimeSourceId)\n                || !OwnsBlankPreferences(basis, token) || _newSessionCreatePending) return;"), "blank picker revalidates captured source/token/epoch immediately after MRU load await");
        var strengthStart = blank.IndexOf("private async void BlankStrengthButton_Click", StringComparison.Ordinal);
        var strengthProducer = blank.Substring(strengthStart, blank.IndexOf("internal const string BlankVariantRadioGroup", StringComparison.Ordinal) - strengthStart);
        Check(strengthProducer.Contains("(model.Variants ?? new()).Select(variant => variant.Id!).ToArray()"), "blank strength picker receives only current-model catalog variants");
        Check(strengthProducer.Contains("OpenCodeVariantPickerDialog.ShowAsync(XamlRoot, presentation, anchor: sender as FrameworkElement)")
            && !strengthProducer.Contains("RadioMenuFlyoutItem"), "blank strength uses shared compact native ListView picker");
        Check(strengthProducer.Contains("choice.Variant != basis.Model.Variant)") && strengthProducer.Contains("await SetBlankVariantAsync(basis, token, choice.Variant)")
            && !strengthProducer.Contains("Checked +=") && !strengthProducer.Contains("Unchecked +=") && !strengthProducer.Contains("RegisterPropertyChangedCallback"),
            "only an explicit click on a different item commits, via the guarded SetBlankVariantAsync entry");
        Check(blank.Contains("await SetBlankVariantAsync(basis, token, choice.Variant)") && blank.Contains("var fresh = await client.ListModelsAsync(basis.Directory") && blank.Contains("VariantAvailable(model, variantId)"), "variant gesture rechecks fresh catalog");
        Check(blank.Contains("Model = basis.Model with { Variant = variantId }") && blank.Contains("choice.Variant is { } variant && !(offered.Variants?.Any"), "opaque ID commit and model-dialog fresh variant validation retained");
        var logo = Read("src/AIFrontend/Pages/OpenCode/OpenCodeModelLogo.cs");
        Check(logo.Contains("new FontIcon") && logo.Contains("Glyph = \"\\uE99A\"") && logo.Contains("SymbolThemeFontFamily")
            && !logo.Contains("Text = \"AI\"") && !logo.Contains("Foreground =")
            && logo.Contains("host.ActualThemeChanged += Update") && logo.Contains("ticket != revision") && logo.Contains("AppContext.BaseDirectory"), "theme-inheriting Fluent robot fallback, theme updates and stale async-load fence");
        Check(logo.Contains("if (brand == OpenCodeModelBrand.Neutral) return host;")
            && logo.Contains("fallback.Visibility = Visibility.Visible"), "unknown and failed assets share the same default icon");
        foreach (var brand in Enum.GetValues<OpenCodeModelBrand>().Where(b => b != OpenCodeModelBrand.Neutral))
            foreach (var dark in new[] { false, true })
            {
                var path = Path.Combine(root, "src/AIFrontend/Assets/Brands", OpenCodeModelPresentation.Asset(brand, dark)!);
                Check(File.Exists(path), "brand asset exists: " + path);
                if (path.EndsWith(".svg"))
                {
                    var svg = XDocument.Load(path);
                    Check(svg.Root?.Name.LocalName == "svg", "valid SVG document");
                    Check(!svg.Descendants().Any(e => e.Name.LocalName is "style" or "foreignObject"), "rendered SVG needs no unsupported CSS/HTML: " + path);
                }
                else
                {
                    var png = File.ReadAllBytes(path);
                    int Size(int offset) => (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
                    Check(png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                        && Size(16) >= 64 && Size(20) >= 64, "official PNG at least 64px: " + path);
                }
            }
        var project = XDocument.Parse(Read("src/AIFrontend/AIFrontend.csproj"));
        Check(project.Descendants("Content").Any(n => (string?)n.Attribute("Include") == @"Assets\Brands\**\*"
            && (string?)n.Attribute("CopyToOutputDirectory") == "PreserveNewest" && (string?)n.Attribute("CopyToPublishDirectory") == "PreserveNewest"), "brand resources copied to build/publish output");
        var strengthDialog = Read("src/AIFrontend/Pages/OpenCode/OpenCodeVariantPickerDialog.cs");
        Check(strengthDialog.Contains("OpenCodeModelPickerState(presentation)") && strengthDialog.Contains("state.SelectModel(row.ProviderId, row.ModelId)")
            && strengthDialog.Contains("foreach (var variant in row.Variants)"), "independent strength editor has only current-model catalog variants");
        Check(strengthDialog.Contains("VariantLabel(null), Tag = null") && strengthDialog.Contains("VariantLabel(variant), Tag = variant")
            && strengthDialog.Contains("state.TryFreezeConfirmation()"), "independent default/Chinese display keep wire IDs and frozen explicit confirmation");
        Check(strengthDialog.Contains("new ListView") && strengthDialog.Contains("list.ItemClick +=") && !strengthDialog.Contains("RadioButton")
            && !strengthDialog.Contains("Title =") && !strengthDialog.Contains("new ContentDialog") && !strengthDialog.Contains("body.Children.Add"),
            "P4/PK1 effort is only a compact native ListView, no title/note/radio dots/confirmation");
        var agentPicker = Read("src/AIFrontend/Pages/OpenCode/OpenCodeAgentPickerDialog.cs");
        Check(agentPicker.Contains("if (!row.Selectable) continue") && agentPicker.Contains("OpenCodeAgentIcons.Glyph(row.Id)")
            && agentPicker.Contains("OpenCodeAgentIcons.DisplayName(row.Name)") && !agentPicker.Contains("Title =")
            && !agentPicker.Contains("new ContentDialog") && !agentPicker.Contains("row.Description"), "P5/PK1/AS1 agent flyout has only switchable icon/name ListView rows");
        var selector = Read("src/AIFrontend/Pages/OpenCode/OpenCodeAgentSelector.cs");
        var pickerRoute = selector.Substring(selector.IndexOf("private async Task ShowPickerAsync"),
            selector.IndexOf("private void SelectPendingAgent") - selector.IndexOf("private async Task ShowPickerAsync"));
        Check(pickerRoute.Contains("SelectPendingAgent(origin, target)") && !pickerRoute.Contains("RunSwitchAsync"), "AS3 picker choice is UI-only, never a server switch");
        Check(selector.Contains("internal async Task<bool> ApplyPendingSelectionAsync()") && selector.Contains("await RunSwitchAsync(origin, target)")
            && selector.Contains("_coordinator.SwitchAsync(origin, operationId, target, IsOriginCurrent"), "next-send seam preserves existing server coordinator/re-check");
        AIFrontend.Pages.OpenCodePage.RunVariantSelectionChecks(Check).GetAwaiter().GetResult();
        var cardBinding = Read("src/AIFrontend/Pages/OpenCodePage.SessionControlCard.cs");
        Check(cardBinding.Contains("_agentSelector.DisplayChanged +=") && cardBinding.Contains("_agentSelector.CurrentModel")
            && cardBinding.Contains("_blankPreferences") && !cardBinding.Contains("GetSessionInfoAsync") && !cardBinding.Contains("ListModelsAsync"),
            "sidebar reads the same session/blank owners, never adds a second selection cache or data fetch");
        Check(cardBinding.Contains("BlankAgentButton_Click(sender, e)") && cardBinding.Contains("BlankModelButton_Click(sender, e)")
            && cardBinding.Contains("BlankStrengthButton_Click(sender, e)") && cardBinding.Contains("尚未保存为会话选择"),
            "blank sidebar delegates to existing draft semantics and labels its unpersisted scope");
        Check(cardBinding.Contains("CurrentModelInfo(binding!, selected)?.Name") && !cardBinding.Contains("selected?.Id ?? \"模型不可用\""),
            "P2 sidebar uses catalog display Name, never model ID as display name");
        RunChildSessionChecks();
        var card = Read("src/AIFrontend/Pages/OpenCode/OpenCodeSessionControlCard.cs");
        var readOnly = card.Substring(card.IndexOf("internal void RenderReadOnly"), card.IndexOf("private static Button Entry") - card.IndexOf("internal void RenderReadOnly"));
        Check(card.Contains("IsHitTestVisible = false") && readOnly.Contains("_chrome.Child = _readOnlyRows")
            && !readOnly.Contains("new Button") && !readOnly.Contains("ToolTipService") && !readOnly.Contains("Flyout"),
            "SA2 child mode uses only plain display visuals with no selector hover/focus/flyout affordance");
        Check(readOnly.Contains("value.Agent is null ? Visibility.Collapsed") && readOnly.Contains("value.ModelName is null ? Visibility.Collapsed")
            && readOnly.Contains("value.Effort is null ? Visibility.Collapsed"), "SA2 unknown child values hide their own rows independently");
        Check(cardBinding.Contains("CurrentSessionInfo is { ParentId: not null } child") && cardBinding.Contains("child.Id == _selectedSessionId")
            && cardBinding.Contains("OpenCodeChildSessionControl.Project(child.Agent, child.Model, messages") && cardBinding.Contains("card.RenderReadOnly(projection)"),
            "SA2 card consumes real current-child metadata/messages, not pending UI or parent choices");
        Console.WriteLine($"PASS A2 picker UI: {checks} checks; production display/state/selection + structural native UI/resource checks (no native rendering)");
    }

    private static void RunChildSessionChecks()
    {
        var first = new OpenCodeModelRef { Id = "claude-first", ProviderId = "provider-a", Variant = "high" };
        var last = new OpenCodeModelRef { Id = "claude-last", ProviderId = "provider-b", Variant = "extra_high" };
        OpenCodeModelInfo? Catalog(OpenCodeModelRef key) => key.Id == last.Id && key.ProviderId == last.ProviderId
            ? new() { Id = key.Id, ProviderId = key.ProviderId, Name = "Claude Opus 5.5" } : null;
        var empty = Array.Empty<(string? Agent, OpenCodeModelRef? Model)>();
        var unknown = OpenCodeChildSessionControl.Project(null, null, empty, Catalog);
        Check(unknown is { Agent: null, Model: null, ModelName: null, Effort: null }, "SA2 absent real child values stay absent");
        var info = OpenCodeChildSessionControl.Project("explorer", last, empty, Catalog);
        Check(info.Agent == "explorer" && info.Model == last && info.ModelName == "Claude Opus 5.5" && info.Effort == "极高",
            "SA2 Session.Info fallback preserves child identity, catalog name and opaque variant");
        var messages = new (string? Agent, OpenCodeModelRef? Model)[] { ("explorer", first), ("fixer", last), ("oracle", null) };
        var actual = OpenCodeChildSessionControl.Project("orchestrator", first, messages, Catalog);
        Check(actual.Agent == "oracle" && actual.Model == last && actual.ModelName == "Claude Opus 5.5" && actual.Effort == "极高",
            "SA2 latest real assistant/switch fields override stale session defaults without borrowing model variant");
        var noName = OpenCodeChildSessionControl.Project("fixer", first, empty, Catalog);
        Check(noName.ModelName is null && noName.Effort == "高", "SA2 missing matching catalog name hides model row, never falls back to ID; known effort remains");
        var changed = OpenCodeChildSessionControl.Project("fixer", first,
            new (string? Agent, OpenCodeModelRef? Model)[] { (null, last with { Variant = null }) }, Catalog);
        Check(changed.Model?.ProviderId == "provider-b" && changed.Model?.Variant is null && changed.Effort == "默认",
            "SA2 explicit base child model does not inherit an older model's high effort");
        var malformed = OpenCodeChildSessionControl.Project(" ", last with { ProviderId = null }, empty, Catalog);
        Check(malformed is { Agent: null, Model: null, ModelName: null, Effort: null }, "SA2 incomplete model identity cannot produce guessed display/effort");
        Check(OpenCodeChildSessionControl.Project("fixer", last with { Variant = " " }, empty, Catalog).Effort is null,
            "SA2 unknown malformed effort is hidden, not fabricated as default");
    }
}
