using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Services.Appearance;

internal static class ThemeResources
{
    private static readonly ConditionalWeakTable<FrameworkElement, List<Action>> Observers = new();
    private static readonly List<WeakReference<FrameworkElement>> Hosts = new();

    // Images without theme-resource properties can be skipped by WinUI's theme walk even though
    // ActualTheme subsequently reports the new value. Reconcile those hosts after the root changes.
    internal static void Observe(FrameworkElement host, Action refresh)
    {
        if (!Observers.TryGetValue(host, out var callbacks))
        {
            Observers.Add(host, callbacks = new());
            Hosts.Add(new(host));
        }
        callbacks.Add(refresh);
        host.Loaded += (_, _) => refresh();
        host.ActualThemeChanged += (_, _) => host.DispatcherQueue.TryEnqueue(() => refresh());
        refresh();
    }

    internal static void RefreshRoot(FrameworkElement root)
        => root.DispatcherQueue.TryEnqueue(() =>
        {
            for (var i = Hosts.Count - 1; i >= 0; i--)
            {
                if (!Hosts[i].TryGetTarget(out var host)) { Hosts.RemoveAt(i); continue; }
                if (host.XamlRoot is not null && host.XamlRoot == root.XamlRoot && Observers.TryGetValue(host, out var callbacks))
                    foreach (var refresh in callbacks) refresh();
            }
        });

    /// <summary>Resolve in the element's theme, not Application.RequestedTheme (which may differ).</summary>
    internal static Brush Brush(FrameworkElement scope, string key)
        => ResolveBrush(scope.ActualTheme, key);

    private static Brush ResolveBrush(ElementTheme theme, string key)
    {
        try
        {
            var probe = (Border)XamlReader.Load(
                "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" RequestedTheme=\""
                + theme + "\" Background=\"{ThemeResource " + key + "}\"/>");
            return probe.Background;
        }
        catch (System.Exception ex)
        {
            throw new System.InvalidOperationException($"Cannot resolve theme brush {key} ({theme}).", ex);
        }
    }

    /// <summary>Keep native template aliases in theme dictionaries so WinUI invalidates active visual states.</summary>
    internal static void Aliases(FrameworkElement scope, params (string Alias, string Key)[] aliases)
    {
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            var resources = new ResourceDictionary();
            foreach (var (alias, key) in aliases) resources[alias] = ResolveBrush(theme, key);
            scope.Resources.ThemeDictionaries[theme.ToString()] = resources;
        }
        // Do not shadow the native high-contrast template resources with normal-theme snapshots.
        scope.Resources.ThemeDictionaries["HighContrast"] = new ResourceDictionary();
    }
}
