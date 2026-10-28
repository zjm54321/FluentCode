using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AIFrontend.Pages.OpenCode;

/// <summary>A native, selectable read-only terminal for a captured shell job (not a PTY input surface).</summary>
internal sealed class OpenCodeTerminalView : UserControl
{
    private readonly Button _back;
    private readonly TextBlock _command;
    private readonly TextBlock _status;
    private readonly TextBlock _metadata;
    private readonly TextBlock _notice;
    private readonly ContentControl _body;
    private readonly ScrollViewer _scroll;
    private readonly Button _copy;
    private string _text = string.Empty;
    private string? _renderedCommand;
    private bool? _renderedRunning;
    private bool _follow = true;
    private bool _scrollQueued;
    internal event Action? CloseRequested;

    internal OpenCodeTerminalView()
    {
        Content = (FrameworkElement)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" RowDefinitions="Auto,*"
                  Background="{ThemeResource SolidBackgroundFillColorBaseBrush}">
              <Grid ColumnDefinitions="Auto,*,Auto,Auto" ColumnSpacing="12" Padding="12,8"
                    Background="{ThemeResource SolidBackgroundFillColorBaseBrush}">
                <Button x:Name="Back" Width="36" Height="36" Padding="0" MinWidth="0"
                        AutomationProperties.AutomationId="OpenCodeTerminalBack" AutomationProperties.Name="返回会话"
                        ToolTipService.ToolTip="返回会话（Esc）"><FontIcon Glyph="&#xE72B;" FontSize="16" /></Button>
                <TextBlock x:Name="Command" Grid.Column="1" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"
                           FontFamily="Cascadia Mono, Consolas" />
                <TextBlock x:Name="Status" Grid.Column="2" VerticalAlignment="Center"
                           Style="{ThemeResource CaptionTextBlockStyle}" />
                <Button x:Name="Copy" Grid.Column="3" Content="复制输出" VerticalAlignment="Center" />
              </Grid>
              <ScrollViewer x:Name="Scroll" Grid.Row="1" Padding="16" HorizontalScrollBarVisibility="Auto"
                            HorizontalScrollMode="Enabled" VerticalScrollBarVisibility="Auto"
                            AutomationProperties.AutomationId="OpenCodeTerminalScroll">
                <StackPanel Spacing="12">
                  <TextBlock x:Name="Metadata" IsTextSelectionEnabled="True" FontFamily="Cascadia Mono, Consolas"
                              TextWrapping="Wrap" />
                  <ContentControl x:Name="TerminalBody" HorizontalContentAlignment="Stretch"
                                  AutomationProperties.AutomationId="OpenCodeTerminalOutput" />
                  <TextBlock x:Name="Notice" IsTextSelectionEnabled="True" TextWrapping="Wrap"
                              />
                </StackPanel>
              </ScrollViewer>
            </Grid>
            """);
        var root = (FrameworkElement)Content;
        _back = (Button)root.FindName("Back");
        _command = (TextBlock)root.FindName("Command");
        _status = (TextBlock)root.FindName("Status");
        _metadata = (TextBlock)root.FindName("Metadata");
        _notice = (TextBlock)root.FindName("Notice");
        _body = (ContentControl)root.FindName("TerminalBody");
        _scroll = (ScrollViewer)root.FindName("Scroll");
        _copy = (Button)root.FindName("Copy");
        _back.Click += (_, _) => CloseRequested?.Invoke();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            CloseRequested?.Invoke();
        };
        _copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(_text);
            Clipboard.SetContent(data);
        };
        // ViewChanging exposes the user's next offset, before growing content changes the extent.
        _scroll.ViewChanging += (_, e) => _follow = e.FinalView.VerticalOffset >= _scroll.ScrollableHeight - 2;
        _scroll.SizeChanged += (_, _) => QueueFollow();
        _body.SizeChanged += (_, _) => QueueFollow();
        AutomationProperties.SetName(this, "终端输出（只读）");
    }

    internal void Begin()
    {
        _scroll.ChangeView(0, 0, null, true);
        _follow = true;
        QueueFollow();
        _back.Focus(FocusState.Programmatic);
    }

    internal void Render(OpenCodeTerminalState state)
    {
        _command.Text = state.Command;
        ToolTipService.SetToolTip(_command, state.Command);
        _status.Text = state.StatusText;
        _metadata.Text = (state.Cwd is { Length: > 0 } cwd ? "目录：" + cwd : "目录不可用")
            + (state.TimeText.Length > 0 ? "\n" + state.TimeText : string.Empty);
        _notice.Text = state.Notice ?? string.Empty;
        _notice.Visibility = string.IsNullOrEmpty(state.Notice) ? Visibility.Collapsed : Visibility.Visible;
        _copy.IsEnabled = state.Output.Length > 0;
        // A status-only poll must not rebuild selected output. Running state only
        // changes the body when it owns the empty-output placeholder.
        if (_text == state.Output && _renderedCommand == state.Command
            && (_text.Length > 0 || _renderedRunning == state.Running)) return;
        _follow = OpenCodeTerminalState.ShouldFollow(_follow, _scroll.VerticalOffset, _scroll.ScrollableHeight);
        _text = state.Output;
        _renderedCommand = state.Command;
        _renderedRunning = state.Running;
        _body.Content = OpenCodeToolDetailContent.TerminalBody(state.Command,
            _text.Length > 0 ? _text : state.Running ? "命令运行中，暂无输出…" : "（没有输出）");
        QueueFollow();
    }

    private void QueueFollow()
    {
        if (!_follow || _scrollQueued) return;
        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _scrollQueued = false;
            if (_follow) _scroll.ChangeView(null, _scroll.ScrollableHeight, null, true);
        });
    }
}
