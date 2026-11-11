using System.Linq;
using CommunityToolkit.WinUI.Controls;

namespace FluentCode.Pages.Settings;

/// <summary>Capture hooks (FC_SHELL_CAPTURE_TEST only): select a category, scroll, expand previews, show in-place feedback.</summary>
public sealed partial class SettingsView
{
    internal int TestCategoryCount => _categories.Count;

    internal string TestSelectCategory(int index)
    {
        SelectCategory(index);
        return _categories[index].Title;
    }

    internal void TestScrollToEnd() => _categoryScroll.ChangeView(null, _categoryScroll.ScrollableHeight, null, disableAnimation: true);

    internal string TestLayoutReport()
    {
        string Box(Microsoft.UI.Xaml.FrameworkElement e)
        {
            var p = e.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
            return $"{e.GetType().Name}@{p.X:0},{p.Y:0} {e.ActualWidth:0}x{e.ActualHeight:0} desired={e.DesiredSize.Width:0}";
        }
        var root = (Microsoft.UI.Xaml.FrameworkElement)Content;
        var right = (Microsoft.UI.Xaml.FrameworkElement)_categoryScroll.Content;
        var pane = _categoryItems.Count > 0 ? _categoryItems[0] : (Microsoft.UI.Xaml.FrameworkElement)_navigation;
        return $"view {ActualWidth:0}; root {Box(root)}; list {Box(_navigation)}; nav {Box(pane)}; scroll {Box(_categoryScroll)}; right {Box(right)}; host {Box(_categoryContentHost)}";
    }

    internal bool TestCanScroll => _categoryScroll.ScrollableHeight > 1;

    internal void TestExpandAll(bool expanded)
    {
        if (_categoryContentHost.Content is not Microsoft.UI.Xaml.DependencyObject root) return;
        foreach (var expander in Descendants(root).OfType<SettingsExpander>()) expander.IsExpanded = expanded;
    }

    /// <summary>Puts the browser Save button mid-✓ (as after a successful save) for the evidence screenshot.</summary>
    internal void TestShowSaveFeedback() => FluentCode.Motion.ActionFeedback.Success(_browserSave, "已保存浏览器设置");

    /// <summary>Shows one map provider's cards as a clean draft (events suppressed: not dirty, Save stays off, nothing saved).</summary>
    internal void TestPreviewMapProvider(int provider)
    {
        var suppress = _suppressUiEvents; _suppressUiEvents = true;
        try { _mapProvider.SelectedIndex = provider; MapDraftChanged(); }
        finally { _suppressUiEvents = suppress; }
    }

    /// <summary>Puts the saved map settings back on screen after <see cref="TestPreviewMapProvider"/>.</summary>
    internal void TestRestoreMapProvider() => ApplyNativeUiMapToUi();

    private static System.Collections.Generic.IEnumerable<Microsoft.UI.Xaml.DependencyObject> Descendants(Microsoft.UI.Xaml.DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
