using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Web;

/// <summary>
/// 官网 WebView2 环境 cookie 的<strong>逐 provider</strong>清理入口（Web 范围，供主窗口调用）。
/// </summary>
/// <remarks>
/// 目的与边界：
/// <list type="bullet">
/// <item>设置界面以应用内页面（Frame 内页）承载，显示期间 <c>NormalPage</c> 不在内容树中
/// （本轮起该页经 Frame 缓存保活、宿主不再随 Unloaded 关闭，见 <c>docs/web-ui-contract.md</c> §11）；
/// 本类<strong>不依赖</strong> <c>NormalPage</c> 的加载态，可在此场景下安全清理。</item>
/// <item>Cookie 清理只能由<strong>存活</strong>的 <c>CoreWebView2</c> 执行
/// （<c>CoreWebView2CookieManager.DeleteAllCookies</c>）：不存在“离线清 cookie”的通道。
/// 因此本类采用<strong>受控临时宿主</strong>方案——在调用方提供的已加载容器内临时挂载一个
/// 1×1 的 <see cref="WebViewHost"/>，等待其环境/实例就绪并执行清理后，<strong>立即</strong>
/// <see cref="WebViewHost.Close"/> 并移除。绝不把“已排队”当作“已清除”：本方法只在
/// <c>DeleteAllCookies</c> 实际返回后完成。</item>
/// <item>只清 cookie：<strong>不</strong>删除用户数据目录、<strong>不</strong>清除缓存/本地存储/站点权限、
/// <strong>不</strong>删除密码或网站配置；各 provider 使用各自独立固定的环境目录，互不影响，profile 隔离保留。</item>
/// <item><strong>不调用已 dispose/Close 的实例</strong>：每次调用新建临时宿主，无论成功失败都在
/// <c>finally</c> 关闭并移除，不复用已关闭实例。</item>
/// <item>必须在 <strong>UI 线程</strong>调用；<paramref name="hostLayer"/> 必须是<strong>已挂载且已加载</strong>
/// 的可视树容器（<c>XamlRoot</c> 非 null），否则临时宿主无法初始化。违反时抛
/// <see cref="InvalidOperationException"/>（不静默失败，也不假称已清除）。</item>
/// <item><paramref name="cancellationToken"/> 仅能在<strong>开始清理前</strong>取消；一旦临时宿主初始化并进入
/// <c>DeleteAllCookies</c>，该同步调用一次性完成，不中途取消（不谎报“已取消/未清除”）。</item>
/// <item>WebView2 初始化失败时由 <see cref="WebViewHost.ClearCookiesAsync"/> 抛
/// <see cref="InvalidOperationException"/>，原样上抛由调用方如实呈现。</item>
/// </list>
/// </remarks>
public static class WebCookieCleaner
{
    /// <summary>
    /// 清理指定 provider 环境的全部 cookie（只清 cookie）。
    /// </summary>
    /// <param name="hostLayer">
    /// 调用方提供的<strong>已加载</strong>可视树容器（如主窗口的隐藏宿主层 <c>Grid</c>）；
    /// 临时宿主将临时挂入其中，清理后立即移除。必须非 null 且已随窗口加载（<c>XamlRoot</c> 非 null）。
    /// </param>
    /// <param name="provider">目标官网 provider（决定使用哪个独立环境目录）。</param>
    /// <param name="cancellationToken">仅用于在开始清理前取消。</param>
    /// <exception cref="ArgumentNullException"><paramref name="hostLayer"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">非 UI 线程调用；容器未加载；或 WebView2 初始化失败。</exception>
    public static async Task ClearCookiesAsync(
        Panel hostLayer,
        WebProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostLayer);

        // UI 线程 + 已加载容器：WebView2（CoreWebView2）具有 UI 线程亲和性，且需要已加载的可视树才能初始化。
        if (!hostLayer.DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("ClearCookiesAsync 必须在 UI 线程调用（WebView2 线程亲和）。");
        }

        if (hostLayer.XamlRoot is null)
        {
            throw new InvalidOperationException(
                "hostLayer 未挂载到已加载的可视树，无法初始化用于清理 cookie 的临时 WebView2。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 受控临时宿主：1×1、可见（WebView2 需要已加载且未折叠才能初始化），清理后立即关闭移除。
        // 新建实例 → 从不复用已 Close 的实例；所有路径都在 finally 收尾。
        var host = new WebViewHost(provider)
        {
            Visibility = Visibility.Visible,
            Width = 1,
            Height = 1,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        hostLayer.Children.Add(host);
        try
        {
            await host.ClearCookiesAsync();
        }
        finally
        {
            host.Close();
            hostLayer.Children.Remove(host);
        }
    }
}
