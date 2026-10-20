using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

// Independent native strength editor. Returns the same exact choice/basis contract as the model
// picker so normal sessions retain their existing coordinator preview/fingerprint/confirm fences.
// It is shown only AFTER the model dialog has closed, never as a nested ContentDialog.
internal static class OpenCodeVariantPickerDialog
{
    internal static async Task<OpenCodeModelPickerResult> ShowAsync(XamlRoot owner, OpenCodeModelPickerPresentation presentation)
    {
        var row = OpenCodeModelPresentation.CurrentRow(presentation);
        if (row is null) return new(OpenCodeModelPickerOutcome.Cancelled, null, null);
        var state = new OpenCodeModelPickerState(presentation);
        state.SelectModel(row.ProviderId, row.ModelId);
        var original = presentation.Current switch { OpenCodeModelCurrent.Explicit e => e.Key.Variant, _ => null };
        if (original is not null) state.SelectVariant(original); // Unoffered IDs never become selectable items.
        var options = new RadioButtons { MaxColumns = 4 };
        AutomationProperties.SetAutomationId(options, "OpenCodeStrengthPickerOptions");
        AutomationProperties.SetName(options, "推理强度");
        options.Items.Add(new RadioButton { Content = OpenCodeModelPresentation.VariantLabel(null), Tag = null });
        foreach (var variant in row.Variants)
            options.Items.Add(new RadioButton { Content = OpenCodeModelPresentation.VariantLabel(variant), Tag = variant });
        var selected = 0;
        for (var i = 1; i < options.Items.Count; i++)
            if (options.Items[i] is RadioButton { Tag: string id } && id == state.SelectedVariant) selected = i;
        options.SelectedIndex = selected;
        var body = new StackPanel { Spacing = 12, MinWidth = 240, MaxWidth = 480 };
        body.Children.Add(OpenCodeModelLogo.Label(row.ModelId, row.Name));
        body.Children.Add(options);
        var dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = "推理强度",
            Content = body,
            PrimaryButtonText = "确认修改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
            IsPrimaryButtonEnabled = state.CanConfirm,
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
        };
        options.SelectionChanged += (_, _) =>
        {
            if (state.IsClosed) return;
            if (options.SelectedItem is RadioButton item) state.SelectVariant(item.Tag as string);
            dialog.IsPrimaryButtonEnabled = state.CanConfirm;
        };
        OpenCodeModelPickerResult? frozen = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            frozen = state.TryFreezeConfirmation();
            if (frozen is null) args.Cancel = true;
        };
        try
        {
            var result = await dialog.ShowAsync();
            state.Close();
            return result == ContentDialogResult.Primary && frozen is not null
                ? frozen : new(OpenCodeModelPickerOutcome.Cancelled, null, null);
        }
        catch (Exception)
        {
            state.Close();
            return new(OpenCodeModelPickerOutcome.OwnerUnavailable, null, null);
        }
    }
}
