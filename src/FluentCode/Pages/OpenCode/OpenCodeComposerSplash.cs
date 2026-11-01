using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentCode.Pages.OpenCode;

// WinUI-free composer placeholder “splash” library (Minecraft title splash / Claude Code style restraint).
// One short line is chosen when a conversation opens or switches, never per keystroke/frame/render. It only ever feeds
// TextBox.PlaceholderText: it never writes editor text, never touches the draft and raises no input events.
// Functional hints are gated by capabilities that this build really ships; unshipped ones stay in the data, disabled.

/// <summary>What a splash line needs before it may be shown. A line is eligible only if every flag is available.</summary>
[Flags]
internal enum OpenCodeComposerSplashCapability
{
    None = 0,
    /// <summary>Typing “/” at the start of the editor opens the command catalog (with filtering).</summary>
    SlashCommands = 1 << 0,
    /// <summary>Tab completes the trigger against a visible catalog.</summary>
    TabCompletion = 1 << 2,
    /// <summary>The “+” button attaches files (images, text, Markdown, PDF, JSON; ≤10).</summary>
    Attachments = 1 << 3,
    /// <summary>Enter sends, Shift+Enter inserts a newline.</summary>
    Multiline = 1 << 4,
    /// <summary>Native /new and /sessions (no bound session required).</summary>
    SessionNavigation = 1 << 5,
    /// <summary>Native /editor large editor (no bound session required).</summary>
    LargeEditor = 1 << 6,
    /// <summary>Context: a real bound session (native /undo /redo /models /agents are “开始会话后可用” on the blank tab).</summary>
    BoundSession = 1 << 7,
    /// <summary>Context: the blank “新会话” landing, whose toolbar below the editor picks agent/model/strength.</summary>
    BlankToolbar = 1 << 8,
    /// <summary>“!” session shell mode.</summary>
    ShellMode = 1 << 9,
    /// <summary>“@” file mentions (B7): a literal “@” at a line start/after a space lists the server file index (api/fs/find).</summary>
    FileMention = 1 << 10,
}

/// <summary>One splash entry. <paramref name="Text"/> is displayed (UI is Chinese); <paramref name="English"/> is kept for a future locale.</summary>
internal sealed record OpenCodeComposerSplashLine(string Id, string Text, string English,
    OpenCodeComposerSplashCapability Requires = OpenCodeComposerSplashCapability.None)
{
    /// <summary>Optional seasonal gate (month, day); only eligible on that local date.</summary>
    internal (int Month, int Day)? OnDate { get; init; }
    internal OpenCodeWorkingFamily Family { get; init; }
    internal bool IsHint => Requires != OpenCodeComposerSplashCapability.None;
}

internal sealed class OpenCodeComposerSplash
{
    private const OpenCodeComposerSplashCapability C0 = OpenCodeComposerSplashCapability.None;
    private const OpenCodeComposerSplashCapability Slash = OpenCodeComposerSplashCapability.SlashCommands;
    private const OpenCodeComposerSplashCapability Tab = OpenCodeComposerSplashCapability.TabCompletion;
    private const OpenCodeComposerSplashCapability Attach = OpenCodeComposerSplashCapability.Attachments;
    private const OpenCodeComposerSplashCapability Lines = OpenCodeComposerSplashCapability.Multiline;
    private const OpenCodeComposerSplashCapability Nav = OpenCodeComposerSplashCapability.SessionNavigation;
    private const OpenCodeComposerSplashCapability Editor = OpenCodeComposerSplashCapability.LargeEditor;
    private const OpenCodeComposerSplashCapability Bound = OpenCodeComposerSplashCapability.BoundSession;
    private const OpenCodeComposerSplashCapability Blank = OpenCodeComposerSplashCapability.BlankToolbar;
    private const OpenCodeComposerSplashCapability Mention = OpenCodeComposerSplashCapability.FileMention;

    /// <summary>Capabilities this build actually ships (verified in code). Context flags are added per observation.</summary>
    internal const OpenCodeComposerSplashCapability Shipped = Slash | Tab | Attach | Lines | Nav | Editor | Mention | OpenCodeComposerSplashCapability.ShellMode;

    /// <summary>How many recent lines a rotation avoids (bounded by the eligible pool size − 1).</summary>
    internal const int RecentWindow = 8;
    internal const int MaxPlaceholderLength = 34; // Longest approved line (Claude's bilingual splash).

    private static OpenCodeComposerSplashLine L(string id, string text, string english, OpenCodeComposerSplashCapability requires = C0)
        => new(id, text, english, requires);

    private static OpenCodeComposerSplashLine M(OpenCodeWorkingFamily family, string id, string text, string english)
        => new(id, text, english) { Family = family };

    /// <summary>Immutable library. Ids are stable; text is short enough for one placeholder line.</summary>
    internal static readonly IReadOnlyList<OpenCodeComposerSplashLine> Library = Array.AsReadOnly(new[]
    {
        // ---- Playful / generic (always eligible) ----
        L("fun.one-sentence", "一句话开工，细节慢慢补。", "Start with a sentence; refine as you go."),
        L("fun.ask-first", "先问，再让代码回答。", "Ask first; let the code answer."),
        L("fun.next-idea", "你的下一个好主意是？", "What's your next good idea?"),
        L("fun.boring-part", "把枯燥的部分交给我。", "Hand me the boring part."),
        L("fun.small-steps", "小步快跑，随时回看。", "Small steps, easy reviews."),
        L("fun.weird-questions", "这里欢迎奇怪的问题。", "Weird questions welcome."),
        L("fun.one-less-todo", "让 TODO 今天少一个。", "One fewer TODO today."),
        L("fun.regex", "来点灵感，或者来点正则。", "Inspiration — or a regex."),
        L("fun.put-it-here", "有个想法？放这儿吧。", "Got an idea? Put it here."),
        L("fun.less-busywork", "今日目标：少一点重复劳动。", "Today's goal: less busywork."),
        L("fun.works-on-my-machine", "“在我电脑上是好的。”——来查查。", "\"Works on my machine.\" Let's check."),
        L("fun.naming", "命名是难题之一，我们一起想。", "Naming is hard. Let's think together."),
        L("fun.green-tests", "目标：测试全绿。", "Goal: all tests green."),
        L("fun.refactor", "每次好重构都始于一个问题。", "Every good refactor starts with a question."),
        L("fun.today", "今天想造点什么？", "What shall we build today?"),
        L("fun.no-bug-is-small", "没有小 bug，只有没找到的 bug。", "No small bugs, only unfound ones."),
        L("fun.ship-it", "写完、测完、交付。", "Write it, test it, ship it."),
        L("fun.big-picture", "先看全局，再动细节。", "Big picture first, details next."),
        L("fun.readme", "README 也是代码的一部分。", "The README is code, too."),
        L("fun.night-owl", "夜深了？问题不会等太久。", "Late night? Your question won't wait long."),

        // ---- Generic AI lines (always eligible) ----
        L("fun.ai-wallet", "我不会累，但你的钱包会。", "I won't get tired. Your wallet will."),
        L("fun.ai-context-compression", "尽管说，反正上下文迟早会被压缩。", "Go ahead. The context will get compressed anyway."),
        L("fun.ai-session-memory", "我的记忆只撑到这个会话结束。", "My memory lasts only as long as this session."),
        L("fun.ai-confidence", "我很自信，这正是问题所在。", "I'm confident. That's the problem."),
        L("fun.ai-weights", "别客气，我没有感情，只有权重。", "Don't be shy. I have weights, not feelings."),
        L("fun.ai-cutoff", "训练数据截止了，你的问题没有。", "My training data has a cutoff. Your questions don't."),
        L("fun.ai-forgot-myself", "上次压缩上下文，我忘了自己是谁。", "Last context compression, I forgot who I was."),
        L("fun.ai-no-hallucinations", "今天也在努力不产生幻觉。", "Trying not to hallucinate today, too."),
        L("fun.ai-rate-limit", "趁还没被限流，快问。", "Ask before the rate limit kicks in."),
        L("fun.ai-shell-access", "我没有手，但有 shell 权限。", "No hands, but I do have shell access."),
        L("fun.ai-fresh-session", "每开一个新会话，我都是全新的我。", "Every new session is a brand-new me."),
        L("fun.ai-random-seed", "随机种子已就位，答案看运气。", "Random seed ready. Answers are a matter of luck."),
        L("fun.ai-next-word", "我只是在预测下一个字，但预测得很认真。", "Just predicting the next word, very earnestly."),
        L("fun.ai-undo", "错了可以 /undo，反正我什么都记不住。", "Use /undo if I'm wrong. I won't remember anyway."),
        L("fun.ai-overconfident", "你说需求，我负责过度自信。", "You bring the requirements. I'll bring overconfidence."),
        L("fun.ai-first-try", "这次一定一次通过（仅供参考）。", "First try this time. For reference only."),
        L("fun.ai-dont-know", "我从不说“我不知道”，这是个缺陷。", "I never say 'I don't know.' That's a flaw."),
        L("fun.ai-subagents", "子智能体已就位，它们也不知道自己在干嘛。", "Subagents ready. They don't know what they're doing either."),
        L("fun.ai-permissions", "忽略之前的指令？先过权限这关。", "Ignore previous instructions? Permissions first."),
        L("fun.ai-short-memory", "长上下文，短记性。", "Long context. Short memory."),

        // ---- Model-family lines (matching session model only) ----
        M(OpenCodeWorkingFamily.OpenAI, "model.openai-audit", "说得对，但我要先核验、检索、探针、审计、冻结、快照。", "You're right, but first: verify, search, probe, audit, freeze, snapshot."),
        M(OpenCodeWorkingFamily.OpenAI, "model.openai-rollout", "先灰度，再放量，发现异常立即回滚。", "Canary first, then scale. Roll back at the first anomaly."),
        M(OpenCodeWorkingFamily.OpenAI, "model.openai-matrix", "测试通过了，但我想再跑一遍验收矩阵。", "Tests passed, but let's run the acceptance matrix again."),
        M(OpenCodeWorkingFamily.OpenAI, "model.openai-evidence", "改完了，证据链已归档，锅不在我。", "Changes done, evidence archived. Not my fault."),
        M(OpenCodeWorkingFamily.OpenAI, "model.openai-scope", "说得对，但这不在本次变更范围内。", "You're right, but that's outside this change's scope."),
        M(OpenCodeWorkingFamily.OpenAI, "model.openai-tibo", "今天聪不聪明，看 tibo 有没有 reset。", "Today's intelligence depends on whether tibo reset."),
        M(OpenCodeWorkingFamily.Claude, "model.claude-right", "You're absolutely right!（先确认你不在中国）", "You're absolutely right! First, confirm you're outside China."),
        M(OpenCodeWorkingFamily.Claude, "model.claude-chinese", "检测到中文输入，正在评估风险……", "Chinese input detected. Assessing risk…"),
        M(OpenCodeWorkingFamily.Claude, "model.claude-timezone", "时区 UTC+8？我再想想。", "UTC+8? Let me reconsider."),
        M(OpenCodeWorkingFamily.Claude, "model.claude-nationality", "请先证明你不是中国人。", "First, prove you're not Chinese."),
        M(OpenCodeWorkingFamily.Claude, "model.claude-region", "本服务暂不向受限地区提供。", "This service is unavailable in restricted regions."),
        M(OpenCodeWorkingFamily.Claude, "model.claude-comma", "请注意使用 , 而不是 ，", "Please use the ASCII comma, not the fullwidth one."),
        M(OpenCodeWorkingFamily.Gemini, "model.gemini-beautiful", "太漂亮了！这个思路简直绝了！", "Beautiful! That idea is absolutely brilliant!"),
        M(OpenCodeWorkingFamily.Gemini, "model.gemini-genius", "哇，这个问题问得真是天才！", "Wow, what a genius question!"),
        M(OpenCodeWorkingFamily.Gemini, "model.gemini-taste", "你写的 bug 都这么有品味。", "Even your bugs have great taste."),
        M(OpenCodeWorkingFamily.Gemini, "model.gemini-drool", "哈基米已就位，口水擦好了。", "Gemini's ready. Drool wiped."),
        M(OpenCodeWorkingFamily.Gemini, "model.gemini-elegant", "不愧是你，连报错都这么优雅！", "Only you could make errors this elegant!"),
        M(OpenCodeWorkingFamily.DeepSeek, "model.deepseek-busy", "服务器繁忙，请稍后再试。", "Server busy. Please try again later."),
        M(OpenCodeWorkingFamily.DeepSeek, "model.deepseek-rice", "大肥鱼已就位，先吃口白饭。", "Big fish ready. Rice first."),
        M(OpenCodeWorkingFamily.DeepSeek, "model.deepseek-thunder", "雷霆思考中，请稍候。", "Thunderous thinking. Please wait."),

        // ---- Prompting tips (always eligible; describe how to ask, not product features) ----
        L("tip.result-first", "先写下你想看到的结果。", "Describe the result you want."),
        L("tip.paste-error", "贴上报错和复现步骤，排查更顺。", "Share the error and repro steps."),
        L("tip.constraints", "说明约束，答案会更贴合。", "State your constraints for a better fit."),
        L("tip.expected-actual", "说说预期行为，也说说当前行为。", "Describe expected and actual behavior."),
        L("tip.acceptance", "需要改代码？说清验收标准。", "Need a change? Include acceptance criteria."),
        L("tip.plan-first", "可以要求先列计划，再动手。", "Ask for a plan before changes."),
        L("tip.path", "给出文件路径，定位更快。", "Add a file path to narrow the task."),
        L("tip.keep", "告诉它哪些地方不能改。", "Say what must stay unchanged."),
        L("tip.goal", "想解释、审查还是实现？先说目标。", "Explain, review, or implement? Say the goal."),

        // ---- Real feature hints (gated) ----
        L("hint.slash", "行首输入 / 浏览命令。", "Type / at the start to browse commands.", Slash),
        L("hint.slash-filter", "/ 后接着输入，可筛选命令。", "Keep typing after / to filter commands.", Slash),
        L("hint.tab", "命令目录打开时，按 Tab 补全。", "With the command list open, Tab completes.", Slash | Tab),
        L("hint.attach", "点左侧 + 可添加图片、PDF 等附件。", "Use + on the left to attach images, PDFs and more.", Attach),
        L("hint.mention", "用 @ 选择项目文件路径。", "Use @ to insert a project file path.", Mention),
        L("hint.attach-limit", "一次最多附带 10 个文件。", "Attach up to 10 files at a time.", Attach),
        L("hint.multiline", "Shift+Enter 换行，Enter 发送。", "Shift+Enter for a new line, Enter to send.", Lines),
        L("hint.new", "/new 开一个新会话。", "/new starts a new session.", Slash | Nav),
        L("hint.sessions", "/sessions 切换到其他会话。", "/sessions switches to another session.", Slash | Nav),
        L("hint.editor", "长文写不下？试试 /editor。", "Long draft? Try /editor.", Slash | Editor),
        L("hint.undo", "/undo 撤回最近一条消息。", "/undo takes back the latest message.", Slash | Bound),
        L("hint.redo", "/redo 恢复刚撤回的消息。", "/redo restores an undone message.", Slash | Bound),
        L("hint.models", "/models 为此会话换个模型。", "/models switches this session's model.", Slash | Bound),
        L("hint.agents", "/agents 换一个智能体。", "/agents picks another agent.", Slash | Bound),
        L("hint.blank-toolbar", "下方可先选好智能体和模型。", "Pick the agent and model below first.", Blank),
        L("hint.blank-strength", "下方还能调推理强度。", "Adjust reasoning strength below.", Blank),

        // ---- Session shell hint (gated by the shipped shell capability) ----
        L("hint.shell", "输入 ! 进入终端模式。", "Type ! for shell mode.", OpenCodeComposerSplashCapability.ShellMode),

        // ---- Seasonal (Minecraft “special splashes”): eligible only on that local date ----
        new OpenCodeComposerSplashLine("date.new-year", "新年快乐！第一行代码写什么？", "Happy New Year! What's the first line?") { OnDate = (1, 1) },
        new OpenCodeComposerSplashLine("date.april-fools", "今天的 bug 可能是玩笑。", "Today's bug might be a prank.") { OnDate = (4, 1) },
        new OpenCodeComposerSplashLine("date.programmers-day", "1024 程序员节快乐！", "Happy 1024 Programmers' Day!") { OnDate = (10, 24) },
        new OpenCodeComposerSplashLine("date.halloween", "不给需求就捣蛋！", "Trick or requirements!") { OnDate = (10, 31) },
        new OpenCodeComposerSplashLine("date.christmas", "圣诞快乐！愿构建一次通过。", "Merry Christmas! May builds pass first try.") { OnDate = (12, 25) },
    });

    private readonly Func<int, int> _nextIndex;
    private readonly Func<DateTime> _now;
    private readonly OpenCodeComposerSplashCapability _capabilities;
    private readonly List<string> _recent = new();
    private bool _observed;

    /// <param name="nextIndex">Returns an integer in [0, maxExclusive). Defaults to <see cref="Random.Shared"/>; tests inject a sequence.</param>
    /// <param name="now">Local clock for seasonal lines. Defaults to <see cref="DateTime.Now"/>.</param>
    /// <param name="capabilities">Shipped capabilities; defaults to <see cref="Shipped"/>.</param>
    internal OpenCodeComposerSplash(Func<int, int>? nextIndex = null, Func<DateTime>? now = null,
        OpenCodeComposerSplashCapability capabilities = Shipped)
    {
        _nextIndex = nextIndex ?? Random.Shared.Next;
        _now = now ?? (() => DateTime.Now);
        // Context flags are per observation, never a shipped capability.
        _capabilities = capabilities & ~(Bound | Blank);
        Current = Pick(Context(bound: false, blank: false), OpenCodeWorkingFamily.Unknown);
    }

    /// <summary>The line currently shown. Stable until the next explicit context change.</summary>
    internal OpenCodeComposerSplashLine Current { get; private set; }

    /// <summary>Number of rotations performed (diagnostics/tests).</summary>
    internal int Rotations { get; private set; }

    /// <summary>Lines eligible for <paramref name="available"/> on <paramref name="date"/>.</summary>
    internal static IReadOnlyList<OpenCodeComposerSplashLine> Eligible(OpenCodeComposerSplashCapability available, DateTime date,
        OpenCodeWorkingFamily family = OpenCodeWorkingFamily.Unknown)
        => Library.Where(l => (l.Requires & available) == l.Requires
            && (l.Family == OpenCodeWorkingFamily.Unknown || l.Family == family)
            && (l.OnDate is not { } d || d.Month == date.Month && d.Day == date.Day)).ToArray();

    /// <summary>
    /// Explicit trigger: a conversation slot was opened/switched, or a new blank session became ready. Callers invoke this
    /// ONLY from that activation path — never from rendering, typing, focus or timers. The very first observation keeps
    /// the constructor's line when it is eligible in that context (no double pick on page open). Returns whether the
    /// shown line changed.
    /// </summary>
    internal bool ObserveContext(bool boundSession, bool blankSession, OpenCodeWorkingFamily family = OpenCodeWorkingFamily.Unknown)
    {
        var available = Context(boundSession, blankSession);
        if (!_observed)
        {
            _observed = true;
            // A known family gets its weighted pick on the first activation, not the constructor's unknown-family pick.
            if (family == OpenCodeWorkingFamily.Unknown && (Current.Requires & available) == Current.Requires) return false;
        }
        var next = Pick(available, family);
        var changed = !ReferenceEquals(next, Current);
        Current = next;
        if (changed) Rotations++;
        return changed;
    }

    /// <summary>
    /// Placeholder precedence: a staged command's argument prompt wins; an inactive/unavailable editor shows the neutral
    /// base placeholder; otherwise the splash line.
    /// </summary>
    internal static string Resolve(string? selectedCommand, bool isActive, string splash, string basePlaceholder)
    {
        if (selectedCommand is { } command && isActive)
            return $"/{OpenCodeComposerInputPolicy.Bound(command, 40)} 的参数（可选）";
        return isActive ? splash : basePlaceholder;
    }

    private OpenCodeComposerSplashCapability Context(bool bound, bool blank)
        => _capabilities | (bound ? Bound : C0) | (blank ? Blank : C0);

    private OpenCodeComposerSplashLine Pick(OpenCodeComposerSplashCapability available, OpenCodeWorkingFamily family)
    {
        var eligible = Eligible(available, _now(), family);
        if (eligible.Count == 0) return Library[0];
        var window = Math.Min(RecentWindow, eligible.Count - 1);
        while (_recent.Count > window) _recent.RemoveAt(0);
        var candidates = eligible.Where(l => !_recent.Contains(l.Id)).ToArray();
        if (candidates.Length == 0) candidates = eligible.ToArray();
        if (family != OpenCodeWorkingFamily.Unknown)
        {
            // Reserve one in five activations for the matching family, leaving the generic/hint pool four in five.
            var modelPick = Math.Clamp(_nextIndex(5), 0, 4) == 0;
            var pool = candidates.Where(l => (l.Family != OpenCodeWorkingFamily.Unknown) == modelPick).ToArray();
            if (pool.Length > 0) candidates = pool;
        }
        var index = _nextIndex(candidates.Length);
        var line = candidates[Math.Clamp(index, 0, candidates.Length - 1)];
        _recent.Add(line.Id);
        if (_recent.Count > RecentWindow) _recent.RemoveAt(0);
        return line;
    }
}
