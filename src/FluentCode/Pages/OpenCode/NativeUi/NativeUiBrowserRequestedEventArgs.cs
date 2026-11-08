// SPDX-License-Identifier: GPL-3.0-or-later
using System;
namespace FluentCode.Pages.OpenCode;

internal sealed class NativeUiBrowserRequestedEventArgs(string parentMessageId, string partId, string url) : EventArgs
{
    internal string ParentMessageId { get; } = parentMessageId;
    internal string PartId { get; } = partId;
    internal string Url { get; } = url;
    internal bool Accepted { get; set; }
    internal string Notice { get; set; } = "当前会话浏览器不可用";
}
