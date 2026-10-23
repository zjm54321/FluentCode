using System;
using System.Threading.Tasks;
using AIFrontend.Pages.Web;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIFrontend.Pages.Settings;

/// <summary>
/// 应用内设置页：以既有 <c>ContentFrame</c> 导航承载可复用的 <see cref="SettingsView"/>。
/// </summary>
/// <remarks>
/// <para>不复用 <c>ContentDialog</c> / 独立窗口 / 模态浮层；作为普通 <see cref="Page"/> 进入
/// Frame 返回栈，由主窗口顶栏的原生返回按钮返回上一页，不新增任何窗口。</para>
/// <para>共享服务：始终复用 <see cref="NormalPage.SharedSettingsService"/> /
/// <see cref="NormalPage.SharedSecretStore"/>（应用级惰性单例），不新建
/// <c>SettingsService</c> / <c>DpapiSecretStore</c>，避免重复订阅与状态分裂。</para>
/// <para>Cookie 清理回调：可经导航参数注入（<c>Func&lt;WebProvider, Task&gt;?</c>）。
/// 主窗口现提供基于窗口临时 <c>WebViewHost</c> 的实现，**不依赖** <c>NormalPage</c> 处于已加载态；
/// 未注入时 <see cref="SettingsView"/> 禁用清除按钮并说明，绝不虚构“已清理”。</para>
/// <para>离开保护：主窗口经 <c>Frame.Navigating</c> 拦下有未保存输入的导航，调用本页
/// <see cref="ConfirmLeaveAsync"/> 做页内三态确认，并按结果调用
/// <see cref="TrySavePendingEditsAsync"/> / <see cref="DiscardPendingEdits"/>。</para>
/// </remarks>
public sealed partial class SettingsPage : Page
{
    private SettingsView? _view;

    /// <summary>
    /// 设置页内主题保存成功后转发（仅供主窗口把主题应用到自己的根元素）。
    /// 除 <see cref="SettingsView.ThemeChanged"/> 外，加载完成后也会以已保存主题触发一次。
    /// </summary>
    public event EventHandler<SettingsThemeChangedEventArgs>? ThemeChanged;

    /// <summary>当前承载的设置视图（导航离开后为 null）。</summary>
    public SettingsView? View => _view;

    /// <summary>当前设置视图是否存在未保存输入（无视图时为 false）。供主窗口在离开前判断是否提示。</summary>
    public bool HasUnsavedChanges => _view?.HasUnsavedChanges == true;

    public SettingsPage()
    {
        InitializeComponent();
        AutomationProperties.SetAutomationId(this, "SettingsPage");
        AutomationProperties.SetName(this, "设置");
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 参数：可选的 cookie 清理回调（不依赖 NormalPage 加载态）；无参数时为 null。
        Func<WebProvider, Task>? clearCookiesAsync = e.Parameter as Func<WebProvider, Task>;

        var view = new SettingsView(
            NormalPage.SharedSettingsService,
            NormalPage.SharedSecretStore,
            clearCookiesAsync);
        view.ThemeChanged += OnViewThemeChanged;
        _view = view;
        Content = view;

        // 主动加载并应用一次已保存主题。LoadAsync 自身捕获加载异常并以 InfoBar 呈现（不抛出）。
        _ = LoadAndApplyThemeAsync(view);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        SettingsView? view = _view;
        _view = null;
        Content = null;
        if (view is null)
        {
            return;
        }

        view.ThemeChanged -= OnViewThemeChanged;
        // 离开页面：结束 SettingsView 内仍在等待的内联确认（按“取消”处理），
        // 不依赖其 Unloaded 触发时机，确保等待方不悬挂。
        view.CancelPendingInteractions();
    }

    /// <summary>
    /// 离开前由主窗口调用：在设置页内做三态确认（保存并离开 / 放弃并离开 / 取消）。
    /// 无视图（已离开）时按“取消”返回，避免误放行。
    /// </summary>
    public Task<SettingsLeaveChoice> ConfirmLeaveAsync()
        => _view?.ConfirmLeaveAsync() ?? Task.FromResult(SettingsLeaveChoice.Cancel);

    /// <summary>离开前由主窗口调用：提交全部未保存输入；任一项失败返回 <c>false</c>（调用方留在本页）。</summary>
    public Task<bool> TrySavePendingEditsAsync()
        => _view?.TrySavePendingEditsAsync() ?? Task.FromResult(true);

    /// <summary>离开前由主窗口调用：放弃全部未保存输入。</summary>
    public void DiscardPendingEdits() => _view?.DiscardPendingEdits();

    private async Task LoadAndApplyThemeAsync(SettingsView view)
    {
        await view.LoadAsync();
        if (ReferenceEquals(_view, view))
        {
            ThemeChanged?.Invoke(this, new SettingsThemeChangedEventArgs(view.Settings.Theme));
        }
    }

    private void OnViewThemeChanged(object? sender, SettingsThemeChangedEventArgs e)
    {
        ThemeChanged?.Invoke(this, e);
    }
}
