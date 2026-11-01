using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.Configuration;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Services.Appearance;

/// <summary>
/// 应用级字体运行时提供者：把用户在设置中选择的正文 / 衬线 / 等宽字体应用到整个应用，并在变更时通知。
/// </summary>
/// <remarks>
/// <para><b>数据源</b>：唯一来源是共享 <see cref="SettingsService"/>（<c>settings.json</c> 的 <c>fonts</c> 节点）。
/// <see cref="InitializeAsync"/> 在主窗口创建前加载一次并订阅 <see cref="SettingsService.Changed"/>，
/// 之后任何成功保存都会被自动应用（设置页无需额外事件链）。</para>
/// <para><b>应用方式</b>：</para>
/// <list type="bullet">
/// <item><description>更新 <c>Application.Current.Resources</c> 中的字体键：
/// <see cref="ContentControlThemeFontFamilyKey"/>（WinUI 内置控件模板与本应用重定义的内置文字样式都以 ThemeResource 引用它）、
/// <see cref="BodyResourceKey"/> / <see cref="SerifResourceKey"/> / <see cref="MonospaceResourceKey"/>。
/// 应用级资源对弹出层（Flyout / MenuFlyout /
/// ContentDialog / TeachingTip / ToolTip）同样生效——它们不在主视觉树内，但资源查找最终都落到 Application 资源。</description></item>
/// <item><description>运行期变更时<strong>不</strong>做 RequestedTheme 往返（D1：同帧 opposite→original 主题翻转在
/// ComboBox 选择 / 弹出层关闭期间触发 CoreMessagingXP.dll 0xc000027b 原生崩溃）。改为在稍后的
/// DispatcherQueue 低优先级时机（合并为一次）写资源，并遍历 <see cref="RegisterRoot"/> 登记的已加载根元素与
/// 已打开弹出层，只把仍持有某槽位旧字体源的元素逐个改为新字体（判定见 <see cref="AppFontRefreshPlan"/>）；
/// 未加载的登记根（Frame 缓存页）在下次 Loaded 时同样延迟补刷。新打开的弹出层 / 新建元素直接取到新资源。</description></item>
/// <item><description>代码里构建的元素用 <see cref="Apply(FrameworkElement, AppFontSlot)"/>（加载期间随变更实时更新）
/// 或在构建时读取 <see cref="Monospace"/> 等属性（适合随内容重建的 Run / Span）。</description></item>
/// </list>
/// <para>线程：除 <see cref="InitializeAsync"/> 内部调度外，所有成员都应在 UI 线程调用；
/// <see cref="Changed"/> 只在 UI 线程触发。</para>
/// </remarks>
public static class AppFonts
{
    /// <summary>WinUI 内置控件使用的正文字体键（本应用在 App.xaml 中覆盖并在运行期替换）。</summary>
    public const string ContentControlThemeFontFamilyKey = "ContentControlThemeFontFamily";

    /// <summary>正文字体资源键（<c>FontFamily</c>）。</summary>
    public const string BodyResourceKey = "AppBodyFontFamily";

    /// <summary>衬线字体资源键（<c>FontFamily</c>）。</summary>
    public const string SerifResourceKey = "AppSerifFontFamily";

    /// <summary>等宽字体资源键（<c>FontFamily</c>）。</summary>
    public const string MonospaceResourceKey = "AppMonospaceFontFamily";

    private static readonly List<RootEntry> Roots = new();
    private static AppFontRefreshPlan? _plan;
    private static SettingsService? _settingsService;
    private static DispatcherQueue? _dispatcher;
    private static AppFontSettings? _current;
    private static string _bodySource = AppFontDefaults.BodyDefault;
    private static string _serifSource = AppFontDefaults.SerifDefault;
    private static string _monospaceSource = AppFontDefaults.MonospaceDefault;
    private static FontFamily? _body;
    private static FontFamily? _serif;
    private static FontFamily? _monospace;
    private static bool _applied;
    private static double _textSize = AppFontDefaults.DefaultTextSize;
    private static readonly AppTextSizeRefreshPlan TextMetrics = new();
    private static readonly AppTextSizeRefreshPlan LineMetrics = new(lineHeight: true);
    private static bool _layoutPending;

    public const string TextSizeResourceKey = "AppTextSize";
    public static double TextSize => _textSize;

    /// <summary>字体实际变化后在 UI 线程触发（资源已更新、根元素已刷新）。</summary>
    public static event EventHandler? Changed;

    /// <summary>当前生效的用户选择（null = 全部默认）。</summary>
    public static AppFontSettings? Current => _current;

    /// <summary>当前正文字体（含回退链）。</summary>
    public static FontFamily Body => _body ??= new FontFamily(_bodySource);

    /// <summary>当前衬线字体（含回退链）。</summary>
    public static FontFamily Serif => _serif ??= new FontFamily(_serifSource);

    /// <summary>当前等宽字体（含回退链）。</summary>
    public static FontFamily Monospace => _monospace ??= new FontFamily(_monospaceSource);

    /// <summary>当前槽位字体。</summary>
    public static FontFamily Get(AppFontSlot slot) => slot switch
    {
        AppFontSlot.Body => Body,
        AppFontSlot.Serif => Serif,
        AppFontSlot.Monospace => Monospace,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>当前槽位字体的源字符串（含回退链，例如 “Cascadia Mono, Consolas”）。</summary>
    public static string GetSource(AppFontSlot slot) => slot switch
    {
        AppFontSlot.Body => _bodySource,
        AppFontSlot.Serif => _serifSource,
        AppFontSlot.Monospace => _monospaceSource,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>
    /// 启动时调用一次（UI 线程、主窗口创建<strong>之前</strong>）：加载已保存字体并写入应用资源，
    /// 然后订阅设置变更。加载失败（文件损坏等）时使用默认字体，不抛出。重复调用为无操作。
    /// </summary>
    public static async Task InitializeAsync(SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        if (_settingsService is not null)
        {
            return;
        }

        _settingsService = settingsService;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        AppSettings? loaded = settingsService.Current;
        if (loaded is null)
        {
            try
            {
                loaded = await settingsService.LoadAsync();
            }
            catch (Exception)
            {
                // 损坏 / 读取失败：保持默认字体；设置页会以 InfoBar 呈现加载错误。
                loaded = null;
            }
        }

        settingsService.Changed += OnSettingsChanged;
        ApplySettings(loaded?.Fonts);
    }

    /// <summary>
    /// 登记需要在运行期字体变更时刷新的根元素（主窗口根元素、Frame 缓存页；其他独立窗口的根元素同理）。
    /// 弱引用持有；重复登记同一元素为无操作。根元素未加载时错过的刷新在其下次 Loaded 后延迟补做。
    /// </summary>
    public static void RegisterRoot(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Roots.RemoveAll(r => !r.Element.TryGetTarget(out _));
        foreach (RootEntry existing in Roots)
        {
            if (existing.Element.TryGetTarget(out FrameworkElement? target) && ReferenceEquals(target, root))
            {
                return;
            }
        }

        // Registration does not prove the consumers have the current font: a newly recreated
        // Frame page can reuse an older Style/ThemeResource FontFamily. Its first Loaded must
        // catch up too, even when it was registered after the setting change.
        var entry = new RootEntry(root, -1);
        Roots.Add(entry);
        root.Loaded += entry.OnLoaded;
        root.LayoutUpdated += entry.OnLayoutUpdated;
        if (SafeIsLoaded(root))
        {
            entry.OnLoaded(root, new RoutedEventArgs());
        }
    }

    /// <summary>
    /// 把槽位字体应用到代码构建的元素（TextBlock / RichTextBlock / Control / TextElement 宿主），
    /// 并在元素加载期间随字体变更实时更新（卸载时自动退订，不泄漏）。
    /// </summary>
    public static void Apply(FrameworkElement element, AppFontSlot slot)
    {
        ArgumentNullException.ThrowIfNull(element);
        SetFont(element, Get(slot));
        _ = new ElementTracker(element, slot);
    }

    /// <summary>
    /// 把槽位字体应用到 <see cref="TextElement"/>（Run / Span 等，没有 Loaded 事件）：只取当前值，
    /// 内容重建时自然更新；需要实时更新时请改用承载它的 RichTextBlock / TextBlock + <see cref="Apply(FrameworkElement, AppFontSlot)"/>。
    /// </summary>
    public static void Apply(TextElement element, AppFontSlot slot)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.FontFamily = Get(slot);
    }

    /// <summary>直接应用一组字体选择（UI 线程）。通常由设置变更自动调用；测试/预览也可直接调用。</summary>
    /// <remarks>
    /// 运行期变更只更新字体状态（<see cref="Body"/> 等立即返回新值），资源写入、已存在元素刷新与
    /// <see cref="Changed"/> 通知一律推迟到稍后的 DispatcherQueue 低优先级时机、合并为一次执行
    /// （见 <see cref="ScheduleRefresh"/>）。调用方往往正处在 ComboBox SelectionChanged / 弹出层关闭 /
    /// 布局过程中，绝不在当帧改动资源或主题。
    /// </remarks>
    public static void ApplySettings(AppFontSettings? settings)
    {
        AppFontSettings? normalized = AppFontDefaults.Normalize(settings);
        string body = AppFontDefaults.Resolve(normalized, AppFontSlot.Body);
        string serif = AppFontDefaults.Resolve(normalized, AppFontSlot.Serif);
        string monospace = AppFontDefaults.Resolve(normalized, AppFontSlot.Monospace);
        double textSize = AppFontDefaults.ResolveTextSize(normalized);

        bool initialized = _applied;
        _applied = true;
        _current = normalized;

        if (!initialized)
        {
            SetSources(body, serif, monospace);
            _plan = new AppFontRefreshPlan(body, serif, monospace);
            _textSize = textSize;
            _plan.Advance(body, serif, monospace, textSize);

            // 首次应用发生在任何界面创建之前：资源已就位，无需刷新也无需通知。
            WriteApplicationResources();
            return;
        }

        AppFontRefreshPlan plan = _plan ??= new AppFontRefreshPlan(_bodySource, _serifSource, _monospaceSource);
        if (!plan.Advance(body, serif, monospace, textSize))
        {
            return;
        }

        SetSources(body, serif, monospace);
        _textSize = textSize;
        ScheduleRefresh();
    }

    private static void SetSources(string body, string serif, string monospace)
    {
        _bodySource = body;
        _serifSource = serif;
        _monospaceSource = monospace;
        _body = new FontFamily(body);
        _serif = new FontFamily(serif);
        _monospace = new FontFamily(monospace);
    }

    private static void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        AppFontSettings? fonts = (_settingsService?.Current ?? e.Settings).Fonts;
        DispatcherQueue? dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            ApplySettings(fonts);
            return;
        }

        dispatcher.TryEnqueue(() => ApplySettings((_settingsService?.Current ?? e.Settings).Fonts));
    }

    private static void WriteApplicationResources()
    {
        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        resources[ContentControlThemeFontFamilyKey] = Body;
        resources[BodyResourceKey] = Body;
        resources[SerifResourceKey] = Serif;
        resources[MonospaceResourceKey] = Monospace;
        resources[TextSizeResourceKey] = _textSize;
    }

    /// <summary>
    /// 把“写资源 + 刷新已存在元素 + 通知”排到 DispatcherQueue 低优先级时机（当前输入 / 布局 / 弹出层关闭处理完之后），
    /// 同一轮内多次变更只执行一次且使用最新字体。不使用 RequestedTheme 往返（D1：同帧主题翻转导致
    /// CoreMessagingXP.dll 0xc000027b 原生崩溃）。
    /// </summary>
    private static void ScheduleRefresh()
    {
        AppFontRefreshPlan? plan = _plan;
        if (plan is null || !plan.TryBeginRefresh())
        {
            return;
        }

        DispatcherQueue? dispatcher = _dispatcher ?? DispatcherQueue.GetForCurrentThread();
        if (dispatcher is not null && dispatcher.TryEnqueue(DispatcherQueuePriority.Low, RunDeferredRefresh))
        {
            return;
        }

        // 没有调度器（无界面宿主）：没有可刷新的界面，只更新资源并通知。
        plan.EndRefresh();
        WriteApplicationResources();
        RaiseChanged();
    }

    private static void RunDeferredRefresh()
    {
        _plan?.EndRefresh();
        try
        {
            WriteApplicationResources();
        }
        catch (Exception)
        {
            // 资源字典不可写（应用正在关闭）：已存在元素仍按下方逐元素刷新。
        }

        RefreshRoots();
        RaiseChanged();
    }

    /// <summary>
    /// 刷新所有已加载的登记根元素及其 XamlRoot 上已打开的弹出层；未加载的根元素（如 Frame 缓存页）
    /// 在下次 Loaded 时补刷（见 <see cref="RootEntry"/>）。
    /// </summary>
    private static void RefreshRoots()
    {
        AppFontRefreshPlan? plan = _plan;
        if (plan is null)
        {
            return;
        }

        Roots.RemoveAll(r => !r.Element.TryGetTarget(out _));
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var xamlRoots = new List<XamlRoot>();
        foreach (RootEntry entry in Roots.ToArray())
        {
            if (!entry.Element.TryGetTarget(out FrameworkElement? root) || !SafeIsLoaded(root))
            {
                continue;
            }

            RefreshTree(root, plan, visited);
            entry.Generation = plan.Generation;
            if (SafeXamlRoot(root) is { } xamlRoot && !xamlRoots.Contains(xamlRoot))
            {
                xamlRoots.Add(xamlRoot);
            }
        }

        foreach (XamlRoot xamlRoot in xamlRoots)
        {
            IReadOnlyList<Microsoft.UI.Xaml.Controls.Primitives.Popup> popups;
            try
            {
                popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Microsoft.UI.Xaml.Controls.Primitives.Popup popup in popups)
            {
                RefreshTree(popup, plan, visited);
                if (popup.Child is { } child)
                {
                    RefreshTree(child, plan, visited);
                }
            }
        }
    }

    /// <summary>
    /// 深度优先遍历：先改父元素（模板绑定 / 属性继承随之同步生效），再看子元素，因此只有显式持有旧字体源的元素
    /// 才会被逐个赋值。字号先快照再按元素自身保存的基线刷新，不改主题、不替换样式或框架字号资源。
    /// </summary>
    private static void RefreshTree(DependencyObject start, AppFontRefreshPlan plan, HashSet<DependencyObject> visited)
    {
        var stack = new Stack<DependencyObject>();
        var sizeUpdates = new List<Action>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            if (!visited.Add(node))
            {
                continue;
            }

            try
            {
                CaptureTextSize(node, sizeUpdates);
                RefreshElement(node, plan);
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = count - 1; i >= 0; i--)
                {
                    stack.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
            catch (Exception)
            {
                // 单个元素失效（窗口关闭中等）：跳过，不影响其余元素。
            }
        }
        // Capture the complete tree before changing any inherited parent metric.
        foreach (Action update in sizeUpdates)
        {
            try { update(); } catch (Exception) { /* Closing consumer. */ }
        }
    }

    private static bool IsIconFont(FontFamily? family)
    {
        string source = family?.Source ?? string.Empty;
        return source.Contains("Icons", StringComparison.OrdinalIgnoreCase)
            || source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Wingdings", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Symbol", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGlyphOnly(string text)
    {
        bool glyph = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (category is not (UnicodeCategory.PrivateUse or UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol)
                && c is not ('✓' or '✔' or '✕' or '×' or '…' or '⋯')) return false;
            glyph = true;
        }
        return glyph;
    }

    private static void CaptureTextSize(DependencyObject node, List<Action> updates)
    {
        switch (node)
        {
            case TextBlock text when !IsIconFont(text.FontFamily) && !IsGlyphOnly(text.Text):
                CaptureMetric(text, TextBlock.FontSizeProperty, text.FontSize, v => text.FontSize = v, updates);
                CaptureLine(text, text.LineHeight, v => text.LineHeight = v, updates);
                CaptureInlines(text.Inlines, updates);
                break;
            case RichTextBlock rich when !IsIconFont(rich.FontFamily):
                CaptureMetric(rich, RichTextBlock.FontSizeProperty, rich.FontSize, v => rich.FontSize = v, updates);
                CaptureLine(rich, rich.LineHeight, v => rich.LineHeight = v, updates);
                foreach (Block block in rich.Blocks)
                {
                    CaptureTextElement(block, updates);
                    CaptureLine(block, block.LineHeight, v => block.LineHeight = v, updates);
                    if (block is Paragraph paragraph) CaptureInlines(paragraph.Inlines, updates);
                }
                break;
            case Control control when !IsIconFont(control.FontFamily):
                CaptureMetric(control, Control.FontSizeProperty, control.FontSize, v => control.FontSize = v, updates);
                break;
            case ContentPresenter presenter when !IsIconFont(presenter.FontFamily):
                CaptureMetric(presenter, ContentPresenter.FontSizeProperty, presenter.FontSize, v => presenter.FontSize = v, updates);
                break;
        }
    }

    private static void CaptureMetric(DependencyObject node, DependencyProperty property, double current,
        Action<double> set, List<Action> updates)
    {
        // Inherited text follows its parent; local/style sizes keep their own hierarchy.
        if (node.ReadLocalValue(property) == DependencyProperty.UnsetValue && !HasStyleSize(node))
        {
            DependencyObject? parent = VisualTreeHelper.GetParent(node);
            while (parent is not null)
            {
                double? parentSize = parent switch
                {
                    TextBlock t => t.FontSize,
                    RichTextBlock r => r.FontSize,
                    Control c => c.FontSize,
                    ContentPresenter p => p.FontSize,
                    _ => null,
                };
                if (parentSize is double size)
                {
                    if (Math.Abs(size - current) < 0.001) return;
                    break;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
        }
        double next = TextMetrics.Resolve(node, current, _textSize / AppFontDefaults.DefaultTextSize);
        if (Math.Abs(next - current) > 0.001) updates.Add(() => set(next));
    }

    private static bool HasStyleSize(DependencyObject node)
    {
        for (Style? style = (node as FrameworkElement)?.Style; style is not null; style = style.BasedOn)
            foreach (SetterBase item in style.Setters)
                if (item is Setter setter && (setter.Property == TextBlock.FontSizeProperty
                    || setter.Property == RichTextBlock.FontSizeProperty || setter.Property == Control.FontSizeProperty
                    || setter.Property == ContentPresenter.FontSizeProperty)) return true;
        return false;
    }

    private static void CaptureLine(DependencyObject node, double current, Action<double> set, List<Action> updates)
    {
        if (current <= 0) return;
        double next = LineMetrics.Resolve(node, current, _textSize / AppFontDefaults.DefaultTextSize);
        if (Math.Abs(next - current) > 0.001) updates.Add(() => set(next));
    }

    private static void CaptureTextElement(TextElement text, List<Action> updates)
    {
        if (IsIconFont(text.FontFamily) || text.ReadLocalValue(TextElement.FontSizeProperty) == DependencyProperty.UnsetValue) return;
        double next = TextMetrics.Resolve(text, text.FontSize, _textSize / AppFontDefaults.DefaultTextSize);
        if (Math.Abs(next - text.FontSize) > 0.001) updates.Add(() => text.FontSize = next);
    }

    private static void CaptureInlines(InlineCollection inlines, List<Action> updates)
    {
        foreach (Inline inline in inlines)
        {
            CaptureTextElement(inline, updates);
            if (inline is Span span) CaptureInlines(span.Inlines, updates);
        }
    }

    private static void RefreshElement(DependencyObject node, AppFontRefreshPlan plan)
    {
        switch (node)
        {
            case TextBlock textBlock:
                if (Remap(textBlock.FontFamily, plan) is { } textFont)
                {
                    textBlock.FontFamily = textFont;
                }

                RefreshInlines(textBlock.Inlines, plan);
                break;
            case RichTextBlock richTextBlock:
                if (Remap(richTextBlock.FontFamily, plan) is { } richFont)
                {
                    richTextBlock.FontFamily = richFont;
                }

                foreach (Block block in richTextBlock.Blocks)
                {
                    if (Remap(block.FontFamily, plan) is { } blockFont)
                    {
                        block.FontFamily = blockFont;
                    }

                    if (block is Paragraph paragraph)
                    {
                        RefreshInlines(paragraph.Inlines, plan);
                    }
                }

                break;
            case Control control:
                if (Remap(control.FontFamily, plan) is { } controlFont)
                {
                    control.FontFamily = controlFont;
                }

                break;
            case ContentPresenter presenter:
                if (Remap(presenter.FontFamily, plan) is { } presenterFont)
                {
                    presenter.FontFamily = presenterFont;
                }

                break;
        }
    }

    private static void RefreshInlines(InlineCollection inlines, AppFontRefreshPlan plan)
    {
        foreach (Inline inline in inlines)
        {
            if (Remap(inline.FontFamily, plan) is { } font)
            {
                inline.FontFamily = font;
            }

            if (inline is Span span)
            {
                RefreshInlines(span.Inlines, plan);
            }
        }
    }

    /// <summary>旧槽位字体源 → 当前同槽位 FontFamily；不相关字体（图标字体、显式指定字体、已是新值）返回 null。</summary>
    private static FontFamily? Remap(FontFamily? current, AppFontRefreshPlan plan)
        => plan.Resolve(current?.Source) is { } slot ? Get(slot) : null;

    private static bool SafeIsLoaded(FrameworkElement element)
    {
        try
        {
            return element.IsLoaded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static XamlRoot? SafeXamlRoot(FrameworkElement element)
    {
        try
        {
            return element.XamlRoot;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void RaiseChanged()
    {
        EventHandler? handler = Changed;
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(null, EventArgs.Empty);
            }
            catch
            {
                // 订阅者异常不影响其他订阅者与字体应用。
            }
        }
    }

    private static void SetFont(FrameworkElement element, FontFamily font)
    {
        switch (element)
        {
            case TextBlock textBlock:
                textBlock.FontFamily = font;
                break;
            case RichTextBlock richTextBlock:
                richTextBlock.FontFamily = font;
                break;
            case Control control:
                control.FontFamily = font;
                break;
            case ContentPresenter presenter:
                presenter.FontFamily = font;
                break;
            case RichTextBlockOverflow:
                // 字体随源 RichTextBlock。
                break;
            default:
                throw new ArgumentException($"{element.GetType().Name} 没有 FontFamily 属性。", nameof(element));
        }
    }

    /// <summary>已登记根元素：记录它最后一次刷新到的字体代数；重新加载时若已落后，延迟补刷（低优先级，绝不同帧）。</summary>
    private sealed class RootEntry
    {
        public RootEntry(FrameworkElement element, int generation)
        {
            Element = new WeakReference<FrameworkElement>(element);
            Generation = generation;
        }

        public WeakReference<FrameworkElement> Element { get; }

        public int Generation { get; set; }

        public void OnLayoutUpdated(object? sender, object e)
        {
            // Includes newly rendered text and open popups without another setting change.
            if (_layoutPending || _textSize == AppFontDefaults.DefaultTextSize ||
                !Element.TryGetTarget(out FrameworkElement? element) || !SafeIsLoaded(element)) return;
            _layoutPending = true;
            DispatcherQueue? dispatcher = _dispatcher ?? DispatcherQueue.GetForCurrentThread();
            if (dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _layoutPending = false;
                RefreshRoots();
            }) != true) _layoutPending = false;
        }

        public void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppFontRefreshPlan? plan = _plan;
            if (plan is null || Generation >= plan.Generation)
            {
                return;
            }

            DispatcherQueue? dispatcher = _dispatcher ?? DispatcherQueue.GetForCurrentThread();
            dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                AppFontRefreshPlan? latest = _plan;
                if (latest is null || Generation >= latest.Generation ||
                    !Element.TryGetTarget(out FrameworkElement? root) || !SafeIsLoaded(root))
                {
                    return;
                }

                RefreshTree(root, latest, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
                Generation = latest.Generation;
            });
        }
    }

    /// <summary>元素加载期间订阅 <see cref="Changed"/>，卸载时退订；元素本身持有本对象（经事件），无静态强引用。</summary>
    private sealed class ElementTracker
    {
        private readonly WeakReference<FrameworkElement> _element;
        private readonly AppFontSlot _slot;
        private bool _subscribed;

        public ElementTracker(FrameworkElement element, AppFontSlot slot)
        {
            _element = new WeakReference<FrameworkElement>(element);
            _slot = slot;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
            {
                Subscribe();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 卸载期间可能错过变更：重新加载时补一次当前值。
            if (sender is FrameworkElement element)
            {
                SetFont(element, Get(_slot));
            }

            Subscribe();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // Reparented text can already be loaded again when the old unload arrives.
            if (sender is FrameworkElement { IsLoaded: true }) return;
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (!_subscribed)
            {
                Changed += OnFontsChanged;
                _subscribed = true;
            }
        }

        private void Unsubscribe()
        {
            if (_subscribed)
            {
                Changed -= OnFontsChanged;
                _subscribed = false;
            }
        }

        private void OnFontsChanged(object? sender, EventArgs e)
        {
            if (_element.TryGetTarget(out FrameworkElement? element))
            {
                SetFont(element, Get(_slot));
            }
            else
            {
                Unsubscribe();
            }
        }
    }
}
