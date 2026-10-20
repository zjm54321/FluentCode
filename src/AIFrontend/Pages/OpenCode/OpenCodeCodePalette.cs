using System;
using System.Collections.Generic;
using AIFrontend.Models;
using AIFrontend.Services.Appearance;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Code-only live settings consumer. Loaded surfaces subscribe; Unloaded surfaces unsubscribe.
/// Cached/unloaded content reads the latest saved snapshot on its next Loaded, without rebuilding Markdown/images.</summary>
internal static class OpenCodeCodePalette
{
    private sealed class TextState { internal string Source = string.Empty; internal string Language = "text"; }
    private static readonly DependencyProperty TextStateProperty = DependencyProperty.RegisterAttached(
        "CodePaletteTextState", typeof(object), typeof(OpenCodeCodePalette), new PropertyMetadata(null));
    private static readonly DependencyProperty SurfaceBoundProperty = DependencyProperty.RegisterAttached(
        "CodePaletteSurfaceBound", typeof(bool), typeof(OpenCodeCodePalette), new PropertyMetadata(false));
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.Ordinal);
    private static AppCodeTheme Current => NormalPage.SharedSettingsService.Current?.CodeTheme ?? AppCodeTheme.Fluent;
    private static SolidColorBrush Brush(string color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
            Brushes[color] = brush = (SolidColorBrush)XamlReader.Load("<SolidColorBrush xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Color=\"" + color + "\"/>");
        return brush;
    }

    private static void Subscribe(FrameworkElement element, Action repaint)
    {
        var service = NormalPage.SharedSettingsService;
        var subscribed = false;
        var applied = Current;
        void Apply() { applied = Current; repaint(); }
        void Changed(object? sender, AIFrontend.Services.Configuration.AppSettingsChangedEventArgs args)
        {
            if ((service.Current?.CodeTheme ?? AppCodeTheme.Fluent) == applied) return;
            element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (subscribed && element.IsLoaded && Current != applied) Apply();
            });
        }
        element.Loaded += (_, _) =>
        {
            if (!subscribed) { service.Changed += Changed; subscribed = true; }
            Apply();
        };
        element.Unloaded += (_, _) => { if (subscribed) { service.Changed -= Changed; subscribed = false; } };
        if (element.IsLoaded) { service.Changed += Changed; subscribed = true; }
        Apply();
    }

    internal static void BindSurface(Border surface)
    {
        if (surface.GetValue(SurfaceBoundProperty) is true) return;
        surface.SetValue(SurfaceBoundProperty, true);
        // Restore caller-owned local values; shared styles/resources are never changed.
        var theme = surface.ReadLocalValue(FrameworkElement.RequestedThemeProperty);
        var background = surface.ReadLocalValue(Border.BackgroundProperty);
        var border = surface.ReadLocalValue(Border.BorderBrushProperty);
        Subscribe(surface, () =>
        {
            if (Current == AppCodeTheme.OneDarkPro)
            {
                surface.RequestedTheme = ElementTheme.Dark;
                surface.Background = Brush(CodePalette.Background);
                surface.BorderBrush = Brush(CodePalette.Border);
            }
            else
            {
                Restore(surface, FrameworkElement.RequestedThemeProperty, theme);
                Restore(surface, Border.BackgroundProperty, background);
                Restore(surface, Border.BorderBrushProperty, border);
            }
        });
    }

    private static void Restore(DependencyObject element, DependencyProperty property, object value)
    {
        if (value == DependencyProperty.UnsetValue) element.ClearValue(property);
        else element.SetValue(property, value);
    }

    internal static void BindText(TextBlock block, string source, string? language)
    {
        if (block.GetValue(TextStateProperty) is not TextState state)
        {
            state = new TextState();
            block.SetValue(TextStateProperty, state);
            var captured = state;
            Subscribe(block, () => Paint(block, captured));
        }
        state.Source = source;
        state.Language = OpenCodeFileDetailPolicy.NormalizeLanguage(language);
        Paint(block, state);
    }

    private static void Paint(TextBlock block, TextState state)
    {
        var dark = Current == AppCodeTheme.OneDarkPro;
        if (dark)
        {
            block.Foreground = Brush(CodePalette.Foreground);
            block.SelectionHighlightColor = Brush(CodePalette.Selection);
        }
        else
        {
            block.ClearValue(TextBlock.ForegroundProperty);
            block.ClearValue(TextBlock.SelectionHighlightColorProperty);
        }
        block.Text = string.Empty;
        block.Inlines.Clear();
        foreach (var (text, kind) in OpenCodeFileDetailPolicy.Tokens(state.Source, state.Language))
        {
            Run run;
            if (dark)
            {
                run = new Run();
                if (CodePalette.TokenColor(kind) is { } color) run.Foreground = Brush(color);
            }
            else
            {
                var key = kind switch
                {
                    "keyword" or "function" or "type" => OpenCodeToolTheme.Accent,
                    "string" => OpenCodeToolTheme.Success, "comment" => OpenCodeToolTheme.TextTertiary,
                    "number" => OpenCodeToolTheme.Attention, _ => null,
                };
                run = key is null ? new Run() : (Run)XamlReader.Load("<Run xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Foreground=\"{ThemeResource " + key + "}\"/>");
            }
            run.Text = text;
            block.Inlines.Add(run);
        }
    }
}
