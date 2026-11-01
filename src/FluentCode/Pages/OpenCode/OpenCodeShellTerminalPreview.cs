using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>Bounded XAML-only shell summary. The open callback retains the original, complete output.</summary>
internal sealed class OpenCodeShellTerminalPreview : StackPanel
{
    private readonly TextBlock _command = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis,
        FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12, Opacity = 0.75 };
    private readonly TextBlock _output = new() { MaxLines = OpenCodeShellTerminalText.PreviewLines,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12 };
    private readonly Button _open = new() { Content = "在终端中打开", Padding = new Thickness(8, 3, 8, 3), MinHeight = 24,
        HorizontalAlignment = HorizontalAlignment.Left };
    private Action? _requested;
    internal string PreviewText => _output.Text;

    internal OpenCodeShellTerminalPreview()
    {
        Spacing = 4;
        Children.Add(_command); Children.Add(_output); Children.Add(_open);
        AutomationProperties.SetAutomationId(this, "OpenCodeShellPreview");
        _open.Click += (_, _) => _requested?.Invoke();
    }

    internal void Update(string? command, string? output, Action? open)
    {
        _command.Text = "$ " + OpenCodeShellTerminalText.PlainText(command ?? string.Empty).ReplaceLineEndings(" ");
        ToolTipService.SetToolTip(_command, command);
        _output.Text = OpenCodeShellTerminalText.Preview(output);
        _output.Visibility = _output.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _requested = open; _open.Visibility = open is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
