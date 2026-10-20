// Test host for OpenCodePage.ViewAcknowledgement.cs: only the WinUI/XAML page surface and the App glue
// are substituted with minimal fakes. The production view-ack partial (水印读取 / 去重键 / 512 上限 /
// fence 语义) and the real OpenCodePageSource / message DTOs / OpenCodeExecutionStateReader compile
// as-is; no view-ack behavior is re-implemented here.
using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }

    public sealed class XamlRoot { }

    public class Application
    {
        public static Application? Current { get; set; }
    }
}

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess { get; set; } = true;

        // 同步执行排队回调：本入口绝不排队（非 UI 直接拒绝），此处仅为满足页面面。
        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public class Page
    {
        public bool IsLoaded { get; set; }

        // Fully qualified: the property name shadows the enum type name inside this scope.
        public Microsoft.UI.Xaml.Visibility Visibility { get; set; } = Microsoft.UI.Xaml.Visibility.Visible;

        public Microsoft.UI.Xaml.XamlRoot? XamlRoot { get; set; }

        public double ActualWidth { get; set; }

        public double ActualHeight { get; set; }

        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new();
    }
}

namespace AIFrontend
{
    /// <summary>
    /// 仅替代 OpenCodePage.ViewAcknowledgement.cs 触及的 App 面：view ACK 入口。记录每次调用；
    /// <see cref="ResultUnknown"/> 模拟真实“结果未知”返回（服务端可能已接受，调用方绝不自动重试）。
    /// </summary>
    public class App : Microsoft.UI.Xaml.Application
    {
        /// <summary>true：每次 ACK 返回 null（结果未知）；false：可见且未被 fence 拦截时返回已确认水位。</summary>
        public bool ResultUnknown { get; set; }

        /// <summary>App 入口被调用的总次数（含结果未知 / 被 fence 拦截的调用）。</summary>
        public int Calls { get; private set; }

        /// <summary>返回非 null 回执的次数。</summary>
        public int Posts { get; private set; }

        /// <summary>因 isStillVisible 返回 false 而被拒绝的调用次数。</summary>
        public int FenceBlocked { get; private set; }

        public double? LastSent { get; private set; }

        public Func<bool>? LastFence { get; private set; }

        public Task<double?> AcknowledgeOpenCodeSessionAsync(
            OpenCodePageSource source, long revision, string sessionId, double coveredIdle,
            Func<bool>? isStillVisible = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastFence = isStillVisible;
            if (!double.IsFinite(coveredIdle))
            {
                return Task.FromResult<double?>(null);
            }

            if (isStillVisible is not null && !isStillVisible())
            {
                FenceBlocked++;
                return Task.FromResult<double?>(null);
            }

            if (ResultUnknown)
            {
                return Task.FromResult<double?>(null);
            }

            Posts++;
            LastSent = coveredIdle;
            return Task.FromResult<double?>(coveredIdle);
        }
    }
}

namespace AIFrontend.Pages
{
    /// <summary>替代 OpenCodePage.xaml.cs（未参与本测试编译）的最小页面面 + 测试钩子。</summary>
    public sealed partial class OpenCodePage : Microsoft.UI.Xaml.Controls.Page
    {
        private OpenCodePageSource? _source;
        private long _sourceRevision;
        private int _sourceGeneration;
        private int _sessionGeneration;
        private string? _selectedSessionId;
        private bool _pageActive;
        private CancellationTokenSource? _pageCts;
        private CancellationTokenSource? _sessionCts;

        /// <summary>与 xaml.cs 的同名实现逐字一致：链接来源撤销 + 页面 + 会话取消（页面管道，非 ACK 算法）。</summary>
        private CancellationTokenSource CreateOperationToken(OpenCodePageSource source)
            => CancellationTokenSource.CreateLinkedTokenSource(
                source.RevocationToken,
                _pageCts?.Token ?? CancellationToken.None,
                _sessionCts?.Token ?? CancellationToken.None);

        // ---- 测试钩子（仅本测试编译可见） ----

        internal void TestActivate(OpenCodePageSource source, string sessionId)
        {
            _source = source;
            _sourceRevision = source.Revision;
            _sourceGeneration++;
            _sessionGeneration++;
            _selectedSessionId = sessionId;
            _pageActive = true;
            _pageCts = new CancellationTokenSource();
            _sessionCts = new CancellationTokenSource();
            IsLoaded = true;
            Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            XamlRoot = new Microsoft.UI.Xaml.XamlRoot();
            ActualWidth = 800;
            ActualHeight = 600;
        }

        internal void TestSetPageActive(bool active) => _pageActive = active;

        internal void TestSetPageVisible()
        {
            Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            ActualWidth = 800;
            ActualHeight = 600;
        }

        internal void TestSetPageInvisible()
        {
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            ActualWidth = 0;
            ActualHeight = 0;
        }

        internal void TestSetThreadAccess(bool access) => DispatcherQueue.HasThreadAccess = access;

        internal void TestSessionGenerationBump(string sessionId)
        {
            _sessionGeneration++;
            _selectedSessionId = sessionId;
        }

        /// <summary>临时清空来源（模拟来源暂时为 null）；尝试键刻意不清除（由生产部分持有）。</summary>
        internal void TestClearSource()
        {
            _source = null;
            _sourceRevision = 0;
        }

        internal void TestRevokeSource() => _source!.Revoke();
    }
}
