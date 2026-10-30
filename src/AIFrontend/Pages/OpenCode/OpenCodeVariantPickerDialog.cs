using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

// The ListView's native selection pill is the only selection chrome. The page still rechecks the exact basis.
internal static class OpenCodeVariantPickerDialog
{
    internal static async Task<OpenCodeModelPickerResult> ShowAsync(XamlRoot owner, OpenCodeModelPickerPresentation presentation,
        FrameworkElement? anchor = null)
    {
        var cancelled = new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Cancelled, null, null);
        anchor = OpenCodePickerFlyout.Anchor(owner, anchor, "OpenCodeSidebarVariant");
        var row = OpenCodeModelPresentation.CurrentRow(presentation);
        if (row is null || anchor is null) return cancelled;
        var state = new OpenCodeModelPickerState(presentation);
        state.SelectModel(row.ProviderId, row.ModelId);
        var original = presentation.Current switch { OpenCodeModelCurrent.Explicit e => e.Key.Variant, _ => null };
        state.SelectVariant(original);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true,
            Width = 168, MaxHeight = System.Math.Max(32, owner.Size.Height - 80) };
        AutomationProperties.SetAutomationId(list, "OpenCodeStrengthPickerOptions");
        AutomationProperties.SetName(list, "推理强度");
        list.Items.Add(new ListViewItem { Content = OpenCodeModelPresentation.VariantLabel(null), Tag = null, MinHeight = 34 });
        foreach (var variant in row.Variants)
            list.Items.Add(new ListViewItem { Content = OpenCodeModelPresentation.VariantLabel(variant), Tag = variant, MinHeight = 34 });
        list.SelectedIndex = original is null ? 0 : -1;
        for (var i = 1; i < list.Items.Count; i++)
            if (list.Items[i] is ListViewItem { Tag: string id } && id == original) list.SelectedIndex = i;
        try
        {
            return await OpenCodePickerFlyout.ShowAsync(anchor, list, cancelled, choose =>
                list.ItemClick += (_, e) =>
                {
                    if (OpenCodeNativeListChoice.ResolveItem(list, e.ClickedItem) is not { } item || !state.SelectVariant(item.Tag as string)) return;
                    choose(state.TryFreezeConfirmation() ?? cancelled);
                }, () => list.Focus(FocusState.Programmatic));
        }
        finally { state.Close(); }
    }
}
