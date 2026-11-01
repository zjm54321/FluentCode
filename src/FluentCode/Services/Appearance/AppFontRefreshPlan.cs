using System;
using System.Collections.Generic;

namespace FluentCode.Services.Appearance;

/// <summary>
/// 运行期字体刷新的纯逻辑（不依赖 WinUI，可无头测试）：记录各槽位的历史字体源，
/// 判断某个元素当前的字体源应被换成哪个新字体源，并合并同一轮内的多次刷新请求。
/// </summary>
/// <remarks>
/// <para><b>为什么需要</b>：运行期改字体时，已存在元素的 ThemeResource 不会自动重新解析。旧实现靠同帧
/// RequestedTheme 往返强制重解析，在 ComboBox 选择/弹出层关闭期间触发原生崩溃（CoreMessagingXP.dll
/// 0xc000027b，D1）。新实现不碰主题：在稍后的 DispatcherQueue 低优先级时机遍历已登记根元素与已打开弹出层，
/// 只把“当前字体源等于某槽位旧字体源”的元素改为该槽位新字体源——普通的依赖属性赋值，不会触发主题 / 资源
/// 系统的重入。</para>
/// <para>判断按字体源字符串（<c>FontFamily.Source</c>，忽略大小写与首尾空白）进行：图标字体
/// （Segoe Fluent Icons / SymbolThemeFontFamily）及其他显式指定的字体永远不会被误改。</para>
/// </remarks>
public sealed class AppFontRefreshPlan
{
    /// <summary>每个槽位保留的历史字体源上限（防止反复切换导致无界增长）。</summary>
    public const int MaxHistoryPerSlot = 16;

    private static readonly AppFontSlot[] Priority = { AppFontSlot.Body, AppFontSlot.Monospace, AppFontSlot.Serif };

    private readonly Dictionary<AppFontSlot, string> _current = new();
    private readonly Dictionary<AppFontSlot, List<string>> _history = new();
    private bool _pending;
    private double _textSize = AppFontDefaults.DefaultTextSize;

    /// <summary>以启动时生效的三个字体源创建。</summary>
    public AppFontRefreshPlan(string body, string serif, string monospace)
    {
        foreach (AppFontSlot slot in Priority)
        {
            _history[slot] = new List<string>();
        }

        _current[AppFontSlot.Body] = Normalize(body);
        _current[AppFontSlot.Serif] = Normalize(serif);
        _current[AppFontSlot.Monospace] = Normalize(monospace);
    }

    /// <summary>字体代数：每次实际变化 +1（已登记根元素据此判断是否错过了刷新）。</summary>
    public int Generation { get; private set; }

    /// <summary>是否已有一次尚未执行的延迟刷新（同一轮内的后续请求被合并）。</summary>
    public bool IsRefreshPending => _pending;

    /// <summary>当前槽位字体源（规范化后）。</summary>
    public string Current(AppFontSlot slot) => _current[slot];

    /// <summary>
    /// 记录一次新的字体源组合。返回是否有任一槽位实际变化（变化时代数 +1，旧值进入历史）。
    /// </summary>
    public bool Advance(string body, string serif, string monospace, double textSize = AppFontDefaults.DefaultTextSize)
    {
        bool changed = false;
        changed |= AdvanceSlot(AppFontSlot.Body, body);
        changed |= AdvanceSlot(AppFontSlot.Serif, serif);
        changed |= AdvanceSlot(AppFontSlot.Monospace, monospace);
        changed |= _textSize != textSize;
        _textSize = textSize;
        if (changed)
        {
            Generation++;
        }

        return changed;
    }

    /// <summary>
    /// 元素当前字体源 <paramref name="elementSource"/> 应换成的槽位；<c>null</c> = 不动它。
    /// 仅当它等于某槽位的<strong>历史</strong>字体源、且不等于任何槽位的<strong>当前</strong>字体源时才替换；
    /// 同一历史源属于多个槽位时按 正文 → 等宽 → 衬线 优先。
    /// </summary>
    public AppFontSlot? Resolve(string? elementSource)
    {
        if (string.IsNullOrWhiteSpace(elementSource))
        {
            return null;
        }

        string source = Normalize(elementSource);
        foreach (string current in _current.Values)
        {
            if (string.Equals(current, source, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        foreach (AppFontSlot slot in Priority)
        {
            foreach (string old in _history[slot])
            {
                if (string.Equals(old, source, StringComparison.OrdinalIgnoreCase))
                {
                    return slot;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 请求一次延迟刷新。返回 <c>true</c> 表示调用方应当把刷新排到稍后的调度时机；
    /// 已有待执行刷新时返回 <c>false</c>（合并：那一次执行时会使用最新字体）。
    /// </summary>
    public bool TryBeginRefresh()
    {
        if (_pending)
        {
            return false;
        }

        _pending = true;
        return true;
    }

    /// <summary>延迟刷新开始执行（或排队失败）时调用，之后的请求会重新排队。</summary>
    public void EndRefresh() => _pending = false;

    private bool AdvanceSlot(AppFontSlot slot, string next)
    {
        string normalized = Normalize(next);
        string previous = _current[slot];
        if (string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        List<string> history = _history[slot];
        history.RemoveAll(h => string.Equals(h, previous, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, previous);
        if (history.Count > MaxHistoryPerSlot)
        {
            history.RemoveRange(MaxHistoryPerSlot, history.Count - MaxHistoryPerSlot);
        }

        _current[slot] = normalized;
        return true;
    }

    private static string Normalize(string source) => (source ?? string.Empty).Trim();
}
