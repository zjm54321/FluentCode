using System;
using FluentCode.Motion;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;

namespace FluentCode.Pages.Account;

internal sealed partial class OpenCodePairDeviceView
{
    /// <summary>
    /// 按当前地址与配对码算出“生成的链接”（经 Compose，用现有解析器复核）。它变了才改写链接框：
    /// 换地址直接替换框里的内容；换配对码时按 <see cref="OpenCodePairingLinkEdit.Apply"/> 把用户改的地址带过来。
    /// 用户正在输入时这里不碰链接框。
    /// </summary>
    private void Render()
    {
        if (_closed) return;
        _customBox.Visibility = CustomSelected ? Visibility.Visible : Visibility.Collapsed;
        string? origin = null;
        string? problem = null;
        if (CustomSelected)
        {
            if (!OpenCodePairingLinks.TryNormalizeOrigin(_customBox.Text, out var custom, out var reason)) problem = reason;
            else origin = custom;
            _addressHint.Text = _addresses.Count == 0
                ? "当前服务地址带路径，不能直接组成配对链接。请填其他设备能访问的服务根地址。FluentCode 不会检查它能否访问。"
                : "填其他设备能访问的服务根地址。FluentCode 不会检查它能否访问。";
        }
        else if (_addressBox.SelectedIndex >= 0)
        {
            var address = _addresses[_addressBox.SelectedIndex];
            origin = address.Origin;
            _addressHint.Text = address.Kind switch
            {
                OpenCodePairingAddressKind.Server => "使用已配置的服务地址。",
                OpenCodePairingAddressKind.Lan =>
                    "同一局域网的设备可以用这个地址，前提是 OpenCode 服务不只监听本机。如需修改，在 WSL 里运行 opencode service set hostname 0.0.0.0（它会先停止服务），再运行 opencode service start。FluentCode 无法替你确认。",
                _ => _connection == OpenCodePairingConnection.Ssh
                    ? "SSH 隧道只在这台电脑上可用。其他设备请选“自定义地址”，填它们能访问的地址。"
                    : "这个地址只能在这台电脑上打开。",
            };
        }

        string? generated = null;
        if (_code is { } code && origin is not null)
        {
            try { generated = OpenCodePairingLinks.Compose(origin, code); }
            catch (OpenCodeConfigurationException error) { problem = error.Message; }
        }

        _addressProblem = problem;
        if (!string.Equals(generated, _generated, StringComparison.Ordinal))
        {
            _generated = generated;
            _keptText = null;
            var text = generated ?? string.Empty;
            if (generated is not null && _code is { } current && !_carry.IsNone)
            {
                text = OpenCodePairingLinkEdit.Apply(_carry, generated, current.Code, out var kept);
                if (kept) _keptText = text;
                _carry = OpenCodePairingLinkCarry.None;
            }

            _linkBox.Text = text;
        }

        RenderLink();
    }

    /// <summary>
    /// 按链接框里现在的文字刷新二维码、状态行与按钮；只读不写链接框，所以不打断输入法。
    /// 二维码编码的就是框里的文字；文字不能用时不显示二维码，位置照留。
    /// </summary>
    private void RenderLink()
    {
        if (_closed) return;
        if (_code is not { } code || _generated is null)
        {
            _link = null;
            _edited = false;
            _linkPanel.Visibility = Visibility.Collapsed;
            _copy.IsEnabled = false;
            _open.IsEnabled = _code is not null && _ownOrigin is not null;
            _qr.Clear();
            SetQrNote(_code is not null && _addressProblem is { } problem ? problem : string.Empty);
            return;
        }

        var state = OpenCodePairingLinkEdit.Evaluate(_linkBox.Text, _generated, code.Code,
            renewed: _keptText is not null && string.Equals(_linkBox.Text, _keptText, StringComparison.Ordinal));
        _link = state.Link;
        _edited = state.IsEdited;
        _linkPanel.Visibility = Visibility.Visible;
        _copy.IsEnabled = state.Link is not null;
        // 没改过：仍用 FluentCode 自己在用的地址打开。改过：打开框里的链接，所见即所得。
        _open.IsEnabled = state.IsEdited ? state.Link is not null : _ownOrigin is not null;
        var note = state.QrNote;
        if (state.ShowQr)
        {
            try { _qr.Show(state.Link!, XamlRoot?.RasterizationScale ?? _fallbackScale); }
            catch (ArgumentException)
            {
                _qr.Clear();
                note = "链接太长，无法生成二维码。";
            }
        }
        else
        {
            _qr.Clear();
        }

        SetQrNote(note);
        _linkStatus.Text = state.Status;
        var style = OpenCodeInteractionUi.TryStyle(state.IsProblem ? OpenCodeInteractionUi.QuestionErrorStyleKey : OpenCodeInteractionUi.CaptionSubtleStyleKey);
        if (!ReferenceEquals(_linkStatus.Style, style)) _linkStatus.Style = style;
        _restore.Visibility = state.IsEdited ? Visibility.Visible : Visibility.Collapsed;
        if (_statusKind == state.Kind) return;
        // 状态变了才播报一次（不是每敲一个字）；刚生成、还没动过的链接不播报提示。
        var first = _statusKind is null;
        _statusKind = state.Kind;
        if (!first || state.Kind != OpenCodePairingLinkEditKind.Generated) ActionFeedback.Announce(_linkStatus, state.Status);
    }

    /// <summary>
    /// 二维码位置的收尾：正在生成或已有配对码时留出整块位置，二维码没显示就放空底板（里面是原因）；
    /// 其余时候（出错、已过期）收回到原来的高度。
    /// </summary>
    private void SetQrNote(string text)
    {
        var reserved = _busy || _code is not null;
        _stage.MinHeight = reserved ? OpenCodePairQrCode.PlateSize : IdleStageHeight;
        _qrSlot.Visibility = reserved && _qr.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        _qrNote.Text = text;
        _qrNote.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearCode()
    {
        _timer.Stop();
        // 配对码作废前记下用户对链接的修改；下一个配对码到手时放回去。还没显示过链接时，保留已经在等的修改。
        if (_code is { } old && _generated is not null && !_closed) _carry = OpenCodePairingLinkEdit.Capture(_linkBox.Text, _generated, old.Code);
        _code = null;
        _link = null;
        _generated = null;
        _keptText = null;
        _statusKind = null;
        _edited = false;
        _expiresAtTick = 0;
        _qr.Clear();
        _linkBox.Text = string.Empty;
        _linkPanel.Visibility = Visibility.Collapsed;
        _restore.Visibility = Visibility.Collapsed;
        _linkStatus.Text = string.Empty;
        SetQrNote(string.Empty);
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
        if (!_busy && _code is null) SetQrNote(string.Empty);
    }

    private void HideError()
    {
        _error.IsOpen = false;
        _error.Visibility = Visibility.Collapsed;
    }
}
