// Explicit native flyout lifetime boundary. Agent ShowAsync/RowItem and page handler are production.
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace AIFrontend.Pages.OpenCode;
internal static class OpenCodePickerFlyout
{
    internal static FrameworkElement? Anchor(XamlRoot? owner, FrameworkElement? anchor, string id) => anchor;
    internal static Task<T> ShowAsync<T>(FrameworkElement anchor, FrameworkElement content, T cancelled,
        Action<Action<T>> wireChoice, Action? opened = null)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flyout = new Flyout { Content = content };
        wireChoice(value => { result.TrySetResult(value); flyout.Hide(); });
        flyout.Closed += (_, _) => result.TrySetResult(cancelled);
        flyout.ShowAt(anchor);
        opened?.Invoke();
        return result.Task;
    }
}
