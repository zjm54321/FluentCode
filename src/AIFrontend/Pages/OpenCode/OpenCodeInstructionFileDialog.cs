using System;
using System.Threading;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Explicit remote read only. Native selectable text; no local filesystem access or instruction-body projection.</summary>
internal sealed class OpenCodeInstructionFileDialog : IDisposable
{
    private CancellationTokenSource? _read;
    private ContentDialog? _dialog;

    internal async void Open(FrameworkElement owner, string path)
    {
        if (_dialog is not null || owner.XamlRoot is null) return;
        using var read = _read = new CancellationTokenSource();
        var text = new TextBox
        {
            Text = "正在读取…", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinWidth = 420, Height = 440,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(text, "OpenCodeInstructionFile.Content");
        var dialog = _dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot, Title = OpenCodeStepRunPresentation.ShortSourceName(path),
            Content = text, CloseButtonText = "关闭", DefaultButton = ContentDialogButton.Close,
        };
        var scope = OpenCodeImageContext.Find(owner);
        void Unloaded(object sender, RoutedEventArgs args)
        {
            if (owner.IsLoaded) return; // Ignore the previous message container's late unload.
            read.Cancel();
            dialog.Hide();
        }
        owner.Unloaded += Unloaded;
        async void Load()
        {
            try
            {
                if (scope is not { IsCurrent: true }) throw new InvalidOperationException("无法读取：会话连接已失效。");
                var directory = await OpenCodeTurnChipSession.For(scope).Directory(read.Token);
                var content = await scope.Identity.Client.ReadTextFileAsync(directory, path, read.Token);
                if (!read.IsCancellationRequested && scope.IsCurrent && ReferenceEquals(scope, OpenCodeImageContext.Find(owner)))
                    text.Text = content;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!read.IsCancellationRequested)
                    text.Text = error is OpenCodeException or ArgumentException or InvalidOperationException
                        ? "无法读取\n" + error.Message : "无法读取：远程文件读取失败。";
            }
        }
        try
        {
            var showing = dialog.ShowAsync();
            Load();
            await showing;
        }
        catch (Exception) { /* Another native modal may already own this XamlRoot. */ }
        finally
        {
            read.Cancel();
            owner.Unloaded -= Unloaded;
            _dialog = null;
            if (ReferenceEquals(_read, read)) _read = null;
        }
    }

    public void Dispose() { _read?.Cancel(); _dialog?.Hide(); }
}
