using FluentCode.Pages.OpenCode;
using FluentCode.Services.Appearance;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Account;

internal sealed partial class OpenCodePairDeviceView
{
    private readonly ComboBox _addressBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _customBox = new() { PlaceholderText = "http://192.168.1.20:4096", Visibility = Visibility.Collapsed };
    private readonly TextBlock _addressHint = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
    private readonly OpenCodePairQrCode _qr = new();
    private readonly TextBlock _qrNote = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodySubtleStyleKey);
    private readonly ProgressRing _ring = new() { Width = 24, Height = 24, IsActive = false, Visibility = Visibility.Collapsed };
    // 系统自带的 TextBox：全选、复制、粘贴、撤销与输入法都按系统行为走；这里只读它的 Text。
    private readonly TextBox _linkBox = new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        MaxLength = OpenCodePairingLinkEdit.MaxLength * 2,
    };
    private readonly TextBlock _linkStatus = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
    private readonly HyperlinkButton _restore = new()
    {
        Content = "恢复原链接",
        FontSize = 12,
        Padding = new Thickness(4, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly StackPanel _linkPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly Grid _stage = new() { MinHeight = OpenCodePairQrCode.PlateSize };

    /// <summary>二维码不显示时占着同一块位置的空底板（与二维码底板同大同圆角），里面写原因。</summary>
    private readonly Border _qrSlot = new()
    {
        Width = OpenCodePairQrCode.PlateSize,
        Height = OpenCodePairQrCode.PlateSize,
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(20),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Style = OpenCodeInteractionUi.TryStyle("OpenCodeSubtleCardChromeStyle"),
    };
    private readonly Button _copy = new() { Content = "复制链接", IsEnabled = false };
    private readonly Button _open = new() { Content = "在这台电脑的浏览器打开", IsEnabled = false };
    private readonly Button _renew = new() { Content = "重新生成", IsEnabled = false };
    private readonly TextBlock _countdown = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStrongStyleKey);
    private readonly TextBlock _rule = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error, Visibility = Visibility.Collapsed };

    private UIElement BuildLayout()
    {
        AutomationProperties.SetAutomationId(_addressBox, "OpenCodePairAddress");
        AutomationProperties.SetName(_addressBox, "链接使用的地址");
        AutomationProperties.SetAutomationId(_customBox, "OpenCodePairCustomAddress");
        AutomationProperties.SetName(_customBox, "自定义地址");
        AutomationProperties.SetAutomationId(_linkBox, "OpenCodePairLink");
        AutomationProperties.SetName(_linkBox, "配对链接");
        AutomationProperties.SetAutomationId(_linkStatus, "OpenCodePairLinkStatus");
        // 讲述人停在链接框上时，把下面这句状态一起读出来。
        AutomationProperties.GetDescribedBy(_linkBox).Add(_linkStatus);
        AutomationProperties.SetAutomationId(_restore, "OpenCodePairRestore");
        AutomationProperties.SetName(_restore, "恢复生成的链接");
        ToolTipService.SetToolTip(_restore, "换回 FluentCode 生成的链接");
        AutomationProperties.SetAutomationId(_copy, "OpenCodePairCopy");
        AutomationProperties.SetAutomationId(_open, "OpenCodePairOpenBrowser");
        AutomationProperties.SetAutomationId(_renew, "OpenCodePairRenew");
        AutomationProperties.SetAutomationId(_countdown, "OpenCodePairCountdown");
        AutomationProperties.SetAutomationId(_error, "OpenCodePairError");
        AutomationProperties.SetName(_ring, "正在生成配对码");
        // 倒计时每秒变化：明确关闭实时区域，讲述人不会逐秒朗读；过期时另行播报一次。
        AutomationProperties.SetLiveSetting(_countdown, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Off);
        AppFonts.Apply(_linkBox, AppFontSlot.Monospace);
        _qrNote.HorizontalAlignment = HorizontalAlignment.Center;
        _qrNote.TextAlignment = TextAlignment.Center;
        _ring.HorizontalAlignment = HorizontalAlignment.Center;
        if (OpenCodeInteractionUi.TryStyle("AccentButtonStyle") is { } accent) _copy.Style = accent;

        _addressBox.SelectionChanged += OnAddressChanged;
        _customBox.TextChanged += OnCustomChanged;
        _linkBox.TextChanged += OnLinkChanged;
        _linkBox.KeyDown += OnLinkKeyDown;
        _restore.Click += OnRestoreClick;
        _copy.Click += OnCopyClick;
        _open.Click += OnOpenClick;
        _renew.Click += OnRenewClick;

        // 二维码的位置一直留着：输入过程中二维码出现、消失或换成一句说明，下面的内容都不会上下跳。
        _qrSlot.Child = _qrNote;
        _stage.Children.Add(_qrSlot);
        _stage.Children.Add(_qr);
        _stage.Children.Add(_ring);
        _qrNote.VerticalAlignment = VerticalAlignment.Center;
        _ring.VerticalAlignment = VerticalAlignment.Center;

        var timeRow = new Grid { ColumnSpacing = 12 };
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _countdown.VerticalAlignment = VerticalAlignment.Center;
        timeRow.Children.Add(_countdown);
        Grid.SetColumn(_renew, 1);
        timeRow.Children.Add(_renew);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_copy);
        actions.Children.Add(_open);

        // 状态行高度固定：“恢复原链接”出现或消失不改变行高。
        var statusRow = new Grid { ColumnSpacing = 8, MinHeight = 20 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _linkStatus.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(_linkStatus);
        Grid.SetColumn(_restore, 1);
        statusRow.Children.Add(_restore);
        _linkPanel.Children.Add(_linkBox);
        _linkPanel.Children.Add(statusRow);

        var address = new StackPanel { Spacing = 6 };
        address.Children.Add(OpenCodeInteractionUi.StyledText("链接使用的地址", OpenCodeInteractionUi.BodyStrongStyleKey));
        address.Children.Add(_addressBox);
        address.Children.Add(_customBox);
        address.Children.Add(_addressHint);

        var root = new StackPanel { Spacing = 16, Width = 420 };
        root.Children.Add(OpenCodeInteractionUi.StyledText(
            "用手机扫码，或在另一台设备的浏览器里打开链接，就能连到这个 OpenCode 服务，不用输入密码。",
            OpenCodeInteractionUi.BodyStyleKey));
        root.Children.Add(address);
        root.Children.Add(_error);
        root.Children.Add(_stage);
        root.Children.Add(_linkPanel);
        root.Children.Add(actions);
        root.Children.Add(timeRow);
        root.Children.Add(_rule);
        return root;
    }
}
