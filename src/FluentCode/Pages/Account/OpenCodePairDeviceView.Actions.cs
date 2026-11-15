using System;
using System.Threading.Tasks;
using FluentCode.Motion;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FluentCode.Pages.Account;

internal sealed partial class OpenCodePairDeviceView
{
    private void FillAddresses()
    {
        _addressBox.SelectionChanged -= OnAddressChanged;
        _addressBox.Items.Clear();
        foreach (var address in _addresses)
        {
            var label = address.Kind switch
            {
                OpenCodePairingAddressKind.Server => "服务地址",
                OpenCodePairingAddressKind.Lan => "局域网",
                _ => "这台电脑",
            };
            _addressBox.Items.Add(label + " · " + address.Origin);
        }

        _addressBox.Items.Add(CustomItem);
        var index = OpenCodePairingLinks.DefaultIndex(_addresses);
        _addressBox.SelectedIndex = index >= 0 ? index : _addresses.Count;
        _addressBox.SelectionChanged += OnAddressChanged;
        _open.IsEnabled = false;
        Render();
    }

    private bool CustomSelected => _addressBox.SelectedIndex >= _addresses.Count;

    // 选地址、改自定义地址：生成的链接直接替换框里的内容，之前对链接的修改不再保留。
    private void OnAddressChanged(object sender, SelectionChangedEventArgs e)
    {
        _carry = OpenCodePairingLinkCarry.None;
        Render();
    }

    private void OnCustomChanged(object sender, TextChangedEventArgs e)
    {
        if (_closed) return;
        // TextChanged 是异步触发的：自定义地址其实没变（生成的链接相同）时，不动用户对链接的修改。
        if (!string.Equals(_customBox.Text, _customText, StringComparison.Ordinal))
        {
            _customText = _customBox.Text;
            _carry = OpenCodePairingLinkCarry.None;
        }

        Render();
    }

    /// <summary>链接框的文字变了（用户输入，或程序写入后的回声）：只按新文字刷新，不回写。</summary>
    private void OnLinkChanged(object sender, TextChangedEventArgs e)
    {
        if (_closed) return;
        if (_keptText is not null && !string.Equals(_linkBox.Text, _keptText, StringComparison.Ordinal)) _keptText = null;
        RenderLink();
    }

    /// <summary>对话框的默认按钮是“关闭”：在链接框里按回车不应该把对话框关掉（配对码会跟着清空）。</summary>
    private void OnLinkKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) e.Handled = true;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_closed || _generated is not { } generated) return;
        _carry = OpenCodePairingLinkCarry.None;
        _keptText = null;
        _linkBox.Text = generated;
        RenderLink();
        // 按钮随即隐藏：把焦点交回链接框，键盘用户不会丢焦点。
        _linkBox.Focus(FocusState.Programmatic);
        ActionFeedback.Announce(_linkBox, "已恢复生成的链接");
    }

    /// <summary>一次显式操作 = 一次 POST。失败只显示原因，不重试。</summary>
    private async Task CreateAsync()
    {
        if (_busy || _closed || _client is not { } client) return;
        _busy = true;
        ClearCode();
        HideError();
        _renew.IsEnabled = false;
        _ring.Visibility = Visibility.Visible;
        _ring.IsActive = true;
        Render();
        try
        {
            var code = await client.CreatePairingCodeAsync(_cts.Token);
            if (_closed) return;
            _code = code;
            _expiresAtTick = Environment.TickCount64 + (long)code.Lifetime.TotalMilliseconds;
            _rule.Text = "链接只能用一次，" + OpenCodePairingLinks.DescribeLifetime(code.Lifetime) + "内有效。用过或过期后请重新生成。";
            _countdown.Text = "剩余 " + OpenCodePairingLinks.FormatRemaining(code.Lifetime);
            _timer.Start();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is OpenCodeException or InvalidOperationException or ObjectDisposedException)
        {
            if (_closed) return;
            ShowError(OpenCodePairingLinks.DescribeCreateError(error));
        }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                _ring.IsActive = false;
                _ring.Visibility = Visibility.Collapsed;
                _renew.IsEnabled = true;
                Render();
            }
        }
    }

    private void OnTimerTick(object? sender, object e)
    {
        if (_closed || _code is null) { _timer.Stop(); return; }
        var remaining = TimeSpan.FromMilliseconds(_expiresAtTick - Environment.TickCount64);
        if (remaining > TimeSpan.Zero)
        {
            _countdown.Text = "剩余 " + OpenCodePairingLinks.FormatRemaining(remaining);
            return;
        }

        ClearCode();
        Render();
        _countdown.Text = "配对码已过期。";
        ActionFeedback.Announce(_countdown, "配对码已过期");
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_link is not { } link) return;
        try
        {
            var data = new DataPackage();
            data.SetText(link);
            // 链接是一次性凭据：不进剪贴板历史，也不漫游到其他设备。
            Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
            ActionFeedback.Success(_copy, "已复制配对链接");
        }
        catch (Exception) { ActionFeedback.Failure(_copy, "复制失败；可选择链接后手动复制"); }
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_code is not { } code) return;
        try
        {
            var target = _edited ? _link : _ownOrigin is { } origin ? OpenCodePairingLinks.Compose(origin, code) : null;
            if (target is null) return;
            var uri = new Uri(target);
            // 链接交给浏览器后就算用掉了：先清掉，再打开。
            ClearCode();
            Render();
            _countdown.Text = "已交给浏览器打开。要连接其他设备，请重新生成。";
            if (!await Launcher.LaunchUriAsync(uri) && !_closed) ShowError("系统默认浏览器没有打开链接。请重新生成后复制链接手动打开。");
        }
        catch (Exception error) when (error is OpenCodeException or UriFormatException)
        {
            ShowError("这个地址无法组成有效的配对链接。");
        }
    }

    private async void OnRenewClick(object sender, RoutedEventArgs e) => await CreateAsync();
}
