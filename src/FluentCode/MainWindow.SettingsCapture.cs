using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentCode;

/// <summary>Settings evidence (FC_SHELL_CAPTURE_SETTINGS=1 with FC_SHELL_CAPTURE_TEST): every category light/dark, scrolled, expanded.</summary>
public sealed partial class MainWindow
{
    private async Task CaptureSettingsCategoriesAsync(Func<string, Task> shot, Action<string> log)
    {
        if (Environment.GetEnvironmentVariable("FC_SHELL_CAPTURE_SETTINGS") != "1") return;
        Pages.Settings.SettingsView? view = null;
        for (int i = 0; i < 50 && (view = Find(RootElement)) is null; i++) await Task.Delay(200);
        if (view is null) { log("FAIL settings view not found"); return; }
        for (int index = 0; index < view.TestCategoryCount; index++)
        {
            string title = view.TestSelectCategory(index);
            await Task.Delay(1200);
            string name = $"50-settings-{index}";
            log($"category {index}: {title} | {view.TestLayoutReport()}");
            await shot(name);
            view.TestExpandAll(true);
            await Task.Delay(800);
            if (view.TestCanScroll) { view.TestScrollToEnd(); await Task.Delay(500); }
            await shot(name + "-expanded");
            view.TestExpandAll(false);
            if (title == "浏览器")
            {
                var hold = Motion.ActionFeedback.SuccessHold;
                Motion.ActionFeedback.SuccessHold = TimeSpan.FromSeconds(8); // keep ✓ through both theme passes
                view.TestShowSaveFeedback();
                await shot("60-settings-save-feedback");
                Motion.ActionFeedback.SuccessHold = hold;
            }
        }
    }

    private static Pages.Settings.SettingsView? Find(DependencyObject root)
    {
        if (root is Pages.Settings.SettingsView found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i)) is { } nested) return nested;
        return null;
    }
}
