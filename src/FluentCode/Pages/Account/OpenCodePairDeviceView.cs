using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Appearance;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Account;

/// <summary>
/// “连接其他设备”：向当前服务要一个一次性配对码（POST api/pair），显示链接与二维码。
/// 每次显式操作（打开、重新生成）只发一次请求；不自动重试、不在后台续期，过期就显示已过期。
/// 配对码是凭据：只存在于本控件的字段与界面里，<see cref="Close"/> 时全部清空；从不记录或持久化。
/// </summary>
internal sealed partial class OpenCodePairDeviceView : UserControl
{
    private const string CustomItem = "自定义地址…";
    private readonly OpenCodePageSource _source;
    private readonly OpenCodePairingConnection _connection;
    private readonly CancellationTokenSource _cts;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<OpenCodePairingAddress> _addresses = new();
    private OpenCodeClient? _client;
    private OpenCodePairingCode? _code;
    private string? _link;
    private string? _ownOrigin;
    private long _expiresAtTick;
    private bool _busy;
    private bool _closed;
    private bool _started;
    private readonly double _fallbackScale;

    internal OpenCodePairDeviceView(OpenCodePageSource source, OpenCodePairingConnection connection, double fallbackScale = 1)
    {
        _fallbackScale = fallbackScale;
        _source = source;
        _connection = connection;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(source.RevocationToken);
        _timer.Tick += OnTimerTick;
        Content = BuildLayout();
        AutomationProperties.SetAutomationId(this, "OpenCodePairDevice");
    }

    /// <summary>打开时调用一次：读取本机局域网地址（仅 WSL），然后生成第一个配对码。</summary>
    internal async Task StartAsync()
    {
        if (_started || _closed) return;
        _started = true;
        try
        {
            _client = _source.CreateClient();
            var baseUrl = _client.DisplayBaseUrl;
            IReadOnlyList<System.Net.IPAddress>? lan = null;
            if (_connection == OpenCodePairingConnection.Wsl)
                lan = await Task.Run(OpenCodePairingLinks.ReadLanAddresses, _cts.Token);
            if (_closed) return;
            _addresses.AddRange(OpenCodePairingLinks.Candidates(_connection, baseUrl, lan));
            // “在本机浏览器打开”只用 FluentCode 自己正在用的地址：已知这台电脑能访问。
            _ownOrigin = _addresses.Count > 0 ? _addresses[0].Origin : null;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is InvalidOperationException or OpenCodeException or ObjectDisposedException)
        {
            ShowError(OpenCodePairingLinks.DescribeCreateError(error));
            return;
        }

        FillAddresses();
        await CreateAsync();
    }

    /// <summary>关闭：停止计时、取消请求、释放客户端，并把配对码从界面与内存清掉。</summary>
    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        ClearCode();
        _customBox.Text = string.Empty;
        _client?.Dispose();
        _client = null;
    }
}
