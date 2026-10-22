using System;
using System.Collections.Generic;
using System.Linq;

namespace AIFrontend.Pages.OpenCode;

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
    /// <summary>Ctrl+/ explicitly opens the command catalog.</summary>
    CommandShortcut = 1 << 1,
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
    internal bool IsHint => Requires != OpenCodeComposerSplashCapability.None;
}

internal sealed class OpenCodeComposerSplash
{
    private const OpenCodeComposerSplashCapability C0 = OpenCodeComposerSplashCapability.None;
    private const OpenCodeComposerSplashCapability Slash = OpenCodeComposerSplashCapability.SlashCommands;
    private const OpenCodeComposerSplashCapability CtrlSlash = OpenCodeComposerSplashCapability.CommandShortcut;
    private const OpenCodeComposerSplashCapability Tab = OpenCodeComposerSplashCapability.TabCompletion;
    private const OpenCodeComposerSplashCapability Attach = OpenCodeComposerSplashCapability.Attachments;
    private const OpenCodeComposerSplashCapability Lines = OpenCodeComposerSplashCapability.Multiline;
    private const OpenCodeComposerSplashCapability Nav = OpenCodeComposerSplashCapability.SessionNavigation;
    private const OpenCodeComposerSplashCapability Editor = OpenCodeComposerSplashCapability.LargeEditor;
    private const OpenCodeComposerSplashCapability Bound = OpenCodeComposerSplashCapability.BoundSession;
    private const OpenCodeComposerSplashCapability Blank = OpenCodeComposerSplashCapability.BlankToolbar;
    private const OpenCodeComposerSplashCapability Mention = OpenCodeComposerSplashCapability.FileMention;

    /// <summary>Capabilities this build actually ships (verified in code). Context flags are added per observation.</summary>
    internal const OpenCodeComposerSplashCapability Shipped = Slash | CtrlSlash | Tab | Attach | Lines | Nav | Editor | Mention | OpenCodeComposerSplashCapability.ShellMode;

    /// <summary>How many recent lines a rotation avoids (bounded by the eligible pool size − 1).</summary>
    internal const int RecentWindow = 8;

    private static OpenCodeComposerSplashLine L(string id, string text, string english, OpenCodeComposerSplashCapability requires = C0)
        => new(id, text, english, requires);

    /// <summary>Immutable library. Ids are stable; text is short enough for one placeholder line.</summary>
    internal static readonly IReadOnlyList<OpenCodeComposerSplashLine> Library = Array.AsReadOnly(new[]
    {
        // ---- Playful / generic (always eligible) ----
        L("fun.ideas-queue", "灵感正在排队。", "Ideas are lining up."),
        L("fun.blocks", "把难题拆成小方块。", "Break the big problem into blocks."),
        L("fun.no-hiding-bugs", "今天也让 bug 无处藏身。", "No bug gets to hide today."),
        L("fun.one-sentence", "一句话开工，细节慢慢补。", "Start with a sentence; refine as you go."),
        L("fun.ask-first", "先问，再让代码回答。", "Ask first; let the code answer."),
        L("fun.next-idea", "你的下一个好主意是？", "What's your next good idea?"),
        L("fun.boring-part", "把枯燥的部分交给我。", "Hand me the boring part."),
        L("fun.small-steps", "小步快跑，随时回看。", "Small steps, easy reviews."),
        L("fun.weird-questions", "这里欢迎奇怪的问题。", "Weird questions welcome."),
        L("fun.just-right", "把“差不多”变成“刚刚好”。", "Turn \"almost\" into \"just right\"."),
        L("fun.one-less-todo", "让 TODO 今天少一个。", "One fewer TODO today."),
        L("fun.regex", "来点灵感，或者来点正则。", "Inspiration — or a regex."),
        L("fun.put-it-here", "有个想法？放这儿吧。", "Got an idea? Put it here."),
        L("fun.less-busywork", "今日目标：少一点重复劳动。", "Today's goal: less busywork."),
        L("fun.add-up", "小提交，大进步。", "Small commits add up."),
        L("fun.rubber-duck", "比小黄鸭更会接话。", "Talks back better than a rubber duck."),
        L("fun.hello-world", "Hello, World! 也是从一行开始的。", "Hello, World! started with one line, too."),
        L("fun.forty-two", "答案是 42，问题是什么？", "The answer is 42. What's the question?"),
        L("fun.works-on-my-machine", "“在我电脑上是好的。”——来查查。", "\"Works on my machine.\" Let's check."),
        L("fun.off-by-one", "差一错误，退！退！退！", "Off-by-one errors, begone!"),
        L("fun.naming", "命名是难题之一，我们一起想。", "Naming is hard. Let's think together."),
        L("fun.cache", "缓存失效？先喝口水。", "Cache invalidation? Grab some water first."),
        L("fun.semicolon", "少一个分号也不怕。", "Not afraid of a missing semicolon."),
        L("fun.coffee", "咖啡已就位，问题呢？", "Coffee's ready. And the question?"),
        L("fun.legacy", "旧代码也值得被温柔对待。", "Legacy code deserves kindness, too."),
        L("fun.green-tests", "目标：测试全绿。", "Goal: all tests green."),
        L("fun.edge-cases", "边界条件，我们来了。", "Edge cases, here we come."),
        L("fun.refactor", "每次好重构都始于一个问题。", "Every good refactor starts with a question."),
        L("fun.detective", "代码侦探，随时待命。", "Code detective, standing by."),
        L("fun.blank-page", "空白页最自由。", "A blank page is the freest."),
        L("fun.today", "今天想造点什么？", "What shall we build today?"),
        L("fun.splash", "也试试 Minecraft 吧！", "Also try Minecraft!"),
        L("fun.no-bug-is-small", "没有小 bug，只有没找到的 bug。", "No small bugs, only unfound ones."),
        L("fun.ship-it", "写完、测完、交付。", "Write it, test it, ship it."),
        L("fun.typo", "错别字也能看懂，放心打。", "Typos welcome — just type."),
        L("fun.big-picture", "先看全局，再动细节。", "Big picture first, details next."),
        L("fun.readme", "README 也是代码的一部分。", "The README is code, too."),
        L("fun.night-owl", "夜深了？问题不会等太久。", "Late night? Your question won't wait long."),

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
        L("hint.ctrl-slash", "按 Ctrl+/ 随时打开命令目录。", "Press Ctrl+/ to open the command list.", CtrlSlash),
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

        // ---- Unshipped (kept for later; never shown until the capability is actually shipped; “!” is plan B8) ----
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
        Current = Pick(Context(bound: false, blank: false));
    }

    /// <summary>The line currently shown. Stable until the next explicit context change.</summary>
    internal OpenCodeComposerSplashLine Current { get; private set; }

    /// <summary>Number of rotations performed (diagnostics/tests).</summary>
    internal int Rotations { get; private set; }

    /// <summary>Lines eligible for <paramref name="available"/> on <paramref name="date"/>.</summary>
    internal static IReadOnlyList<OpenCodeComposerSplashLine> Eligible(OpenCodeComposerSplashCapability available, DateTime date)
        => Library.Where(l => (l.Requires & available) == l.Requires
            && (l.OnDate is not { } d || d.Month == date.Month && d.Day == date.Day)).ToArray();

    /// <summary>
    /// Explicit trigger: a conversation slot was opened/switched, or a new blank session became ready. Callers invoke this
    /// ONLY from that activation path — never from rendering, typing, focus or timers. The very first observation keeps
    /// the constructor's line when it is eligible in that context (no double pick on page open). Returns whether the
    /// shown line changed.
    /// </summary>
    internal bool ObserveContext(bool boundSession, bool blankSession)
    {
        var available = Context(boundSession, blankSession);
        if (!_observed)
        {
            _observed = true;
            if ((Current.Requires & available) == Current.Requires) return false;
        }
        var next = Pick(available);
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

    private OpenCodeComposerSplashLine Pick(OpenCodeComposerSplashCapability available)
    {
        var eligible = Eligible(available, _now());
        if (eligible.Count == 0) return Library[0];
        var window = Math.Min(RecentWindow, eligible.Count - 1);
        while (_recent.Count > window) _recent.RemoveAt(0);
        var candidates = eligible.Where(l => !_recent.Contains(l.Id)).ToArray();
        if (candidates.Length == 0) candidates = eligible.ToArray();
        var index = _nextIndex(candidates.Length);
        var line = candidates[Math.Clamp(index, 0, candidates.Length - 1)];
        _recent.Add(line.Id);
        if (_recent.Count > RecentWindow) _recent.RemoveAt(0);
        return line;
    }
}
