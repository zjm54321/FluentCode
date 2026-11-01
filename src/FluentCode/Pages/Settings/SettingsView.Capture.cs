using System.Linq;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

/// <summary>Capture hooks (FC_SHELL_CAPTURE_TEST only): select a category, scroll, expand previews, show in-place feedback.</summary>
public sealed partial class SettingsView
{
    internal int TestCategoryCount => _categories.Count;

    internal string TestSelectCategory(int index)
    {
        _categoryList.SelectedIndex = index;
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
        return $"view {ActualWidth:0}; root {Box(root)}; nav {Box((Microsoft.UI.Xaml.FrameworkElement)_categoryList.Parent)}; list {Box(_categoryList)}; scroll {Box(_categoryScroll)}; right {Box(right)}; host {Box(_categoryContentHost)}";
    }

    internal bool TestCanScroll => _categoryScroll.ScrollableHeight > 1;

    internal void TestExpandAll(bool expanded)
    {
        if (_categoryContentHost.Content is not Microsoft.UI.Xaml.DependencyObject root) return;
        foreach (var expander in Descendants(root).OfType<Expander>()) expander.IsExpanded = expanded;
    }

    /// <summary>Puts the browser Save button mid-✓ (as after a successful save) for the evidence screenshot.</summary>
    internal void TestShowSaveFeedback() => FluentCode.Motion.ActionFeedback.Success(_browserSave, "已保存浏览器设置");

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
