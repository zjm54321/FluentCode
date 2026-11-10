using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

// Presentation-only host. Dismissal produces no choice; callers retain their existing authority checks.
internal static class OpenCodePickerFlyout
{
    internal static FrameworkElement? Anchor(XamlRoot? owner, FrameworkElement? anchor, string sidebarId)
    {
        if (owner is null) return null;
        if (anchor?.XamlRoot == owner) return anchor;
        // Existing callers need not change: preserve the actual invoking button across their catalog awaits.
        if (FocusManager.GetFocusedElement(owner) is FrameworkElement focused
            && focused is ButtonBase && focused.ActualWidth > 0) return focused;
        return Find(owner.Content, sidebarId) ?? Find(owner.Content, sidebarId switch
        {
            "OpenCodeSidebarAgent" => "OpenCodeBlankAgent",
            "OpenCodeSidebarModel" => "OpenCodeBlankModel",
            _ => "OpenCodeBlankStrength",
        });
    }

    private static FrameworkElement? Find(DependencyObject root, string id)
    {
        if (root is FrameworkElement { Visibility: Visibility.Collapsed }) return null;
        if (root is FrameworkElement element && element.ActualWidth > 0 && element.ActualHeight > 0
            && element.Visibility == Visibility.Visible && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), id) is { } child) return child;
        return null;
    }

    internal static async Task<T> ShowAsync<T>(FrameworkElement anchor, FrameworkElement content, T cancelled,
        Action<Action<T>> wireChoice, Action? opened = null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
            LightDismissOverlayMode = LightDismissOverlayMode.Off };
        var presenter = (Style)XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"FlyoutPresenter\"><Setter Property=\"CornerRadius\" Value=\"{ThemeResource OverlayCornerRadius}\"/><Setter Property=\"FontFamily\" Value=\"{ThemeResource AppBodyFontFamily}\"/></Style>");
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        presenter.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, Math.Max(1, anchor.XamlRoot.Size.Width - 24)));
        flyout.FlyoutPresenterStyle = presenter;
        wireChoice(value => { completion.TrySetResult(value); flyout.Hide(); });
        flyout.Closed += (_, _) => completion.TrySetResult(cancelled);
        flyout.Opened += (_, _) => opened?.Invoke();
        void Unloaded(object sender, RoutedEventArgs args)
        {
            if (anchor.IsLoaded) return; // Ignore the previous parent's late unload.
            completion.TrySetResult(cancelled);
            flyout.Hide();
        }
        anchor.Unloaded += Unloaded;
        try
        {
            flyout.ShowAt(anchor);
            return await completion.Task;
        }
        catch (Exception) { return cancelled; }
        finally { anchor.Unloaded -= Unloaded; }
    }
}
