using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIFrontend.Pages.Web;

/// <summary>
/// 三个官网标签的官方品牌图标接入点：资源存在时把占位图标替换为官方品牌
/// <see cref="ImageIcon"/>（16 DIP）；缺失或加载失败时静默保留通用 Segoe 占位字形。
/// </summary>
/// <remarks>
/// 资源与出处（详见 <c>Assets/Brands/ATTRIBUTION.md</c> 与 <c>docs/web-ui-contract.md</c> §5.3）：
/// <list type="bullet">
/// <item>ChatGPT → <c>Assets/Brands/chatgpt.svg</c>（浅色）/ <c>chatgpt.dark.svg</c>（深色）：
/// 几何逐字取自官方 <c>https://chatgpt.com/cdn/assets/favicon-l4nq08hd.svg</c>（原文另存
/// <c>chatgpt.official.svg</c> 供溯源）；</item>
/// <item>Gemini → <c>Assets/Brands/gemini.png</c>（官方 PNG，原样未改）；</item>
/// <item>Claude → <c>Assets/Brands/claude.svg</c>（claude.ai 官方产品图标，原样未改）。</item>
/// </list>
/// <para>
/// <strong>格式支持</strong>：按扩展名选择加载器——<c>.svg</c> → <see cref="SvgImageSource"/>；
/// <c>.png</c> → <see cref="BitmapImage"/>。均经 <c>StorageFile.OpenReadAsync</c> 的流加载，
/// 避开非打包（<c>WindowsPackageType=None</c>）运行下 <c>ms-appx</c> URI 方案的不确定性。
/// </para>
/// <para>
/// <strong>ChatGPT 配色的处理（关键，不假称浏览器 CSS 有效）</strong>：官方 favicon SVG 的填充色
/// <strong>仅</strong>由内嵌 CSS <c>&lt;style&gt;</c>（含 <c>@media (prefers-color-scheme: dark)</c>）
/// 定义，根元素为 <c>fill="none"</c>。而 <see cref="SvgImageSource"/> 底层为 Direct2D SVG 渲染
/// （Learn「SVG Support」：支持元素表<b>不含</b> <c>&lt;style&gt;</c> 元素，仅支持 <c>fill</c> 等表现属性），
/// 故<strong>不保证</strong>执行该 CSS 或媒体查询——若原样加载，path 将继承 <c>fill="none"</c> 而可能
/// <strong>渲染为空白</strong>。因此本接入点<strong>不依赖 CSS</strong>：改用逐字保留官方几何、
/// 仅把填充由 CSS 改为显式 <c>fill</c> 表现属性的自包含浅/深两版（几何未改绘、未自绘、未用第三方素材），
/// 并按控件 <see cref="FrameworkElement.ActualTheme"/> 选择变体。此为配色适配（同 <c>docs/back-icon-source.md</c>
/// 的既定先例），<strong>不构成</strong>“CSS 媒体查询在 SvgImageSource 中有效”的声明。
/// </para>
/// 失败语义：文件缺失 / 加载失败 / 任何异常 → 返回 <c>false</c>，调用方保留通用 Segoe 占位字形。
/// 必须在 UI 线程调用（<see cref="ImageIcon"/> 与图像源均具线程亲和性）。
/// </remarks>
public static class ProviderBrandIcons
{
    /// <summary>品牌图标逻辑尺寸（DIP），与 SelectorBarItem 图标位的 16 DIP 通用字形一致。</summary>
    public const double IconSize = 16;

    /// <summary>品牌资源相对目录（位于应用基目录下）。</summary>
    public const string RelativeFolder = "Assets/Brands";

    /// <summary>provider 对应的官方品牌资源约定文件名（默认/浅色变体）。</summary>
    public static string GetFileName(WebProvider provider) => provider switch
    {
        WebProvider.ChatGPT => "chatgpt.svg",
        WebProvider.Gemini => "gemini.png",
        WebProvider.Claude => "claude.svg",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 按主题选择资源文件名：仅 ChatGPT 需要浅/深色变体（其官方 SVG 的配色原本由内嵌 CSS
    /// 媒体查询决定，而 SvgImageSource 不保证执行 CSS）；其余 provider 的资源与主题无关。
    /// </summary>
    public static string GetFileName(WebProvider provider, ElementTheme theme) => provider switch
    {
        WebProvider.ChatGPT when theme == ElementTheme.Dark => "chatgpt.dark.svg",
        _ => GetFileName(provider),
    };

    /// <summary>品牌资源的绝对路径（应用基目录 + 约定相对路径；默认/浅色变体）。</summary>
    public static string GetAbsolutePath(WebProvider provider) =>
        GetAbsolutePath(provider, ElementTheme.Light);

    /// <summary>品牌资源的绝对路径（按主题变体）。</summary>
    public static string GetAbsolutePath(WebProvider provider, ElementTheme theme) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Brands", GetFileName(provider, theme));

    /// <summary>
    /// 资源存在时把 <paramref name="item"/> 的图标替换（或按当前主题重设为）品牌 <see cref="ImageIcon"/>
    /// 并返回 <c>true</c>；否则保持调用方现有占位图标并返回 <c>false</c>。
    /// <strong>可重复调用</strong>：主题变化后再次调用会按新主题更换变体（不因已是 <see cref="ImageIcon"/> 而跳过）。
    /// </summary>
    public static async Task<bool> TryApplyAsync(SelectorBarItem item, WebProvider provider)
    {
        ArgumentNullException.ThrowIfNull(item);

        // 按控件当前主题选择变体（ChatGPT 浅/深各地一并显式 fill；其余 provider 与主题无关）。
        string path = GetAbsolutePath(provider, item.ActualTheme);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            // 非打包应用下不依赖 ms-appx 方案：直接按绝对路径读取文件流交给图像源。
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenReadAsync();

            ImageSource? source = await CreateImageSourceAsync(path, stream);
            if (source is null)
            {
                return false;
            }

            item.Icon = new ImageIcon
            {
                Source = source,
                Width = IconSize,
                Height = IconSize,
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 按扩展名创建图像源：<c>.svg</c> → <see cref="SvgImageSource"/>；光栅（<c>.png</c> 等）→
    /// <see cref="BitmapImage"/>。未支持的扩展名返回 <c>null</c>（调用方保留占位）。
    /// </summary>
    private static async Task<ImageSource?> CreateImageSourceAsync(string path, IRandomAccessStream stream)
    {
        string extension = Path.GetExtension(path);

        if (string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase))
        {
            var svg = new SvgImageSource();
            await svg.SetSourceAsync(stream);
            return svg;
        }

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
        {
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }

        return null;
    }
}
