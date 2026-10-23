using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

internal static partial class OpenCodeToolDetailBuiltins
{
    private sealed record ChildOutputBinding(OpenCodeClient Client, Func<string, bool> CanRead, CancellationToken Token);
    private static readonly ConditionalWeakTable<OpenCodeToolDetailOptions, ChildOutputBinding> ChildOutputs = new();

    /// <summary>Row host supplies its live client and current/authorized child-session guard. No global client or navigation state.</summary>
    internal static void BindLiveChildOutput(OpenCodeToolDetailOptions options, OpenCodeClient client, Func<string, bool> canRead, CancellationToken token)
    {
        ChildOutputs.Remove(options);
        ChildOutputs.Add(options, new(client, canRead, token));
    }

    private static FrameworkElement? CreateLiveChildOutput(OpenCodeToolDetailOptions options, string session)
    {
        if (!ChildOutputs.TryGetValue(options, out var binding)) return null;
        var text = OpenCodeToolDetailContent.Caption("正在读取子会话最新回复…");
        text.MaxLines = 6;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        CancellationTokenSource? lifetime = null;
        bool busy = false;
        bool loaded = false;
        async Task Refresh()
        {
            if (busy || !loaded) return;
            if (lifetime is not { IsCancellationRequested: false } active) { timer.Stop(); return; }
            var token = active.Token;
            if (!binding.CanRead(session)) { timer.Stop(); text.Text = "当前无法读取该子会话"; return; }
            busy = true;
            try
            {
                var messages = await binding.Client.GetMessagesAsync(session, new OpenCodeMessageQuery { Type = OpenCodeMessageTypes.Assistant, Order = OpenCodeSessionOrders.Desc, Limit = 20 }, token);
                if (!loaded || token.IsCancellationRequested || !binding.CanRead(session)) return;
                var output = OpenCodeToolDetailBuiltinData.LatestChildText(messages.Data);
                text.Text = output is null ? "子会话暂无文本回复" : string.Join('\n', output.Replace("\r", "", StringComparison.Ordinal).Split('\n').TakeLast(6));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { if (loaded && !token.IsCancellationRequested) text.Text = "无法读取子会话最新回复"; }
            finally { busy = false; }
        }
        timer.Tick += async (_, _) => await Refresh();
        text.Loaded += async (_, _) =>
        {
            loaded = true;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(binding.Token);
            timer.Start();
            await Refresh();
        };
        text.Unloaded += (_, _) => { loaded = false; timer.Stop(); lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
        return text;
    }
}
