using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;

namespace FluentCode.Pages.Account;

internal sealed partial class OpenCodePairDeviceView
{
    /// <summary>按当前地址与配对码刷新链接、二维码与说明。任何显示出来的链接都先经 Compose（内部用现有解析器复核）。</summary>
    private void Render()
    {
        if (_closed) return;
        _customBox.Visibility = CustomSelected ? Visibility.Visible : Visibility.Collapsed;
        string? origin = null;
        string? problem = null;
        var loopback = false;
        if (CustomSelected)
        {
            if (!OpenCodePairingLinks.TryNormalizeOrigin(_customBox.Text, out var custom, out var reason)) problem = reason;
            else { origin = custom; loopback = OpenCodePairingLinks.IsLoopbackOrigin(custom); }
            _addressHint.Text = _addresses.Count == 0
                ? "当前服务地址带路径，不能直接组成配对链接。请填其他设备能访问的服务根地址。FluentCode 不会检查它能否访问。"
                : "填其他设备能访问的服务根地址。FluentCode 不会检查它能否访问。";
        }
        else if (_addressBox.SelectedIndex >= 0)
        {
            var address = _addresses[_addressBox.SelectedIndex];
            origin = address.Origin;
            loopback = address.IsLoopback;
            _addressHint.Text = address.Kind switch
            {
                OpenCodePairingAddressKind.Server => "使用已配置的服务地址。",
                OpenCodePairingAddressKind.Lan =>
                    "同一局域网的设备可以用这个地址，但前提是 OpenCode 服务不只监听本机（在 WSL 里运行 opencode service set hostname 0.0.0.0）。FluentCode 无法替你确认。",
                _ => _connection == OpenCodePairingConnection.Ssh
                    ? "SSH 隧道只在这台电脑上可用。其他设备请选“自定义地址”，填它们能访问的地址。"
                    : "这个地址只能在这台电脑上打开。",
            };
        }

        _link = null;
        if (_code is { } code && origin is not null)
        {
            try { _link = OpenCodePairingLinks.Compose(origin, code); }
            catch (OpenCodeConfigurationException error) { problem = error.Message; }
        }

        _linkBox.Text = _link ?? string.Empty;
        _linkBox.Visibility = _link is null ? Visibility.Collapsed : Visibility.Visible;
        _copy.IsEnabled = _link is not null;
        _open.IsEnabled = _code is not null && _ownOrigin is not null;
        if (_link is not null && !loopback)
        {
            _qr.Show(_link, XamlRoot?.RasterizationScale ?? _fallbackScale);
            _qrNote.Text = string.Empty;
        }
        else
        {
            _qr.Clear();
            _qrNote.Text = _link is not null ? "这个地址只有这台电脑能打开，所以不显示二维码。"
                : _code is not null && problem is not null ? problem
                : string.Empty;
        }

        _qrNote.Visibility = _qrNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearCode()
    {
        _timer.Stop();
        _code = null;
        _link = null;
        _expiresAtTick = 0;
        _qr.Clear();
        _linkBox.Text = string.Empty;
        _linkBox.Visibility = Visibility.Collapsed;
        _countdown.Text = string.Empty;
        _copy.IsEnabled = false;
        _open.IsEnabled = false;
    }

    private void ShowError(string message)
    {
        if (_closed) return;
        _error.Message = message;
        // 先关后开：InfoBar 打开时按严重度播报一次。
        _error.IsOpen = false;
        _error.Visibility = Visibility.Visible;
        _error.IsOpen = true;
        _renew.IsEnabled = _client is not null;
    }

    private void HideError()
    {
        _error.IsOpen = false;
        _error.Visibility = Visibility.Collapsed;
    }
}
