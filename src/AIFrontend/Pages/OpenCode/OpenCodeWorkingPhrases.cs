using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AIFrontend.Pages.OpenCode;

/// <summary>B17：working 指示器所处的真实活动（由服务端事件推出的部件状态决定，不由装饰词推断）。</summary>
internal enum OpenCodeWorkingActivity { General, Reasoning, Tool }

/// <summary>B17：真实执行模型的品牌家族（只来自正在运行的 assistant 消息的 modelID，从不来自 UI 下拉框）。</summary>
internal enum OpenCodeWorkingFamily { Unknown, DeepSeek, Claude, OpenAI, Gemini }

/// <summary>B17：词条分层。</summary>
internal enum OpenCodeWorkingTier { Standard, Playful, Satire }

/// <summary>
/// B17：working 状态的装饰性状态词库（纯逻辑，无 WinUI 依赖）。
/// </summary>
/// <remarks>
/// <para>三层：① 标准状态词（与已知真实活动对应：思考 / 工具 / 一般）；② 通用风趣短句；
/// ③ 低概率的模型家族段子——仅作为“待 copy 审校”的备选池保存，<see cref="EnableModelSatire"/> 默认关闭，不随默认版本上线。</para>
/// <para>纯客户端装饰：从不替换错误、提问、权限等真实状态文本，也不表示模型真的在做这些事。</para>
/// </remarks>
internal static class OpenCodeWorkingPhrases
{
    /// <summary>模型家族段子开关。待 copy 审校前保持 false：段子池保留但不会被选中。</summary>
    internal const bool EnableModelSatire = false;

    /// <summary>首个词之后，最短 / 最长换词间隔（毫秒）：8–12 秒，从不按输出行换词。</summary>
    internal const double MinIntervalMs = 8_000;
    internal const double MaxIntervalMs = 12_000;

    /// <summary>换词时各层概率：段子（仅开关开启且家族已知）→ 风趣 → 其余为标准词。</summary>
    internal const double SatireProbability = 0.04;
    internal const double PlayfulProbability = 0.35;

    internal static IReadOnlyList<string> Standard(OpenCodeWorkingActivity activity) => activity switch
    {
        OpenCodeWorkingActivity.Reasoning => StandardReasoning,
        OpenCodeWorkingActivity.Tool => StandardTool,
        _ => StandardGeneral,
    };

    private static readonly string[] StandardGeneral = { "处理中…", "工作中…" };
    private static readonly string[] StandardReasoning = { "思考中…", "推敲中…" };
    private static readonly string[] StandardTool = { "处理中…", "执行中…" };

    internal static IReadOnlyList<string> Playful { get; } = new[]
    {
        "少女祈祷中…",
        "灵感加载中…",
        "小火慢炖中…",
        "把线索串起来…",
        "给答案打磨边角中…",
        "把复杂问题切片中…",
        "正在给思路排队…",
        "和边界条件打招呼中…",
        "在 TODO 里寻宝…",
        "代码侦探出动…",
        "让比特们排好队…",
        "与编译器谈心中…",
        "整理思绪中…",
        "召唤灵感中…",
        "咖啡因注入中…",
    };

    /// <summary>用户提供的段子原文（待 copy 审校；听写词待核准）。仅在 <see cref="EnableModelSatire"/> 开启时可能出现。</summary>
    internal static IReadOnlyList<string> Satire(OpenCodeWorkingFamily family) => family switch
    {
        OpenCodeWorkingFamily.DeepSeek => SatireDeepSeek,
        OpenCodeWorkingFamily.Claude => SatireClaude,
        OpenCodeWorkingFamily.OpenAI => SatireOpenAI,
        OpenCodeWorkingFamily.Gemini => SatireGemini,
        _ => Array.Empty<string>(),
    };

    private static readonly string[] SatireDeepSeek = { "大肥鱼吃白饭中…", "娱乐中…" };
    private static readonly string[] SatireClaude = { "封禁中文中…", "封禁中国人中…", "检测中国人中…" };
    private static readonly string[] SatireOpenAI = { "降智中…", "tibo重置中…" };
    private static readonly string[] SatireGemini = { "奉承用户中…" };

    /// <summary>
    /// 由真实执行的 modelID 判断品牌家族：沿用 A2 的 <see cref="OpenCodeModelPresentation.Brand"/>（网关后的显式家族词也能识别），
    /// 另补 DeepSeek 家族词。providerID 不参与推断（网关 provider 不代表模型品牌）；未知一律 <see cref="OpenCodeWorkingFamily.Unknown"/>。
    /// </summary>
    internal static OpenCodeWorkingFamily Family(string? executingModelId)
    {
        if (string.IsNullOrWhiteSpace(executingModelId)) return OpenCodeWorkingFamily.Unknown;
        switch (OpenCodeModelPresentation.Brand(executingModelId, null))
        {
            case OpenCodeModelBrand.Claude: return OpenCodeWorkingFamily.Claude;
            case OpenCodeModelBrand.OpenAI: return OpenCodeWorkingFamily.OpenAI;
            case OpenCodeModelBrand.Gemini: return OpenCodeWorkingFamily.Gemini;
        }

        return Regex.IsMatch(executingModelId, @"(?:^|[\s/.:_-])deepseek(?:$|[\s/.:_-]|\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? OpenCodeWorkingFamily.DeepSeek
            : OpenCodeWorkingFamily.Unknown;
    }

    /// <summary>词干（去掉结尾省略号，由指示器用逐步点亮的点来画省略号）。</summary>
    internal static string Stem(string phrase) => phrase.TrimEnd('…', '.', '。');
}

/// <summary>
/// B17：working 指示器的运动规则（纯函数 + 与之逐项等价的合成表达式；相位来自 B13 共享时钟）。
/// </summary>
internal static class OpenCodeWorkingMotion
{
    /// <summary>省略号点数（静态降级时全亮，即静态“…”）。</summary>
    internal const int DotCount = 3;
    internal const double DotDimOpacity = 0.2;

    /// <summary>齿轮：每个时钟周期平滑转一整圈。</summary>
    internal const string GearExpression = "clock.Phase * 360";

    /// <summary>第 i 个点：相位分 4 步（0 / 1 / 2 / 3 个点亮），离散跳动而非渐变。</summary>
    internal const string DotExpression = "Floor(clock.Phase * 4) > i ? 1 : 0.2";

    internal static double GearAngle(double phase) => phase * 360;

    internal static double DotOpacity(double phase, int index) => Math.Floor(phase * 4) > index ? 1 : DotDimOpacity;

    /// <summary>某相位下点亮的点数（0–3）。</summary>
    internal static int LitDots(double phase)
    {
        var lit = 0;
        for (var i = 0; i < DotCount; i++) if (DotOpacity(phase, i) >= 1) lit++;
        return lit;
    }
}

/// <summary>
/// B17：一个进行中分组的换词状态机。working 开始时取一次标准词；之后最多每 8–12 秒换一次（由已有的每秒头部刷新驱动，
/// 不新增计时器）。真实活动变化（思考 ↔ 工具）时，若当前是标准词则立即改为对应的标准词，保证不显示与真实状态矛盾的词；
/// 风趣/段子与活动无关，保留到下次换词。
/// </summary>
internal sealed class OpenCodeWorkingPhrasePicker
{
    private readonly Func<double> _random;
    private readonly bool _satireEnabled;
    private OpenCodeWorkingActivity _activity;
    private OpenCodeWorkingFamily _family;
    private double _nextChangeAt;
    private bool _started;

    internal OpenCodeWorkingPhrasePicker(Func<double>? random = null, bool satireEnabled = OpenCodeWorkingPhrases.EnableModelSatire)
    {
        _random = random ?? Random.Shared.NextDouble;
        _satireEnabled = satireEnabled;
    }

    internal string Current { get; private set; } = "处理中…";
    internal OpenCodeWorkingTier Tier { get; private set; }
    internal bool IsStarted => _started;
    internal int ChangeCount { get; private set; }

    /// <summary>进入 working：取一个与真实活动对应的标准词，并排定下一次换词时间。</summary>
    internal void Start(OpenCodeWorkingActivity activity, OpenCodeWorkingFamily family, double nowMs)
    {
        _started = true;
        _activity = activity;
        _family = family;
        Current = string.Empty; // 新一轮不受上一轮词影响（PickFrom 只在轮内避免重复）。
        Set(PickFrom(OpenCodeWorkingPhrases.Standard(activity)), OpenCodeWorkingTier.Standard);
        Schedule(nowMs);
    }

    /// <summary>离开 working（完成 / 隐藏 / 卸载）：下次进入重新从标准词开始。</summary>
    internal void Stop() => _started = false;

    /// <summary>更新真实上下文（每次流式刷新都可能调用；不会因此换装饰词）。返回当前词是否变化。</summary>
    internal bool UpdateContext(OpenCodeWorkingActivity activity, OpenCodeWorkingFamily family, double nowMs)
    {
        if (!_started) { Start(activity, family, nowMs); return true; }
        var familyChanged = _family != family;
        _family = family;
        if (familyChanged && Tier == OpenCodeWorkingTier.Satire)
        {
            _activity = activity;
            Set(PickFrom(OpenCodeWorkingPhrases.Standard(activity)), OpenCodeWorkingTier.Standard);
            return true;
        }
        if (activity == _activity) return false;
        _activity = activity;
        if (Tier != OpenCodeWorkingTier.Standard) return false;
        var pool = OpenCodeWorkingPhrases.Standard(activity);
        if (Contains(pool, Current)) return false;
        Set(PickFrom(pool), OpenCodeWorkingTier.Standard);
        return true;
    }

    /// <summary>定时检查（约每秒一次）：仅到点才换词。返回当前词是否变化。</summary>
    internal bool Tick(double nowMs)
    {
        if (!_started || nowMs < _nextChangeAt) return false;
        var previous = Current;
        var roll = _random();
        var satire = _satireEnabled ? OpenCodeWorkingPhrases.Satire(_family) : Array.Empty<string>();
        if (satire.Count > 0 && roll < OpenCodeWorkingPhrases.SatireProbability)
            Set(PickFrom(satire), OpenCodeWorkingTier.Satire);
        else if (roll < OpenCodeWorkingPhrases.SatireProbability + OpenCodeWorkingPhrases.PlayfulProbability)
            Set(PickFrom(OpenCodeWorkingPhrases.Playful), OpenCodeWorkingTier.Playful);
        else
            Set(PickFrom(OpenCodeWorkingPhrases.Standard(_activity)), OpenCodeWorkingTier.Standard);
        Schedule(nowMs);
        return !string.Equals(previous, Current, StringComparison.Ordinal);
    }

    private void Schedule(double nowMs)
        => _nextChangeAt = nowMs + OpenCodeWorkingPhrases.MinIntervalMs
            + (OpenCodeWorkingPhrases.MaxIntervalMs - OpenCodeWorkingPhrases.MinIntervalMs) * Clamp01(_random());

    private void Set(string phrase, OpenCodeWorkingTier tier)
    {
        if (!string.Equals(phrase, Current, StringComparison.Ordinal)) ChangeCount++;
        Current = phrase;
        Tier = tier;
    }

    /// <summary>从池中随机取一个，尽量避开当前词（避免“换了个寂寞”）。</summary>
    private string PickFrom(IReadOnlyList<string> pool)
    {
        if (pool.Count == 1) return pool[0];
        var index = (int)(Clamp01(_random()) * pool.Count);
        if (index >= pool.Count) index = pool.Count - 1;
        if (string.Equals(pool[index], Current, StringComparison.Ordinal)) index = (index + 1) % pool.Count;
        return pool[index];
    }

    private static bool Contains(IReadOnlyList<string> pool, string value)
    {
        for (var i = 0; i < pool.Count; i++) if (string.Equals(pool[i], value, StringComparison.Ordinal)) return true;
        return false;
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value >= 1 ? 0.999999 : value;
}
