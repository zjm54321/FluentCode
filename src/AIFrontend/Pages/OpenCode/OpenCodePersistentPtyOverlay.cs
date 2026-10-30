using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Whole-content floating card for one persistent terminal; same smoke, surface and geometry as the browser card.</summary>
internal sealed class OpenCodePersistentPtyOverlay : Grid
{
    private readonly FlyoutPresenter _presenter;
    private readonly Grid _content = new();
    internal event EventHandler? CloseRequested;

    internal OpenCodePersistentPtyOverlay()
    {
        var smoke = new Border(); Theme(smoke, "Background", "SmokeFillColorDefaultBrush");
        smoke.Tapped += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Children.Add(smoke);
        _presenter = new FlyoutPresenter
        {
            Content = _content, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
            MaxWidth = double.PositiveInfinity, MaxHeight = double.PositiveInfinity, IsTabStop = false,
            Translation = new Vector3(0, 0, 128), Shadow = new ThemeShadow(),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollMode(_presenter, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_presenter, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(_presenter, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_presenter, ScrollBarVisibility.Disabled);
        // The presenter template's ScrollViewer defaults to Left/Top; stretch it so the terminal fills the card.
        _presenter.Resources = (ResourceDictionary)XamlReader.Load(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Style TargetType=\"ScrollViewer\" BasedOn=\"{StaticResource DefaultScrollViewerStyle}\"><Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\"/><Setter Property=\"VerticalContentAlignment\" Value=\"Stretch\"/></Style></ResourceDictionary>");
        _presenter.CornerRadius = new CornerRadius(8);
        _presenter.BorderThickness = new Thickness(OpenCodeBrowserOverlayLayout.BorderWidth);
        _presenter.Style = (Style)XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"FlyoutPresenter\" BasedOn=\"{StaticResource DefaultFlyoutPresenterStyle}\"><Setter Property=\"Background\" Value=\"{ThemeResource SolidBackgroundFillColorTertiaryBrush}\"/></Style>");
        _presenter.Transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromHorizontalOffset = 0, FromVerticalOffset = 24, IsStaggeringEnabled = false },
        };
        Children.Add(_presenter);
        AutomationProperties.SetAutomationId(this, "OpenCodePersistentPtyOverlay");
        AutomationProperties.SetName(this, "持久终端");
    }

    internal UIElement? Content
    {
        get => _content.Children.Count == 0 ? null : _content.Children[0];
        set { _content.Children.Clear(); if (value is not null) _content.Children.Add(value); }
    }

    internal void SetSurfaceSize(double width, double height) { _presenter.Width = width; _presenter.Height = height; }

    private static void Theme(FrameworkElement target, string property, string key)
        => target.Style = (Style)XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\""
            + target.GetType().Name + "\"><Setter Property=\"" + property + "\" Value=\"{ThemeResource " + key + "}\"/></Style>");
}
