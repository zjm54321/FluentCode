using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Whole-content floating card for one persistent terminal; same smoke, surface and geometry as the browser card.</summary>
internal sealed class OpenCodePersistentPtyOverlay : Grid
{
    private readonly FlyoutPresenter _presenter;
    private readonly Grid _content = new();
    // 外层卡片容器：只承载“从原位置展开”的合成层动画；presenter 自己的 Translation(Z=128) 用于阴影，二者分开。
    private readonly Grid _card = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    // 退出动画期间显示终端最后一帧（原生 HWND 不能参与动画，先隐藏再用截图跟着卡片一起收起）。
    private readonly Canvas _freeze = new() { IsHitTestVisible = false };
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
        _card.Children.Add(_presenter);
        _card.Children.Add(_freeze);
        Children.Add(_card);
        Motion = new AIFrontend.Motion.OverlayCardMotion(smoke, _card);
        AutomationProperties.SetAutomationId(this, "OpenCodePersistentPtyOverlay");
        AutomationProperties.SetName(this, "持久终端");
    }

    internal AIFrontend.Motion.OverlayCardMotion Motion { get; }

    internal UIElement? Content
    {
        get => _content.Children.Count == 0 ? null : _content.Children[0];
        set { _content.Children.Clear(); _freeze.Children.Clear(); if (value is not null) _content.Children.Add(value); }
    }

    /// <summary>在 <paramref name="area"/>（以卡片为参照）上方盖一张终端最后一帧，供退出动画使用；换内容时自动清除。</summary>
    internal void Freeze(Microsoft.UI.Xaml.Media.ImageSource frame, FrameworkElement area)
    {
        _freeze.Children.Clear();
        try
        {
            var bounds = area.TransformToVisual(_card).TransformBounds(new Windows.Foundation.Rect(0, 0, area.ActualWidth, area.ActualHeight));
            var image = new Image { Source = frame, Stretch = Stretch.Fill, Width = bounds.Width, Height = bounds.Height };
            Canvas.SetLeft(image, bounds.X); Canvas.SetTop(image, bounds.Y);
            _freeze.Children.Add(image);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    internal void SetSurfaceSize(double width, double height) { _presenter.Width = width; _presenter.Height = height; }

    private static void Theme(FrameworkElement target, string property, string key)
        => target.Style = (Style)XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\""
            + target.GetType().Name + "\"><Setter Property=\"" + property + "\" Value=\"{ThemeResource " + key + "}\"/></Style>");
}
