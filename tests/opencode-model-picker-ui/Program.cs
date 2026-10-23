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
            ("anthropic/custom-model", OpenCodeModelBrand.Neutral), ("google/custom-model", OpenCodeModelBrand.Neutral),
            ("openai/custom-model", OpenCodeModelBrand.Neutral), ("notgpt-custom", OpenCodeModelBrand.Neutral),
            ("claudette", OpenCodeModelBrand.Neutral), ("geminization", OpenCodeModelBrand.Neutral) })
            Check(OpenCodeModelPresentation.Brand(sample.Item1, null) == sample.Item2, "explicit proxy family/unknown fallback: " + sample.Item1);
        Check(OpenCodeModelPresentation.Brand("opaque-id", "Claude Sonnet") == OpenCodeModelBrand.Claude, "friendly family name works behind opaque catalog ID");
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
        Check(dialog.Contains("PrimaryButtonText = \"确认修改\"") && dialog.Contains("CloseButtonText = \"取消\"")
            && dialog.Contains("重新读取失败；列表保持不变。") && dialog.Contains("InformativeNote(p.Note)"), "confirmation, cancellation and real failure notes retained");
        Check(dialog.Contains("OpenCodeModelPresentation.Order(p.Rows, sourceKey)") && !dialog.Contains("await OpenCodeRecentModels.Shared.LoadAsync()"), "dialog only reads in-memory shared MRU and never loads preferences while rendering");
        var blank = Read("src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs");
        Check(blank.Contains("OpenCodeModelLogo.Label(model?.Id, model?.Name") && blank.Contains("ToolTipService.SetToolTip(BlankModelButton, model?.Name)"), "composer brand label and clean friendly tooltip");
        Check(blank.Contains("sourceKey: source.Identity"), "blank picker uses captured stable source identity");
        Check(blank.Contains("allowStrengthSelection: false"), "blank picker uses its existing dedicated strength control instead of duplicate dialog entry");
        Check(blank.Contains("await OpenCodeRecentModels.Shared.LoadAsync();\n            if (operation != _blankPickerEpoch || !OwnsHome(source, client, basis.RuntimeSourceId)\n                || !OwnsBlankPreferences(basis, token) || _newSessionCreatePending) return;"), "blank picker revalidates captured source/token/epoch immediately after MRU load await");
        var strengthStart = blank.IndexOf("private async void BlankStrengthButton_Click", StringComparison.Ordinal);
        var strengthProducer = blank.Substring(strengthStart, blank.IndexOf("internal const string BlankVariantRadioGroup", StringComparison.Ordinal) - strengthStart);
        Check(strengthProducer.Contains("foreach (var (variantId, label, isChecked) in BlankVariantMenuEntries(model, basis.Model.Variant))"), "blank strength menu is produced only from default plus catalog-offered entries");
        Check(strengthProducer.Contains("new RadioMenuFlyoutItem { Text = label, GroupName = BlankVariantRadioGroup, IsChecked = isChecked }")
            && !blank.Contains("ToggleMenuFlyoutItem"), "blank strength menu uses native WinUI radio items in one group, not checkbox toggles");
        Check(strengthProducer.Contains("item.Click += async (_, _) => { if (variantId != basis.Model.Variant) await SetBlankVariantAsync(basis, token, variantId); };")
            && !strengthProducer.Contains("Checked +=") && !strengthProducer.Contains("Unchecked +=") && !strengthProducer.Contains("RegisterPropertyChangedCallback"),
            "only an explicit click on a different item commits, via the guarded SetBlankVariantAsync entry");
        Check(blank.Contains("await SetBlankVariantAsync(basis, token, variantId)") && blank.Contains("var fresh = await client.ListModelsAsync(basis.Directory") && blank.Contains("VariantAvailable(model, variantId)"), "variant gesture rechecks fresh catalog");
        Check(blank.Contains("Model = basis.Model with { Variant = variantId }") && blank.Contains("choice.Variant is { } variant && !(offered.Variants?.Any"), "opaque ID commit and model-dialog fresh variant validation retained");
        var logo = Read("src/AIFrontend/Pages/OpenCode/OpenCodeModelLogo.cs");
        Check(logo.Contains("Text = \"AI\"") && logo.Contains("host.ActualThemeChanged += Update") && logo.Contains("ticket != revision") && logo.Contains("AppContext.BaseDirectory"), "neutral fallback, theme updates and stale async-load fence");
        foreach (var brand in new[] { OpenCodeModelBrand.Claude, OpenCodeModelBrand.Gemini, OpenCodeModelBrand.OpenAI })
            foreach (var dark in new[] { false, true })
            {
                var path = Path.Combine(root, "src/AIFrontend/Assets/Brands", OpenCodeModelPresentation.Asset(brand, dark)!);
                Check(File.Exists(path), "brand asset exists: " + path);
                if (path.EndsWith(".svg")) Check(XDocument.Load(path).Root?.Name.LocalName == "svg", "valid SVG document");
            }
        var project = XDocument.Parse(Read("src/AIFrontend/AIFrontend.csproj"));
        Check(project.Descendants("Content").Any(n => (string?)n.Attribute("Include") == @"Assets\Brands\**\*"
            && (string?)n.Attribute("CopyToOutputDirectory") == "PreserveNewest" && (string?)n.Attribute("CopyToPublishDirectory") == "PreserveNewest"), "brand resources copied to build/publish output");
        var strengthDialog = Read("src/AIFrontend/Pages/OpenCode/OpenCodeVariantPickerDialog.cs");
        Check(strengthDialog.Contains("OpenCodeModelPickerState(presentation)") && strengthDialog.Contains("state.SelectModel(row.ProviderId, row.ModelId)")
            && strengthDialog.Contains("foreach (var variant in row.Variants)"), "independent strength editor has only current-model catalog variants");
        Check(strengthDialog.Contains("VariantLabel(null), Tag = null") && strengthDialog.Contains("VariantLabel(variant), Tag = variant")
            && strengthDialog.Contains("state.TryFreezeConfirmation()"), "independent default/Chinese display keep wire IDs and frozen explicit confirmation");
        AIFrontend.Pages.OpenCodePage.RunVariantSelectionChecks(Check).GetAwaiter().GetResult();
        var cardBinding = Read("src/AIFrontend/Pages/OpenCodePage.SessionControlCard.cs");
        Check(cardBinding.Contains("_agentSelector.DisplayChanged +=") && cardBinding.Contains("_agentSelector.CurrentModel")
            && cardBinding.Contains("_blankPreferences") && !cardBinding.Contains("GetSessionInfoAsync") && !cardBinding.Contains("ListModelsAsync"),
            "sidebar reads the same session/blank owners, never adds a second selection cache or data fetch");
        Check(cardBinding.Contains("BlankAgentButton_Click(sender, e)") && cardBinding.Contains("BlankModelButton_Click(sender, e)")
            && cardBinding.Contains("BlankStrengthButton_Click(sender, e)") && cardBinding.Contains("尚未保存为会话选择"),
            "blank sidebar delegates to existing draft semantics and labels its unpersisted scope");
        Console.WriteLine($"PASS A2 picker UI: {checks} checks; production display/state/selection + structural native UI/resource checks (no native rendering)");
    }
}
