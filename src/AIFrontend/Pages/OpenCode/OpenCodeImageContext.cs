using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Explicit subtree binding. Separate popup roots must be bound explicitly; there is no selected-session fallback.</summary>
internal static class OpenCodeImageContext
{
    private static readonly ConditionalWeakTable<DependencyObject, OpenCodeMarkdownImageScope> Scopes = new();

    internal static void Bind(DependencyObject subtree, OpenCodeMarkdownImageScope scope)
    {
        Scopes.Remove(subtree);
        Scopes.Add(subtree, scope);
    }

    internal static void Unbind(DependencyObject subtree) => Scopes.Remove(subtree);

    internal static OpenCodeMarkdownImageScope? Find(DependencyObject subtree)
    {
        for (DependencyObject? node = subtree; node is not null; node = VisualTreeHelper.GetParent(node))
            if (Scopes.TryGetValue(node, out var scope)) return scope;
        return null;
    }
}
