using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// subagent 完成消息的原生完成卡片控制器（普通 sealed 组合控制器，非控件子类）。
/// </summary>
/// <remarks>
/// <para>
/// 职责：把一个既有内容元素 <c>content</c> 在「普通展示」与「完成卡片」之间切换。
/// <see cref="Element"/> 是构造时创建的稳定 <see cref="Grid"/> 根容器，<c>content</c> 实例不重建；
/// 卡片模式下 <c>content</c> 被放入原生 <see cref="Expander"/> 且默认收起，展开后原样完整可用。
/// </para>
/// <para>
/// 识别依据（官方 V2 v2.0.24 已核准形状）：父合成完成消息 <c>metadata</c> 的
/// <c>source == "subagent"</c> 且 <c>childID</c> 通过 <see cref="OpenCodeSubagentPresentation.IsOpaqueSessionId"/>；
/// 本视图只信任调用方已识别的 <see cref="OpenCodeSubagentCompletionInfo"/>，不读取正文/XML、
/// 不访问网络/文件/权限。
/// </para>
/// <para>
/// 严格边界：<see cref="Update"/> 的 info 为 null 或 childID 未通过校验时按普通内容展示
/// （不套可见卡片、不留多余边距、不提供导航）；有效时显示紧凑 Fluent 完成卡片，标题取
/// <c>taskTitle</c>（非空）否则「子任务结果」，状态如实展示（running 标注非实时，缺失/未知不假称成功）。
/// 「查看子会话」按钮位于卡片头部，收起状态下即可点击，且不改变展开状态；点击只把已校验的 childID
/// 传给构造回调，不做授权。
/// </para>
/// <para>
/// 展开状态：仅在首次或切换到不同 childID 时默认收起；同一 childID 的后续 <see cref="Update"/>
/// 保留用户展开状态。普通/卡片切换先摘除再挂载，保证 <c>content</c> 不会同时挂在两个父节点。
/// </para>
/// <para>
/// <see cref="Dispose"/> 退订按钮事件并清空回调与缓存 id，防止移除后的 stale 导航。
/// 本视图不负责 messageID 与会话授权；来源/会话/祖先校验由页面接线时完成。
/// </para>
/// </remarks>
internal sealed class OpenCodeSubagentCompletionView : IDisposable
{
    private const string RunningState = "running";
    private const string FallbackTitle = "子任务结果";

    private readonly Grid _root;
    private readonly Border _card;
    private readonly TextBlock _titleText;
    private readonly TextBlock _stateText;
    private readonly Button _viewChildButton;
    private readonly Expander _expander;
    private readonly FrameworkElement _content;

    private Action<string>? _onChildSessionRequested;
    private string? _childSessionId;
    private string? _lastChildId;
    private bool _cardMode;
    private bool _disposed;

    /// <summary>创建完成卡片控制器（默认以普通内容展示，未套卡片）。</summary>
    /// <param name="content">要承载的既有内容元素（实例不重建；由本视图负责入树/移动）。</param>
    /// <param name="onChildSessionRequested">「查看子会话」点击回调（仅收到已校验的 childID）；
    /// 可为 null（只展示不导航）。</param>
    public OpenCodeSubagentCompletionView(FrameworkElement content, Action<string>? onChildSessionRequested = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        _content = content;
        _onChildSessionRequested = onChildSessionRequested;

        _titleText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        _stateText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        _viewChildButton = new Button
        {
            Content = "查看子会话",
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_viewChildButton, "OpenCodeSubagentCompletionViewChild");
        AutomationProperties.SetName(_viewChildButton, "查看子会话");
        _viewChildButton.Click += ViewChildButton_Click;

        // 头部：标题 / 记录状态 / 「查看子会话」按钮（垂直堆叠，窄宽自动换行）。
        var header = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Children = { _titleText, _stateText, _viewChildButton },
        };

        _expander = new Expander
        {
            Header = new TextBlock { Text = "内容", FontSize = 12, Opacity = 0.75 },
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(_expander, "OpenCodeSubagentCompletionViewContent");
        AutomationProperties.SetName(_expander, "内容");

        _card = new Border
        {
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeSubtleCardChromeStyle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { header, _expander },
            },
        };

        _root = new Grid();
        _root.Children.Add(_content);
    }

    /// <summary>承载 UI 的原生根容器（构造时创建的同一 <see cref="Grid"/> 实例；由调用方入树）。</summary>
    public FrameworkElement Element => _root;

    /// <summary>
    /// 就地更新：<paramref name="info"/> 为 null 或 childID 未通过不透明校验时按普通内容展示；
    /// 有效时切到完成卡片并填充标题与状态。根元素身份稳定；仅首次/换 childID 时默认收起。
    /// </summary>
    /// <param name="info">已识别的完成信息；非 subagent 或 childID 无效时为 null。</param>
    /// <param name="taskTitle">任务名（非空时作为卡片标题；否则用「子任务结果」）。</param>
    public void Update(OpenCodeSubagentCompletionInfo? info, string? taskTitle = null)
    {
        if (_disposed)
        {
            return;
        }

        if (info is null || !OpenCodeSubagentPresentation.IsOpaqueSessionId(info.ChildSessionId))
        {
            ShowNormal();
            _viewChildButton.Visibility = Visibility.Collapsed;
            _childSessionId = null;
            _lastChildId = null;
            return;
        }

        var childId = info.ChildSessionId;

        _titleText.Text = string.IsNullOrWhiteSpace(taskTitle) ? FallbackTitle : taskTitle!;
        _stateText.Text = DescribeState(info.State);

        _childSessionId = childId;
        _viewChildButton.Visibility = Visibility.Visible;

        if (!string.Equals(childId, _lastChildId, StringComparison.Ordinal))
        {
            // 首次或换到不同条目：默认收起；同一 childID 的后续更新保留用户展开状态。
            _expander.IsExpanded = false;
            _lastChildId = childId;
        }

        ShowCard();
    }

    /// <summary>
    /// 停用该视图：退订按钮事件并清空回调与缓存 id；此后点击不再触发任何回调
    /// （防止已移除部件产生指向其他会话的 stale 导航）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewChildButton.Click -= ViewChildButton_Click;
        _onChildSessionRequested = null;
        _childSessionId = null;
        _lastChildId = null;
    }

    /// <summary>切到卡片模式：先把 content 从根摘除再挂到 Expander，避免同时挂两个父节点。</summary>
    private void ShowCard()
    {
        if (_cardMode)
        {
            return;
        }

        _root.Children.Remove(_content);
        _expander.Content = _content;
        _root.Children.Add(_card);
        _cardMode = true;
    }

    /// <summary>切回普通模式：先清空 Expander 内容再挂回根，保证 content 完整可用且单父节点。</summary>
    private void ShowNormal()
    {
        if (_cardMode)
        {
            _expander.Content = null;
            _root.Children.Remove(_card);
            _cardMode = false;
        }

        if (_root.Children.Count == 0)
        {
            _root.Children.Add(_content);
        }
    }

    private void ViewChildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // 只回传已通过不透明校验的 childID；不解析正文、不授权。
        if (_childSessionId is { } childId && OpenCodeSubagentPresentation.IsOpaqueSessionId(childId))
        {
            _onChildSessionRequested?.Invoke(childId);
        }
    }

    /// <summary>
    /// 记录状态的中文行：running 标注非实时（该值可能是启动时刻的历史记录），缺失/未知如实展示，
    /// 绝不宣称成功。与 <see cref="OpenCodeSubagentPresentation.DescribeCompletionMarker"/> 的状态措辞保持一致。
    /// </summary>
    private static string DescribeState(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return "（未提供状态）";
        }

        if (!OpenCodeSubagentPresentation.TryDescribeChildState(state, out var label))
        {
            return "未识别状态：" + state;
        }

        return state == RunningState
            ? "记录：" + label + "，非实时"
            : "记录：" + label;
    }
}
