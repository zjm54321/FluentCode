using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

internal static class OpenCodeNativeListChoice
{
    // WinUI ItemClick unwraps an explicit ListViewItem to its Content. Never substitute SelectedItem:
    // that could turn a stale/header/favorite gesture into a different row's command.
    internal static ListViewItem? ResolveItem(ListView list, object clicked)
    {
        var item = clicked as ListViewItem ?? list.ContainerFromItem(clicked) as ListViewItem
            ?? list.Items.OfType<ListViewItem>().FirstOrDefault(candidate => ReferenceEquals(candidate.Content, clicked));
        return item is { IsEnabled: true } && list.Items.Contains(item) ? item : null;
    }
    internal static T? Resolve<T>(ListView list, object clicked) where T : class => ResolveItem(list, clicked)?.Tag as T;
}
