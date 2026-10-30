using System;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodeBrowserButton : Grid
{
    private readonly ToggleButton _toggle = new() { Width = 40, Height = 36, MinWidth = 0, Padding = new Thickness(0) };
    private readonly FontIcon _globe = new() { Glyph = "\uE774", FontFamily = new("Segoe Fluent Icons"), FontSize = OpenCodeQuickAccessStyle.IconSize };
    private readonly Border _statusDot = new() { Width = 6, Height = 6, CornerRadius = new(3), IsHitTestVisible = false };
    private readonly FrameworkElement _coloredBrowser = OpenCodeBrowserColorIcon.Create();
    private readonly InfoBadge _count = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly Button _statusButton = new() { Width = 12, Height = 12, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new(0), Margin = new(0, 2, 2, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _new = new() { Text = "NEW", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
    private readonly TextBlock _tipTitle = new();
    private readonly TextBlock _tipStatus = new();
    private readonly Border _dot = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
    private readonly HyperlinkButton _reclaim = new() { Padding = new Thickness(0) };
    private readonly TextBlock _reason = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
    private readonly Flyout _availabilityFlyout;
    public event EventHandler? Invoked;
    public event EventHandler? NewPageRequested;
    public event EventHandler? ReclaimRequested;

    public OpenCodeBrowserButton()
    {
        ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = new Grid { Width = 36, Height = 32, Children = { _globe, _coloredBrowser, _count } };
        _toggle.Content = icon;
        // The WSL explorer uses DefaultButtonStyle + transparent background. Keep that same
        // native frame, padding and corner geometry on the native ToggleButton template.
        _toggle.Style = (Style)XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"ToggleButton\" BasedOn=\"{StaticResource DefaultToggleButtonStyle}\"><Setter Property=\"Background\" Value=\"{ThemeResource SubtleFillColorTransparentBrush}\"/><Setter Property=\"BorderBrush\" Value=\"{ThemeResource ButtonBorderBrush}\"/><Setter Property=\"BorderThickness\" Value=\"{ThemeResource ButtonBorderThemeThickness}\"/><Setter Property=\"CornerRadius\" Value=\"{ThemeResource ControlCornerRadius}\"/></Style>");
        // Native ToggleButton chrome with subtle checked states, never the stock accent-filled state.
        _toggle.Resources["ToggleButtonBackgroundChecked"] = Brush("SubtleFillColorSecondaryBrush");
        _toggle.Resources["ToggleButtonBackgroundCheckedPointerOver"] = Brush("SubtleFillColorSecondaryBrush");
        _toggle.Resources["ToggleButtonBackgroundCheckedPressed"] = Brush("SubtleFillColorTertiaryBrush");
        _toggle.Resources["ToggleButtonForegroundChecked"] = Brush("TextFillColorPrimaryBrush");
        _toggle.Resources["ToggleButtonForegroundCheckedPointerOver"] = Brush("TextFillColorPrimaryBrush");
        _statusButton.Content = _statusDot;
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } quiet) _statusButton.Style = quiet;
        Children.Add(_toggle); Children.Add(_statusButton); Children.Add(_new); Grid.SetColumn(_new, 1);
        AutomationProperties.SetName(_toggle, "浏览器"); AutomationProperties.SetAutomationId(_toggle, "OpenCodeBrowserButton");
        _toggle.Click += (_, _) =>
        {
            if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0) NewPageRequested?.Invoke(this, EventArgs.Empty);
            else Invoked?.Invoke(this, EventArgs.Empty);
        };
        _toggle.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(_toggle).Properties.IsMiddleButtonPressed) return;
            e.Handled = true; NewPageRequested?.Invoke(this, EventArgs.Empty);
        }), true);
        ToolTipService.SetToolTip(_toggle, new ToolTip { Content = new StackPanel { Spacing = 4, Children = { _tipTitle,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _dot, _tipStatus } } } } });
        _availabilityFlyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { _reason, _reclaim } } };
        _statusButton.Flyout = _availabilityFlyout;
        _reclaim.Click += (_, _) => { _availabilityFlyout.Hide(); ReclaimRequested?.Invoke(this, EventArgs.Empty); };
        ActualThemeChanged += (_, _) => RefreshTheme();
    }

    private int _pages;
    private OpenCodeBrowserAvailability _availability;
    private void RefreshTheme()
    {
        var colored = _toggle.IsChecked == true && OpenCodeBrowserPanelState.ColoredIcon(_pages)
            && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        _coloredBrowser.Visibility = colored ? Visibility.Visible : Visibility.Collapsed;
        _globe.Visibility = colored ? Visibility.Collapsed : Visibility.Visible;
        // Inherit the tile's themed foreground: an Application-level brush is the app theme's, which made the globe
        // white-on-white when the page theme differed from the app theme.
        _globe.ClearValue(IconElement.ForegroundProperty);
        _dot.Background = _statusDot.Background = Brush(OpenCodeBrowserPanelState.StatusBrushKey(_availability));
        _toggle.Resources["ToggleButtonBackgroundChecked"] = Brush("SubtleFillColorSecondaryBrush");
        _toggle.Resources["ToggleButtonBackgroundCheckedPointerOver"] = Brush("SubtleFillColorSecondaryBrush");
        _toggle.Resources["ToggleButtonBackgroundCheckedPressed"] = Brush("SubtleFillColorTertiaryBrush");
        _toggle.Resources["ToggleButtonForegroundChecked"] = Brush("TextFillColorPrimaryBrush");
        _toggle.Resources["ToggleButtonForegroundCheckedPointerOver"] = Brush("TextFillColorPrimaryBrush");
        _toggle.Resources["ToggleButtonForegroundCheckedPressed"] = Brush("TextFillColorPrimaryBrush");
        _toggle.Resources["ToggleButtonBorderBrushChecked"] = Brush("ButtonBorderBrush");
        _toggle.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = Brush("ButtonBorderBrushPointerOver");
        _toggle.Resources["ToggleButtonBorderBrushCheckedPressed"] = Brush("ButtonBorderBrushPressed");
    }

    public void Render(IOpenCodeBrowserWorkspace? workspace, OpenCodeBrowserPanelState? state, bool seen, int? pageCount = null)
    {
        _pages = pageCount ?? workspace?.Pages.Count ?? 0; _availability = workspace?.Availability ?? OpenCodeBrowserAvailability.Detached;
        _toggle.IsEnabled = workspace is not null;
        _toggle.IsChecked = state?.ShowCommands(_pages) == true;
        _count.Value = _pages; _count.Visibility = OpenCodeBrowserPanelState.ShowCount(_pages) ? Visibility.Visible : Visibility.Collapsed;
        _new.Visibility = seen ? Visibility.Collapsed : Visibility.Visible;
        _statusButton.Visibility = workspace is not null ? Visibility.Visible : Visibility.Collapsed;
        _tipTitle.Text = OpenCodeBrowserPanelState.TooltipTitle(_pages);
        _tipStatus.Text = OpenCodeBrowserPanelState.TooltipStatus(workspace?.Availability ?? OpenCodeBrowserAvailability.Detached, workspace?.AvailabilityReason);
        _reason.Text = _tipStatus.Text;
        AutomationProperties.SetName(_toggle, _tipTitle.Text + " · " + _tipStatus.Text);
        AutomationProperties.SetName(_statusButton, "浏览器连接状态 · " + _tipStatus.Text);
        var reclaim = OpenCodeBrowserPanelState.ReclaimLabel(workspace?.Availability ?? OpenCodeBrowserAvailability.Detached);
        _reclaim.Content = reclaim; _reclaim.Visibility = reclaim is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(_statusButton, _tipStatus.Text);
        RefreshTheme();
    }

    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
