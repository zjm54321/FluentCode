using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

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
        if (Environment.GetEnvironmentVariable("FC_SHELL_CAPTURE_SETTINGS_STATES") == "1") await CaptureSettingsStatesAsync(view, shot, log);
    }

    /// <summary>
    /// FC_SHELL_CAPTURE_SETTINGS_STATES=1: review states the category loop cannot reach — 外观 expanded at the top,
    /// 原生卡片 › 百度 as an unsaved draft, and 900/800 px windows. Nothing here saves settings.
    /// </summary>
    private async Task CaptureSettingsStatesAsync(Pages.Settings.SettingsView view, Func<string, Task> shot, Action<string> log)
    {
        async Task Category(int index, string name)
        {
            log($"state {name}: {view.TestSelectCategory(index)}");
            await Task.Delay(1200);
            log($"layout {name}: {view.TestLayoutReport()}");
            await shot(name);
            view.TestExpandAll(true);
            await Task.Delay(800);
            await shot(name + "-expanded-top");
            if (view.TestCanScroll) { view.TestScrollToEnd(); await Task.Delay(500); await shot(name + "-expanded"); }
            view.TestExpandAll(false);
        }

        log("state 70-settings-1-expanded-top: " + view.TestSelectCategory(1));
        await Task.Delay(1200);
        view.TestExpandAll(true);
        await Task.Delay(800);
        await shot("70-settings-1-expanded-top");
        view.TestExpandAll(false);

        log("state 71-settings-4-baidu: " + view.TestSelectCategory(4));
        await Task.Delay(1200);
        view.TestPreviewMapProvider(2); // NativeUiMapProvider.Baidu, draft only
        await Task.Delay(600);
        await shot("71-settings-4-baidu");
        view.TestRestoreMapProvider();

        foreach (int width in new[] { 900, 800 })
        {
            AppWindow.Resize(new SizeInt32(width, 900));
            await Task.Delay(1000);
            await Category(1, $"72-settings-{width}-1");
            await Category(3, $"72-settings-{width}-3");
        }
        AppWindow.Resize(new SizeInt32(1600, 900));
        await Task.Delay(500);
    }

    private static Pages.Settings.SettingsView? Find(DependencyObject root)
    {
        if (root is Pages.Settings.SettingsView found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i)) is { } nested) return nested;
        return null;
    }
}
