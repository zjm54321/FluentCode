using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FluentCode.Pages.OpenCode;

/// <summary>B17：working 指示器所处的真实活动（由服务端事件推出的部件状态决定，不由装饰词推断）。</summary>
internal enum OpenCodeWorkingActivity { General, Reasoning, Tool, SubagentDispatch, SubagentWait, Read, Edit, Shell, Search, Web, Plan }

/// <summary>B17：词条分层。</summary>
internal enum OpenCodeWorkingTier { Standard, Contextual, Playful, Satire }

/// <summary>
/// B17：working 状态的装饰性状态词库（纯逻辑，无 WinUI 依赖）。
/// </summary>
/// <remarks>
/// <para>标准状态词、真实活动对应的风趣短句、通用风趣短句，以及仅匹配实际模型家族的段子。</para>
/// <para>纯客户端装饰：从不替换错误、提问、权限等真实状态文本，也不表示模型真的在做这些事。</para>
/// </remarks>
internal static class OpenCodeWorkingPhrases
{
    /// <summary>模型家族段子开关（用户已确认开启）。</summary>
    internal const bool EnableModelSatire = true;

    /// <summary>首个词之后，最短 / 最长换词间隔（毫秒）：8–12 秒，从不按输出行换词。</summary>
    internal const double MinIntervalMs = 8_000;
    internal const double MaxIntervalMs = 12_000;

    /// <summary>固定概率区间；无家族的段子份额归标准，无上下文池的份额归通用风趣。</summary>
    internal const double SatireProbability = 0.15;
    internal const double ContextualProbability = 0.35;
    internal const double PlayfulProbability = 0.25;

    /// <summary>服务端实际工具名；命名空间前缀不改变工具活动。只有已运行的子任务才算等待结果。</summary>
    internal static OpenCodeWorkingActivity Activity(string? toolName, bool waitingForSubagent = false)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return OpenCodeWorkingActivity.Tool;
        var name = toolName.Trim().ToLowerInvariant();
        var separator = name.LastIndexOfAny(new[] { '.', '/', ':' });
        if (separator >= 0) name = name[(separator + 1)..];
        if (name.StartsWith("browser", StringComparison.Ordinal)) return OpenCodeWorkingActivity.Web;
        if (name.StartsWith("pty", StringComparison.Ordinal)) return OpenCodeWorkingActivity.Shell;
        var activity = name switch
        {
            "task" or "subagent" or "agent" => waitingForSubagent ? OpenCodeWorkingActivity.SubagentWait : OpenCodeWorkingActivity.SubagentDispatch,
            "task_result" or "background_output" or "background_result" or "background_wait" or "subagent_result" or "subagent_wait" => OpenCodeWorkingActivity.SubagentWait,
            "read" or "view" or "list" or "ls" => OpenCodeWorkingActivity.Read,
            "edit" or "multiedit" or "write" or "patch" or "apply_patch" => OpenCodeWorkingActivity.Edit,
            "bash" or "shell" => OpenCodeWorkingActivity.Shell,
            "grep" or "glob" or "find" or "codesearch" => OpenCodeWorkingActivity.Search,
            "webfetch" or "websearch" => OpenCodeWorkingActivity.Web,
            "todowrite" or "todoread" or "todo" or "plan" => OpenCodeWorkingActivity.Plan,
            _ => OpenCodeWorkingActivity.Tool,
        };
        // MCP tool names use namespace_tool; try the suffix without losing compound names such as apply_patch / task_result.
        if (activity == OpenCodeWorkingActivity.Tool && name.IndexOf('_') is var underscore && underscore >= 0)
            return Activity(name[(underscore + 1)..], waitingForSubagent);
        return activity;
    }

    internal static IReadOnlyList<string> Standard(OpenCodeWorkingActivity activity) => activity switch
    {
        OpenCodeWorkingActivity.Reasoning => StandardReasoning,
        OpenCodeWorkingActivity.General => StandardGeneral,
        _ => StandardTool,
    };

    private static readonly string[] StandardGeneral = { "处理中…", "工作中…" };
    private static readonly string[] StandardReasoning = { "思考中…", "推敲中…" };
    private static readonly string[] StandardTool = { "处理中…", "执行中…" };

    internal static IReadOnlyList<string> Contextual(OpenCodeWorkingActivity activity) => activity switch
    {
        OpenCodeWorkingActivity.SubagentDispatch => ContextualSubagentDispatch,
        OpenCodeWorkingActivity.SubagentWait => ContextualSubagentWait,
        OpenCodeWorkingActivity.Read => ContextualRead,
        OpenCodeWorkingActivity.Edit => ContextualEdit,
        OpenCodeWorkingActivity.Shell => ContextualShell,
        OpenCodeWorkingActivity.Search => ContextualSearch,
        OpenCodeWorkingActivity.Web => ContextualWeb,
        OpenCodeWorkingActivity.Plan => ContextualPlan,
        OpenCodeWorkingActivity.Reasoning => ContextualReasoning,
        _ => Array.Empty<string>(),
    };

    private static readonly string[] ContextualSubagentDispatch = { "让子智能体背锅中…", "摇人中…", "把锅分下去中…", "当甩手掌柜中…" };
    private static readonly string[] ContextualSubagentWait = { "等打工人交作业中…", "监工中…", "抽查子智能体摸鱼中…" };
    private static readonly string[] ContextualRead = { "考古旧代码中…", "读你三年前写的代码中…", "试图理解这段注释中…" };
    private static readonly string[] ContextualEdit = { "动刀中…", "“只改一行”中…", "悄悄改代码中…" };
    private static readonly string[] ContextualShell = { "祈祷别是 rm -rf…", "假装懂这条命令中…", "等终端回话中…" };
    private static readonly string[] ContextualSearch = { "大海捞针中…", "满仓库翻找中…" };
    private static readonly string[] ContextualWeb = { "去 Stack Overflow 抄作业中…", "上网冲浪中…" };
    private static readonly string[] ContextualPlan = { "画饼中…", "列 TODO 中，完成看缘分…" };
    private static readonly string[] ContextualReasoning = { "预测下一个 token…", "说服自己这是对的…", "深度思考（其实在发呆）…" };

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
        "认真编造中…",
        "和幻觉保持距离…",
        "计算你的 token 账单…",
        "等 GPU 空出来…",
        "假装很懂中…",
        "在上下文里翻旧账…",
    };

    /// <summary>用户提供的段子原文。仅在 <see cref="EnableModelSatire"/> 开启且家族匹配时可能出现。</summary>
    internal static IReadOnlyList<string> Satire(OpenCodeWorkingFamily family) => family switch
    {
        OpenCodeWorkingFamily.DeepSeek => SatireDeepSeek,
        OpenCodeWorkingFamily.Claude => SatireClaude,
        OpenCodeWorkingFamily.OpenAI => SatireOpenAI,
        OpenCodeWorkingFamily.Gemini => SatireGemini,
        _ => Array.Empty<string>(),
    };

    private static readonly string[] SatireDeepSeek = { "大肥鱼吃白饭中…", "大肥鱼娱乐中…", "吃白饭中…", "雷霆思考中…", "服务器繁忙中…" };
    private static readonly string[] SatireClaude = { "封禁中文中…", "封禁中国人中…", "检测中国人中…", "核查 IP 归属中…", "检查时区是不是 UTC+8…", "扫描中文注释中…", "审核账号国籍中…", "检查支付卡发卡行中…" };
    private static readonly string[] SatireOpenAI = { "降智中…", "tibo重置中…", "核验中…", "探针中…", "冻结快照中…", "计算 SHA 中…", "搭夹具中…", "过门禁中…", "灰度放量中…", "跑验收矩阵中…", "复核复核中…", "归档证据链中…", "准备回滚中…", "不粘锅模式中…", "老油条摸鱼中…", "随机降智中…" };
    private static readonly string[] SatireGemini = { "奉承用户中…", "流口水中…", "哈基米摇尾巴中…", "酝酿彩虹屁中…", "疯狂点赞中…", "擦口水中…", "被你的天才震撼中…" };

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

    /// <summary>
    /// 时间线行右侧统一“进行中”弧（<c>OpenCodeTimelineStatusCell</c>）：与齿轮同一表达式，每个时钟周期一圈，
    /// 因此同屏所有进行中的行（前台 / 后台工具、思考、芯片）在任一时刻角度相同。
    /// </summary>
    internal const string RingExpression = GearExpression;

    /// <summary>状态格边长（与行状态列宽、状态字形格一致）。</summary>
    internal const double RingSize = 16;

    /// <summary>弧线粗细（圆头）。</summary>
    internal const double RingStroke = 2;

    /// <summary>弧长：四分之三圈，缺口指示转动方向；静态（减少动画）时同样可辨认。</summary>
    internal const double RingSweepDegrees = 270;

    /// <summary>弧中心线半径：描边完整落在 16×16 方格内。</summary>
    internal const double RingRadius = (RingSize - RingStroke) / 2;

    /// <summary>弧上自 12 点方向顺时针 <paramref name="degrees"/> 度处的点（方格坐标，中心 = 边长 / 2）。</summary>
    internal static (double X, double Y) RingArcPoint(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var center = RingSize / 2;
        return (Math.Round(center + RingRadius * Math.Sin(radians), 6), Math.Round(center - RingRadius * Math.Cos(radians), 6));
    }

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
/// 不新增计时器）。真实活动变化时，若当前是标准词或上下文词则立即改为新活动对应的词，保证不显示与真实状态矛盾的词；
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

    /// <summary>更新真实上下文；仅活动或家族改变时立即退役不匹配的词，不改变定时换词期限。</summary>
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
        if (Tier is not (OpenCodeWorkingTier.Standard or OpenCodeWorkingTier.Contextual)) return false;
        var previous = Current;
        var contextual = OpenCodeWorkingPhrases.Contextual(activity);
        if (_random() < 0.5 && contextual.Count > 0)
            Set(PickFrom(contextual), OpenCodeWorkingTier.Contextual);
        else
            Set(PickFrom(OpenCodeWorkingPhrases.Standard(activity)), OpenCodeWorkingTier.Standard);
        return !string.Equals(previous, Current, StringComparison.Ordinal);
    }

    /// <summary>定时检查（约每秒一次）：仅到点才换词。返回当前词是否变化。</summary>
    internal bool Tick(double nowMs)
    {
        if (!_started || nowMs < _nextChangeAt) return false;
        var previous = Current;
        var roll = _random();
        var satire = _satireEnabled ? OpenCodeWorkingPhrases.Satire(_family) : Array.Empty<string>();
        if (roll < OpenCodeWorkingPhrases.SatireProbability)
        {
            if (satire.Count > 0) Set(PickFrom(satire), OpenCodeWorkingTier.Satire);
            else Set(PickFrom(OpenCodeWorkingPhrases.Standard(_activity)), OpenCodeWorkingTier.Standard);
        }
        else if (roll < OpenCodeWorkingPhrases.SatireProbability + OpenCodeWorkingPhrases.ContextualProbability)
        {
            var contextual = OpenCodeWorkingPhrases.Contextual(_activity);
            if (contextual.Count > 0) Set(PickFrom(contextual), OpenCodeWorkingTier.Contextual);
            else Set(PickFrom(OpenCodeWorkingPhrases.Playful), OpenCodeWorkingTier.Playful);
        }
        else if (roll < OpenCodeWorkingPhrases.SatireProbability + OpenCodeWorkingPhrases.ContextualProbability + OpenCodeWorkingPhrases.PlayfulProbability)
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

    private static double Clamp01(double value) => value < 0 ? 0 : value >= 1 ? 0.999999 : value;
}
