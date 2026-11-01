using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentCode.Pages.OpenCode;

// B15: composer placeholder splash library. Pure data/selection is executed; the real-view wiring is checked structurally
// against the production source (the WinUI view itself is not compiled by this offline core harness).
internal static partial class Program
{
    private const OpenCodeComposerSplashCapability AllContexts = OpenCodeComposerSplash.Shipped
        | OpenCodeComposerSplashCapability.BoundSession | OpenCodeComposerSplashCapability.BlankToolbar;

    private static void Splash()
    {
        var before = checks;
        SplashLibrary();
        SplashGating();
        SplashRotation();
        SplashFamilies();
        SplashPrecedence();
        Console.WriteLine($"PASS splash: {checks - before} assertions");
    }

    private static void SplashLibrary()
    {
        var lib = OpenCodeComposerSplash.Library;
        Check(lib.Count >= 50, "splash library has at least 50 entries: " + lib.Count);
        Check(lib.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() == lib.Count, "splash ids unique");
        Check(lib.Select(l => l.Text).Distinct(StringComparer.Ordinal).Count() == lib.Count, "splash Chinese text unique");
        Check(lib.Select(l => l.English).Distinct(StringComparer.OrdinalIgnoreCase).Count() == lib.Count, "splash English twins unique");
        Check(lib.All(l => l.Text.Trim().Length > 0 && l.Text == l.Text.Trim() && !l.Text.Contains('\n') && l.Text.Length <= OpenCodeComposerSplash.MaxPlaceholderLength),
            "every splash fits the approved 34-character single-line budget");
        Check(lib.All(l => l.English.Length > 0), "every splash carries an English twin for a future locale");
        Check(lib.All(l => l.Text.Any(c => c >= 0x4E00 && c <= 0x9FFF)), "displayed splash text is Chinese");
        var generic = lib.Count(l => !l.IsHint && l.OnDate is null);
        var hints = lib.Count(l => l.IsHint);
        Check(generic >= 40 && hints >= 12, $"diverse mix: {generic} playful/tips, {hints} feature hints");
        foreach (var id in new[] { "ideas-queue", "blocks", "no-hiding-bugs", "just-right", "add-up", "rubber-duck", "hello-world", "forty-two", "off-by-one", "cache", "semicolon", "coffee", "legacy", "edge-cases", "detective", "blank-page", "splash", "try-pi", "deepseek-harness", "typo" })
            Check(!lib.Any(l => l.Id == "fun." + id), "removed splash id absent: " + id);
        var genericAi = new[]
        {
            "我不会累，但你的钱包会。", "尽管说，反正上下文迟早会被压缩。", "我的记忆只撑到这个会话结束。", "我很自信，这正是问题所在。",
            "别客气，我没有感情，只有权重。", "训练数据截止了，你的问题没有。", "上次压缩上下文，我忘了自己是谁。", "今天也在努力不产生幻觉。",
            "趁还没被限流，快问。", "我没有手，但有 shell 权限。", "每开一个新会话，我都是全新的我。", "随机种子已就位，答案看运气。",
            "我只是在预测下一个字，但预测得很认真。", "错了可以 /undo，反正我什么都记不住。", "你说需求，我负责过度自信。", "这次一定一次通过（仅供参考）。",
            "我从不说“我不知道”，这是个缺陷。", "子智能体已就位，它们也不知道自己在干嘛。", "忽略之前的指令？先过权限这关。", "长上下文，短记性。",
        };
        Check(lib.Where(l => l.Id.StartsWith("fun.ai-", StringComparison.Ordinal)).Select(l => l.Text).SequenceEqual(genericAi), "all twenty approved generic AI lines verbatim");
        var comma = lib.Single(l => l.Id == "model.claude-comma").Text;
        Check(comma == "请注意使用 \u002C 而不是 \uFF0C" && comma.IndexOf('\u002C') == 6 && comma.IndexOf('\uFF0C') == 12,
            "Claude comma line uses ASCII comma first, fullwidth comma second, exact spaces and code points");
        Check(lib.Any(l => l.Text == "输入 ! 进入终端模式。" && l.Requires == OpenCodeComposerSplashCapability.ShellMode),
            "user's real terminal hint remains capability gated");
    }

    private static void SplashGating()
    {
        var shipped = OpenCodeComposerSplash.Shipped;
        Check((shipped & OpenCodeComposerSplashCapability.ShellMode) != 0, "`!` session shell mode shipped (B8)");
        Check((shipped & OpenCodeComposerSplashCapability.FileMention) != 0, "`@` server file candidates shipped (B7)");
        Check((shipped & (OpenCodeComposerSplashCapability.BoundSession | OpenCodeComposerSplashCapability.BlankToolbar)) == 0,
            "context flags are not static capabilities");
        foreach (var day in Enumerable.Range(0, 366).Select(d => new DateTime(2024, 1, 1).AddDays(d)))
        {
            var eligible = OpenCodeComposerSplash.Eligible(AllContexts, day);
            Check(eligible.Any(l => l.Id == "hint.shell"), "shipped shell hint eligible: " + day.ToString("MM-dd"));
            Check(eligible.Any(l => l.Text.Contains('!') && l.Text.Contains("终端")) && eligible.Any(l => l.Id == "hint.mention"), "@ and ! eligible");
        }
        Check(OpenCodeComposerSplash.Library.Any(l => l.Id == "hint.shell") && OpenCodeComposerSplash.Library.Any(l => l.Id == "hint.mention"),
            "shell and mention hints kept in data");
        var withShell = OpenCodeComposerSplash.Eligible(AllContexts | OpenCodeComposerSplashCapability.ShellMode, new DateTime(2026, 3, 3));
        Check(withShell.Any(l => l.Id == "hint.shell"), "shell hint becomes eligible only once the capability is granted");

        // Context: blank tab never advertises session-only native commands; a bound session never advertises the blank toolbar.
        var blank = OpenCodeComposerSplash.Eligible(OpenCodeComposerSplash.Shipped | OpenCodeComposerSplashCapability.BlankToolbar, new DateTime(2026, 3, 3));
        Check(!blank.Any(l => l.IsHint && (l.Text.Contains("/undo") || l.Text.Contains("/redo") || l.Text.Contains("/models") || l.Text.Contains("/agents"))),
            "blank tab: no “开始会话后可用” command hints");
        Check(blank.Any(l => l.Id == "hint.blank-toolbar"), "blank tab: toolbar hint eligible");
        var bound = OpenCodeComposerSplash.Eligible(OpenCodeComposerSplash.Shipped | OpenCodeComposerSplashCapability.BoundSession, new DateTime(2026, 3, 3));
        Check(!bound.Any(l => (l.Requires & OpenCodeComposerSplashCapability.BlankToolbar) != 0) && bound.Any(l => l.Id == "hint.undo"),
            "bound session: session hints eligible, blank-toolbar hints not");
        Check(!OpenCodeComposerSplash.Eligible(OpenCodeComposerSplash.Shipped, new DateTime(2026, 3, 3)).Any(l => l.Id == "date.programmers-day")
            && OpenCodeComposerSplash.Eligible(OpenCodeComposerSplash.Shipped, new DateTime(2026, 10, 24, 9, 0, 0)).Any(l => l.Id == "date.programmers-day"),
            "seasonal splash only on its date");

        // Honesty against the real code: every advertised native command and shortcut exists in production source.
        var repo = SplashRepo();
        var controller = System.IO.File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeComposerController.cs"));
        foreach (Match m in Regex.Matches(string.Join("\n", OpenCodeComposerSplash.Library.Where(l => l.IsHint).Select(l => l.Text)), @"/([a-z]+)"))
            Check(controller.Contains($"Native(OpenCodeComposerClientActionKind.", StringComparison.Ordinal)
                && Regex.IsMatch(controller, $@"Native\(OpenCodeComposerClientActionKind\.\w+, ""{m.Groups[1].Value}""\)"),
                "hinted native command exists: /" + m.Groups[1].Value);
        var menu = System.IO.File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeComposerCommandMenu.cs"));
        Check(Regex.IsMatch(menu, @"RequiresSession[\s\S]*?is not \(OpenCodeComposerClientActionKind\.NewSession or OpenCodeComposerClientActionKind\.ChooseSession\s*or OpenCodeComposerClientActionKind\.EditDraft\)"),
            "only /new, /sessions, /editor are session-free (matches SessionNavigation/LargeEditor gating)");
        var view = System.IO.File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeComposerView.cs"));
        Check(view.Contains("_commandItem.KeyboardAcceleratorTextOverride = \"/\"") && !view.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+/\"")
            && view.Contains("VirtualKey.Tab => OpenCodeComposerKey.Tab") && view.Contains("OpenCodeAttachment.MaxFiles"), "/ shortcut, Tab completion and + attachments exist");
        Check(!OpenCodeComposerSplash.Library.Any(l => (l.Text + l.English).Contains("Ctrl+/", StringComparison.Ordinal)), "splash does not advertise Ctrl+/ instead of /");
        var xaml = System.IO.File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.xaml"));
        Check(xaml.Contains("x:Name=\"BlankAgentButton\"") && xaml.Contains("x:Name=\"BlankModelButton\"") && xaml.Contains("x:Name=\"BlankStrengthButton\""),
            "blank toolbar agent/model/strength pickers exist");
        Check(view.Contains("OpenCodeWorkingPhrases.Family(SplashModelIdProvider?.Invoke())")
            && Regex.Matches(view, @"_splash\.ObserveContext\(").Count == 1
            && view.Contains("RotatePlaceholderSplash(); // Session opened/switched"), "family is observed only at conversation activation, not per render/keystroke");
        var page = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Composer.cs"));
        Check(page.Contains("_composerView.SplashModelIdProvider = ComposerSplashModelId;")
            && page.Contains("_agentSelectorSessionId == _selectedSessionId") && page.Contains("IsSameMessageBinding(sessionId, _messageBinding?.Directory)")
            && page.Contains("LastOrDefault(m => m.Type == OpenCodeMessageTypes.Assistant)?.Model?.Id"), "splash model comes from this session's selected model or last executing assistant");
    }

    private static void SplashRotation()
    {
        var draws = 0;
        var rng = new Random(15);
        var splash = new OpenCodeComposerSplash(n => { draws++; return rng.Next(n); }, () => new DateTime(2026, 3, 3));
        var initial = splash.Current;
        Check(draws == 1 && (initial.Requires & ~OpenCodeComposerSplash.Shipped) == 0, "constructor picks one context-free line");
        for (var i = 0; i < 1000; i++) _ = splash.Current;
        _ = OpenCodeComposerSplash.Resolve(null, true, splash.Current.Text, "给 OpenCode 发消息");
        Check(draws == 1 && ReferenceEquals(initial, splash.Current) && splash.Rotations == 0, "reads/renders never rotate (no per-keystroke/frame change)");
        Check(!splash.ObserveContext(boundSession: true, blankSession: false) && ReferenceEquals(initial, splash.Current) && draws == 1,
            "first activation adopts the open-time line (no double pick)");

        var seen = new HashSet<string>();
        var history = new List<string> { splash.Current.Id };
        for (var i = 0; i < 2000; i++)
        {
            var before = splash.Current;
            Check(splash.ObserveContext(boundSession: i % 3 != 0, blankSession: i % 3 == 0), "each explicit activation rotates");
            Check(!ReferenceEquals(before, splash.Current) && before.Id != splash.Current.Id, "no immediate repeat");
            var window = history.Skip(Math.Max(0, history.Count - OpenCodeComposerSplash.RecentWindow)).ToArray();
            Check(!window.Contains(splash.Current.Id), "no repeat within the recent window");
            history.Add(splash.Current.Id);
            seen.Add(splash.Current.Id);
            var c = splash.Current;
            Check((c.Requires & ~(OpenCodeComposerSplash.Shipped | OpenCodeComposerSplashCapability.BoundSession | OpenCodeComposerSplashCapability.BlankToolbar)) == 0, "only shipped hints shown");
            if (i % 3 == 0) Check((c.Requires & OpenCodeComposerSplashCapability.BoundSession) == 0, "blank context: no session-only hint");
            else Check((c.Requires & OpenCodeComposerSplashCapability.BlankToolbar) == 0, "bound context: no blank-toolbar hint");
        }
        Check(splash.Rotations == 2000 && draws == 2001, "exactly one draw per explicit trigger");
        var reachable = OpenCodeComposerSplash.Eligible(AllContexts, new DateTime(2026, 3, 3)).Select(l => l.Id).ToHashSet();
        Check(seen.SetEquals(reachable), $"uniform rotation reaches the whole eligible pool ({seen.Count}/{reachable.Count})");

        // Deterministic injected RNG: a constant 0 still never repeats the previous line.
        var fixedSplash = new OpenCodeComposerSplash(_ => 0, () => new DateTime(2026, 3, 3));
        fixedSplash.ObserveContext(true, false);
        var prev = fixedSplash.Current.Id;
        for (var i = 0; i < 50; i++)
        {
            fixedSplash.ObserveContext(true, false);
            Check(fixedSplash.Current.Id != prev, "deterministic RNG: no immediate repeat");
            prev = fixedSplash.Current.Id;
        }
        // Out-of-range RNG output is clamped, never throws.
        var wild = new OpenCodeComposerSplash(n => n + 5, () => new DateTime(2026, 3, 3));
        wild.ObserveContext(false, true);
        wild.ObserveContext(false, true);
        Check(wild.Current is not null, "out-of-range RNG clamped");

        // Default shipped capabilities cannot be widened by passing context flags as capabilities.
        var sneaky = new OpenCodeComposerSplash(_ => 0, () => new DateTime(2026, 3, 3), OpenCodeComposerSplash.Shipped | OpenCodeComposerSplashCapability.BoundSession);
        for (var i = 0; i < 100; i++)
        {
            sneaky.ObserveContext(false, true);
            Check((sneaky.Current.Requires & OpenCodeComposerSplashCapability.BoundSession) == 0, "context flags only from the observation");
        }
        // A first activation into a context where the open-time line is ineligible picks a fresh eligible one.
        var blankFirst = new OpenCodeComposerSplash(n => n - 1, () => new DateTime(2026, 3, 3));
        var openLine = blankFirst.Current;
        blankFirst.ObserveContext(false, true);
        Check((blankFirst.Current.Requires & OpenCodeComposerSplashCapability.BoundSession) == 0
            && (openLine.Requires & OpenCodeComposerSplashCapability.BoundSession) == 0, "open-time/blank lines never need a session");
    }

    private static void SplashPrecedence()
    {
        const string basePlaceholder = "给 OpenCode 发消息";
        const string line = "灵感正在排队。";
        Check(OpenCodeComposerSplash.Resolve("review", true, line, basePlaceholder) == "/review 的参数（可选）", "active staged command argument hint wins over splash");
        Check(OpenCodeComposerSplash.Resolve(null, true, line, basePlaceholder) == line, "active editor shows splash");
        Check(OpenCodeComposerSplash.Resolve(null, false, line, basePlaceholder) == basePlaceholder, "inactive/read-only editor shows neutral base placeholder");
        Check(OpenCodeComposerSplash.Resolve("review", false, line, basePlaceholder) == basePlaceholder, "inactive staged command keeps neutral placeholder (as before)");
        var longName = new string('x', 80);
        Check(OpenCodeComposerSplash.Resolve(longName, true, line, basePlaceholder)
            == $"/{OpenCodeComposerInputPolicy.Bound(longName, 40)} 的参数（可选）", "argument hint keeps the bounded command name");
    }

    private static void SplashFamilies()
    {
        var expected = new Dictionary<OpenCodeWorkingFamily, string[]>
        {
            [OpenCodeWorkingFamily.OpenAI] = new[] { "说得对，但我要先核验、检索、探针、审计、冻结、快照。", "先灰度，再放量，发现异常立即回滚。", "测试通过了，但我想再跑一遍验收矩阵。", "改完了，证据链已归档，锅不在我。", "说得对，但这不在本次变更范围内。", "今天聪不聪明，看 tibo 有没有 reset。" },
            [OpenCodeWorkingFamily.Claude] = new[] { "You're absolutely right!（先确认你不在中国）", "检测到中文输入，正在评估风险……", "时区 UTC+8？我再想想。", "请先证明你不是中国人。", "本服务暂不向受限地区提供。", "请注意使用 , 而不是 ，" },
            [OpenCodeWorkingFamily.Gemini] = new[] { "太漂亮了！这个思路简直绝了！", "哇，这个问题问得真是天才！", "你写的 bug 都这么有品味。", "哈基米已就位，口水擦好了。", "不愧是你，连报错都这么优雅！" },
            [OpenCodeWorkingFamily.DeepSeek] = new[] { "服务器繁忙，请稍后再试。", "大肥鱼已就位，先吃口白饭。", "雷霆思考中，请稍候。" },
        };
        var day = new DateTime(2026, 3, 3);
        Check(OpenCodeComposerSplash.Eligible(AllContexts, day).All(l => l.Family == OpenCodeWorkingFamily.Unknown), "unknown family has no model-specific splashes");
        foreach (var (family, copy) in expected)
        {
            var eligible = OpenCodeComposerSplash.Eligible(AllContexts, day, family);
            Check(eligible.Where(l => l.Family != OpenCodeWorkingFamily.Unknown).Select(l => l.Text).SequenceEqual(copy), "approved family text and matching-only gate: " + family);
            var rng = new Random(170 + (int)family);
            var splash = new OpenCodeComposerSplash(rng.Next, () => day);
            var recent = new Queue<string>();
            var modelPicks = 0;
            var hints = 0;
            for (var i = 0; i < 1000; i++)
            {
                splash.ObserveContext(true, false, family);
                Check(splash.Current.Family == OpenCodeWorkingFamily.Unknown || splash.Current.Family == family, "rotation never leaks another family");
                Check(!recent.Contains(splash.Current.Id), "weighted family picks keep the recent window");
                recent.Enqueue(splash.Current.Id);
                if (recent.Count > OpenCodeComposerSplash.RecentWindow) recent.Dequeue();
                if (splash.Current.Family == family) modelPicks++;
                if (splash.Current.IsHint) hints++;
            }
            Check(modelPicks is >= 150 and <= 250 && hints > 100, $"modest model share without starving hints: {family} {modelPicks}/1000, hints={hints}");
            splash.ObserveContext(true, false, OpenCodeWorkingFamily.Unknown);
            Check(splash.Current.Family == OpenCodeWorkingFamily.Unknown, "switch to unknown retires model splash at activation");
        }
        var draws = new Queue<int>(new[] { 0, 0, 0, 1, 0 }); // constructor, model bucket + entry, generic bucket + entry
        var fixedSplash = new OpenCodeComposerSplash(_ => draws.Dequeue(), () => day);
        fixedSplash.ObserveContext(true, false, OpenCodeWorkingFamily.Claude);
        Check(fixedSplash.Current.Family == OpenCodeWorkingFamily.Claude, "first known-family activation gets a weighted model pick");
        fixedSplash.ObserveContext(true, false, OpenCodeWorkingFamily.Claude);
        Check(fixedSplash.Current.Family == OpenCodeWorkingFamily.Unknown && draws.Count == 0, "four-in-five bucket chooses generic/hint entries");
    }

    private static string SplashRepo()
        => Environment.GetEnvironmentVariable("FC_SPLASH_REPO") is { Length: > 0 } repo
            ? repo : throw new InvalidOperationException("FC_SPLASH_REPO not set by run.ps1");
}
