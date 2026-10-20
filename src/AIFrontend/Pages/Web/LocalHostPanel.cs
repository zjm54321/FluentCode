using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Web;

/// <summary>
/// Local 标签页的占位面板：明确标注“待接入”，不虚构已可聊天。
/// </summary>
/// <remarks>
/// 后续将以独立的 Local UserControl（对接本地模型）替换本占位实现；
/// 本类当前只负责呈现清晰的“不可用”状态，不包含任何对话或模型逻辑。
/// </remarks>
public sealed class LocalHostPanel : UserControl
{
    public LocalHostPanel()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        var content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon
                {
                    Glyph = "\uE8BD",
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "本地模型尚未接入",
                    FontSize = 20,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "此处为占位面板：本地模型对话能力将在后续以独立控件接入，当前不可用，也不代表已有可用对话。",
                    FontSize = 14,
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
            },
        };

        Content = content;
    }
}
