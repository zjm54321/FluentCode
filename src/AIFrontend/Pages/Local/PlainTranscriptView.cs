using System;
using System.Collections.Generic;
using AIFrontend.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Local;

/// <summary>
/// 纯文本降级转录视图：在 WebView2 运行时缺失/初始化失败时使用。
/// </summary>
/// <remarks>
/// 只做<strong>纯文本</strong>呈现（不解析 Markdown/HTML），用于在无法安全渲染时仍然
/// 展示对话内容；上层必须同时给出明确错误提示，避免把降级当作正常渲染。
/// </remarks>
internal sealed class PlainTranscriptView : UserControl
{
    private readonly StackPanel _panel = new() { Spacing = 14, Padding = new Thickness(18, 18, 20, 24) };
    private readonly ScrollViewer _scroll;

    public PlainTranscriptView()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _panel,
        };

        Content = _scroll;
    }

    /// <summary>用给定消息重建纯文本视图（UI 线程调用）。</summary>
    public void SetMessages(IReadOnlyList<DisplayMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _panel.Children.Clear();

        foreach (DisplayMessage message in messages)
        {
            _panel.Children.Add(BuildMessage(message));
        }
    }

    private static UIElement BuildMessage(DisplayMessage message)
    {
        var block = new StackPanel { Spacing = 4 };

        block.Children.Add(new TextBlock
        {
            Text = message.RoleLabel,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.7,
        });

        if (!string.IsNullOrEmpty(message.ReasoningText))
        {
            block.Children.Add(new TextBlock
            {
                Text = "思考：" + message.ReasoningText,
                FontSize = 13,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!string.IsNullOrEmpty(message.Content))
        {
            block.Children.Add(new TextBlock
            {
                Text = message.Content,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }

        if (!string.IsNullOrEmpty(message.StatusText))
        {
            block.Children.Add(new TextBlock
            {
                Text = message.StatusText,
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return block;
    }
}
