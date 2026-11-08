// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using FluentCode.Services.NativeUi;

namespace FluentCode.Pages.OpenCode;

internal sealed class NativeUiAssistantRequestedEventArgs(string parentMessageId, string partId, NativeUiAssistantRequest request) : EventArgs
{
    internal string ParentMessageId { get; } = parentMessageId;
    internal string PartId { get; } = partId;
    internal NativeUiAssistantRequest Request { get; } = request;
    internal bool Accepted { get; set; }
    internal string Notice { get; set; } = "当前卡片没有可用的输入区";
}
