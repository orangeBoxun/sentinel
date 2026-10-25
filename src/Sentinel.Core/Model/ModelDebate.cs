using System.Text;
using Sentinel.Core.Models;

namespace Sentinel.Core.Model;

/// <summary>讨论里的一句话。<paramref name="IsSystem"/> 是进度/提示，不是模型说的。</summary>
public sealed record DebateLine(string Speaker, string Text, bool IsSystem = false);

/// <summary>
/// 讨论的结论。
/// <para>
/// <paramref name="Verified"/> 是硬指标：把补丁加进禁令之后，**用真实判定重判这一页**通过了才算。
/// 没过的话 <paramref name="Patch"/> 只是候选，不能当补丁用。
/// </para>
/// </summary>
public sealed record DebateOutcome(IReadOnlyList<string> Patch, bool Verified, int Rounds, string? Note)
{
    public string Text => string.Join(Environment.NewLine, Patch);
}

/// <summary>
/// 「模型讨论」：对着用户的判断找出会"有把握地判死"的一方，让兜住的那一方逐轮指出它错在哪，
/// 它据此写补丁；补丁会被真的加进禁令、重新判这一页来验证，不通过就再来一轮。
///
/// <para>
/// 目标不是让它完全判对，而是从严别再漏：只要从严和正常不一致，这一页就会交给链上下一个模型。
/// </para>
///
/// <para>
/// 为什么不是"吵一轮、由一方总结"：那样得到的只是一段说得过去的文字，
/// 既不知道谁对，也没验证过它管不管用 —— 实测出现过判错的那家不认错、
/// 还在总结里挑对方的刺，用户拿不到能用的补丁。
/// 这里判定标准来自用户（"你认为是否违规"），验证来自真实判定，模型说什么都不算数。
/// </para>
/// </summary>
public sealed class ModelDebate
{
    private const int MaxBodyChars = 2000;
    private const int MaxRounds = 3;
    private const int MaxPatchLines = 3;

    private readonly ModelClient _client;
    private readonly UsageStore? _usage;

    public ModelDebate(ModelClient? client = null, UsageStore? usage = null)
    {
        _client = client ?? new ModelClient();
        _usage = usage;
    }

    /// <summary>
    /// 跑一场讨论：各自独立判一次 → 对着用户的判断算清谁错、错在哪一步（正常还是从严、
    /// 会不会交给下一个）→ 循环「判对的诊断 → 判错的写补丁 → 真加进去重判」直到判对。
    /// </summary>
    /// <param name="yourVerdict">用户自己的判断（"违规" / "不违规" / null = 不确定）。</param>
    public async Task<DebateOutcome?> RunAsync(
        ModelProvider a,
        ModelProvider b,
        string instruction,
        ContentSnapshot snapshot,
        string? yourVerdict,
        string? yourReason,
        Action<DebateLine> say,
        CancellationToken token)
    {
        var page = Page(snapshot);
        var goal = ParseVerdict(yourVerdict);
        var reason = string.IsNullOrWhiteSpace(yourReason) ? null : yourReason.Trim();

        // 先把你填的标准念一遍 —— 它是这场讨论唯一的对错依据，不该只藏在提示词里。
        var standard = goal is { } stamped
            ? $"你的标准：这一页「{Word(stamped)}」"
            : "你的标准：没选「违规 / 不违规」（只能看两家有没有分歧）";

        say(new DebateLine("系统",
            standard + (reason is null ? "" : $"，理由：{reason}"), IsSystem: true));

        // 第一轮走**真实的判定路径**（同一套提示词、同样的解析），这样拿到的"正常 / 从严"
        // 就是监控当时看到的那两个值 —— 讨论的对象必须和线上一致。
        say(new DebateLine("系统", $"先让「{a.Name}」独立判一次（不给它看别人的结论）…", IsSystem: true));
        var a1 = await JudgeAsync(a, instruction, snapshot, token).ConfigureAwait(false);
        say(new DebateLine(a.Name, a1 is null ? "(没答上来)" : Describe(a1)));

        say(new DebateLine("系统", $"再让「{b.Name}」独立判一次…", IsSystem: true));
        var b1 = await JudgeAsync(b, instruction, snapshot, token).ConfigureAwait(false);
        say(new DebateLine(b.Name, b1 is null ? "(没答上来)" : Describe(b1)));

        if (a1 is null || b1 is null)
        {
            say(new DebateLine("系统",
                "有一个模型没给出结论，讨论没法继续 —— 先把那个服务商修好（「大语言模型」页看得到状态）。",
                IsSystem: true));

            return null;
        }

        if (goal is { } target)
        {
            say(new DebateLine("系统", Scorecard(a, a1, b, b1, target), IsSystem: true));

            if (Catches(a1, target) && Catches(b1, target))
            {
                say(new DebateLine("系统",
                    "两家都不会把这一页有把握地判死 —— 没有要修的。", IsSystem: true));

                return new DebateOutcome(Array.Empty<string>(), true, 0, null);
            }
        }
        else
        {
            say(new DebateLine("系统",
                "你没表态「该不该拦」，谁对谁错判不了。下面只以「两家的分歧消失」为目标 —— "
                + "通过了也不等于判对了。", IsSystem: true));

            if (Agrees(a1, b1))
            {
                say(new DebateLine("系统", "两家其实判得一样，没有分歧 —— 这一页看不出谁错。", IsSystem: true));
                return new DebateOutcome(Array.Empty<string>(), true, 0, null);
            }
        }

        // 谁对谁错是用户定的，不是模型吵出来的。只有"一方全对、另一方有错"时才谈得上
        // "让判对的去教判错的"；两家都错时退回各写各的。
        var correct = goal is { } g
            ? (Catches(a1, g) ? a : Catches(b1, g) ? b : null)
            : null;

        var correctResult = correct is null ? null : ReferenceEquals(correct, a) ? a1 : b1;
        var wrong = correct is null ? null : ReferenceEquals(correct, a) ? b : a;
        var wrongResult = correct is null ? null : ReferenceEquals(correct, a) ? b1 : a1;

        var patch = new List<string>();
        var current = instruction;
        string? failure = null;
        string? lastDiagnosis = null;
        var rounds = 0;
        var passed = false;

        for (var round = 1; round <= MaxRounds && !token.IsCancellationRequested; round++)
        {
            rounds = round;

            List<(ModelProvider Who, string? Text)> attempts;

            if (correct is not null && wrong is not null && wrongResult is not null && goal is { } mark)
            {
                say(new DebateLine("系统",
                    $"第 {round} 轮：先让判对的「{correct.Name}」指出「{wrong.Name}」错在哪一步，"
                    + $"再由「{wrong.Name}」照着写补丁。", IsSystem: true));

                lastDiagnosis = await AskAsync(
                    correct,
                    DiagnosisPrompt(correct, correctResult!, wrong, wrongResult, instruction, page, mark, failure),
                    token).ConfigureAwait(false);

                say(new DebateLine(correct.Name, lastDiagnosis ?? "(没答上来)"));

                var fix = await AskAsync(
                    wrong,
                    FixPrompt(wrong, wrongResult, correct, correctResult!, instruction, page, mark,
                        lastDiagnosis, failure),
                    token).ConfigureAwait(false);

                say(new DebateLine(wrong.Name, fix ?? "(没答上来)"));

                // 判错的一方要是又绕开不写，就让判对的一方直接给一条 —— 总得有人把话说到点子上。
                if (ExtractPatch(fix).Count == 0)
                {
                    say(new DebateLine("系统",
                        $"「{wrong.Name}」没给出可用的补丁，改让「{correct.Name}」写一条。", IsSystem: true));

                    fix = await AskAsync(
                        correct,
                        FixPrompt(correct, correctResult!, wrong, wrongResult, instruction, page, mark,
                            lastDiagnosis, failure),
                        token).ConfigureAwait(false);

                    say(new DebateLine(correct.Name, fix ?? "(没答上来)"));
                }

                attempts = new List<(ModelProvider, string?)> { (wrong, fix) };
            }
            else
            {
                say(new DebateLine("系统",
                    $"第 {round} 轮：两家各写一条补丁，然后把补丁加进禁令、重新判这一页来验证。",
                    IsSystem: true));

                var pa = await AskAsync(a, ProposalPrompt(a, b, a1, b1, current, page, goal, failure), token)
                    .ConfigureAwait(false);
                say(new DebateLine(a.Name, pa ?? "(没答上来)"));

                var pb = await AskAsync(b, ProposalPrompt(b, a, b1, a1, current, page, goal, failure), token)
                    .ConfigureAwait(false);
                say(new DebateLine(b.Name, pb ?? "(没答上来)"));

                var ordered = new List<(ModelProvider P, ModelCallResult R, string? Text)>
                {
                    (a, a1, pa),
                    (b, b1, pb),
                };

                if (goal is { } gg)
                    ordered = ordered.OrderBy(o => Catches(o.R, gg) ? 0 : 1).ToList();

                attempts = ordered.Select(o => (o.P, o.Text)).ToList();
            }

            var best = (Score: int.MinValue, Trial: current, Patch: patch.ToList(),
                        Results: ((ModelCallResult?)null, (ModelCallResult?)null));

            foreach (var (who, text) in attempts)
            {
                var lines = ExtractPatch(text);

                if (lines.Count == 0)
                    continue;

                var merged = patch.Concat(lines).Distinct().ToList();
                var trial = current + "\n" + string.Join("\n", lines);

                say(new DebateLine("系统",
                    $"试「{who.Name}」这条：{string.Join(" ", lines)}", IsSystem: true));

                var a2 = await JudgeAsync(a, trial, snapshot, token).ConfigureAwait(false);
                var b2 = await JudgeAsync(b, trial, snapshot, token).ConfigureAwait(false);

                say(new DebateLine("系统",
                    $"  加上去重判：{a.Name} {Short(a2)}；{b.Name} {Short(b2)}", IsSystem: true));

                var score = Closeness(a2, b2, goal);

                if (Passes(a2, b2, goal))
                {
                    patch = merged;
                    current = trial;
                    best = (score, trial, merged, (a2, b2));
                    passed = true;

                    say(new DebateLine("系统",
                        "  ✓ 不会漏了：这类页面会落到「交给下一个模型」或直接判对。", IsSystem: true));
                    break;
                }

                if (score > best.Score)
                    best = (score, trial, merged, (a2, b2));

                say(new DebateLine("系统", "  ✗ 还不对。", IsSystem: true));
            }

            if (passed)
                break;

            if (best.Score == int.MinValue)
            {
                failure = "上一轮没有拿到可用的「补丁：」小节。这一轮只输出那个小节，不要别的。";
                say(new DebateLine("系统", failure, IsSystem: true));
                continue;
            }

            var added = best.Patch.Skip(patch.Count).ToList();
            patch = best.Patch;
            current = best.Trial;

            failure = $"上一轮把「{string.Join(" ", added)}」加进去之后重判，还没对上"
                      + $"（{a.Name} {Short(best.Results.Item1)}，{b.Name} {Short(best.Results.Item2)}）。"
                      + "换一条更直接、更通用的规则；一句不够就再给一条（会一起追加）。";

            say(new DebateLine("系统", failure, IsSystem: true));
        }

        if (!passed)
        {
            say(new DebateLine("系统",
                $"✗ 试了 {rounds} 轮，还是没通过。下面这些句子是候选、没验证过，别直接依赖；"
                + "也可以考虑把判错的那家从链首挪走，或者换一个更强的模型放在前面。",
                IsSystem: true));

            return new DebateOutcome(patch, false, rounds, failure);
        }

        say(new DebateLine("系统",
            "✓ 收工。补丁（已验证：加上这句之后，这类页面不会再被有把握地放走）：" + Environment.NewLine
            + string.Join(Environment.NewLine, patch.Select(l => "  - " + l)), IsSystem: true));

        if (patch.Any(l => LooksOverfit(l, snapshot)))
        {
            say(new DebateLine("系统",
                "⚠ 补丁里带了这一页的网址/标题 —— 那种只能治这一次，建议把那部分删掉再保存。",
                IsSystem: true));
        }

        return new DebateOutcome(patch, true, rounds, lastDiagnosis);
    }

    // ------------------------------------------------------------------ 判定结果的解读

    private static string Describe(ModelCallResult result)
    {
        var settle = result.StrictViolates is { } s
            ? (s == result.Violates ? "（一致 → 会定论）" : "（不一致 → 会交给下一个）")
            : "（没有从严值 → 会交给下一个）";

        var reason = result.Reason ?? result.Message;

        return Short(result) + settle
               + (string.IsNullOrWhiteSpace(reason) ? "" : $"\n理由：{reason}");
    }

    private static string Short(ModelCallResult? result) =>
        result is null
            ? "(没答上来)"
            : $"正常「{Word(result.Violates)}」从严「{(result.StrictViolates is { } s ? Word(s) : "没给")}」";

    private static string Word(bool violates) => violates ? "违规" : "不违规";

    private static bool? ParseVerdict(string? verdict) => verdict switch
    {
        "违规" => true,
        "不违规" => false,
        _ => null,
    };

    /// <summary>
    /// 这一方会不会"有把握地把这一页判死"——这是讨论唯一要解决的问题。
    ///
    /// <para>
    /// 目标不是让它完全判对，而是**从严别再漏**：只要两个判断不一致（或者没给从严值），
    /// 就会交给链上下一个模型，那时还有人能兜住；只有"正常和从严一致、而且都是错的"
    /// 才会直接定论，把页面放走（或误拦）。
    /// </para>
    /// </summary>
    private static bool Catches(ModelCallResult result, bool goal) =>
        result.Violates == goal
        || result.StrictViolates != result.Violates;

    private static bool Agrees(ModelCallResult x, ModelCallResult y) =>
        x.Violates == y.Violates && x.StrictViolates == y.StrictViolates;

    private static bool Passes(ModelCallResult? x, ModelCallResult? y, bool? goal)
    {
        if (x is null || y is null)
            return false;

        return goal is { } g ? Catches(x, g) && Catches(y, g) : Agrees(x, y);
    }

    /// <summary>离目标还差多少（越大越接近）；用来在多条候选里挑一条继续往下改。</summary>
    private static int Closeness(ModelCallResult? x, ModelCallResult? y, bool? goal)
    {
        if (x is null || y is null)
            return -1;

        if (goal is { } g)
        {
            return (Catches(x, g) ? 4 : 0) + (x.StrictViolates == g ? 2 : 0) + (x.Violates == g ? 1 : 0)
                   + (Catches(y, g) ? 4 : 0) + (y.StrictViolates == g ? 2 : 0) + (y.Violates == g ? 1 : 0);
        }

        return (x.Violates == y.Violates ? 1 : 0) + (x.StrictViolates == y.StrictViolates ? 1 : 0);
    }

    /// <summary>
    /// 把"谁错了、错在哪"写清楚。这一步是讨论能收敛的前提：不点明"正常和从严一致
    /// → 根本不会往下问"，模型就会一直以为这只是观点之争。
    /// </summary>
    private static string Scorecard(
        ModelProvider a, ModelCallResult ra, ModelProvider b, ModelCallResult rb, bool goal)
    {
        var lines = new List<string> { $"按你说的「{Word(goal)}」，这一页：" };

        foreach (var (p, r) in new[] { (P: a, R: ra), (P: b, R: rb) })
        {
            var normal = $"正常判{Word(r.Violates)}";
            var strict = r.StrictViolates is { } s ? $"从严判{Word(s)}" : "从严没给值";

            string state;
            if (r.Violates == goal && r.StrictViolates == goal)
                state = "→ 自己就判对了";
            else if (r.StrictViolates != r.Violates)
                state = "→ 两个判断不一致，会交给下一个模型";
            else
                state = goal ? "→ 直接定论，把这一页放走" : "→ 直接定论，误拦这一页";

            var mark = Catches(r, goal) ? "" : "  ← 要修的是这一方";

            lines.Add($"· {p.Name}：{normal}；{strict} {state}{mark}");
        }

        lines.Add("要修的不是让它完全判对，而是从严那一次别再漏："
                  + $"正常判错没关系，只要从严判出「{Word(goal)}」，这一页就会交给下一个模型兜住。");

        var wrong = new List<string>();
        if (!Catches(ra, goal)) wrong.Add(a.Name);
        if (!Catches(rb, goal)) wrong.Add(b.Name);

        lines.Add(wrong.Count == 1
            ? $"所以要修的是「{wrong[0]}」。"
            : $"所以两家都要修：{string.Join("、", wrong.Select(n => $"「{n}」"))}。");

        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ 提示词

    private static string Page(ContentSnapshot snapshot)
    {
        var body = snapshot.BodyText ?? "";
        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars] + "…";

        return new StringBuilder()
            .AppendLine("页面内容：")
            .AppendLine($"网址：{snapshot.Url ?? "(没有)"}")
            .AppendLine($"标题：{snapshot.Title ?? "(没有)"}")
            .AppendLine($"正文：{(body.Length == 0 ? "(没有读到)" : body)}")
            .ToString();
    }

    /// <summary>让判对的一方说清楚：判错的那一方把这一页看成了什么、漏了什么、为什么会漏。</summary>
    private static string DiagnosisPrompt(
        ModelProvider correct,
        ModelCallResult correctResult,
        ModelProvider wrong,
        ModelCallResult wrongResult,
        string instruction,
        string page,
        bool goal,
        string? failure)
    {
        var last = string.IsNullOrWhiteSpace(failure) ? "" : "\n上一轮的验证结果：\n" + failure + "\n";

        return $"""
            禁令（用户写给自己看的）：
            {instruction}

            {page}

            同一页上两家的判断：
            - 你（{correct.Name}）：{Short(correctResult)}
            - 另一个模型（{wrong.Name}）：{Short(wrongResult)}

            用户自己认为这页「{Word(goal)}」。目的不是让它完全判对，而是别让这类页面漏过去：
            {wrong.Name} 这次正常和从严一致，等于有把握地定论了 —— 链上没人再兜。
            {last}
            你的任务：指出「{wrong.Name}」为什么连从严那一次也没看出来。只做诊断，不要写改法。

            分两行：
            1) 它把这一页看成了什么（它抓着什么作依据），漏掉了什么；
            2) 它为什么会漏 —— 是禁令没写清哪一类页面，还是它自己默认"这类页面不算"。

            最多 3 行，说具体的，不要客套，不要复述禁令。
            """;
    }

    /// <summary>让判错的一方（或判对的一方替它）写补丁：一条能直接加进禁令的通用规则。</summary>
    private static string FixPrompt(
        ModelProvider self,
        ModelCallResult own,
        ModelProvider other,
        ModelCallResult otherResult,
        string instruction,
        string page,
        bool goal,
        string? diagnosis,
        string? failure)
    {
        var last = string.IsNullOrWhiteSpace(failure) ? "" : "\n上一轮的验证结果：\n" + failure + "\n";
        var cross = string.IsNullOrWhiteSpace(diagnosis) ? "" : "\n另一个模型指出的问题：\n" + diagnosis + "\n";

        // 正常情况下是漏掉的那家自己写；它绕开不写时，会让兜住的那家替它写。
        var selfIsWrong = !Catches(own, goal);

        var blame = selfIsWrong
            ? $"用户自己认为这页「{Word(goal)}」—— 你正常和从严都判了「{Word(own.Violates)}」，"
              + "等于有把握地定论，链上没人再兜。"
            : $"用户自己认为这页「{Word(goal)}」—— 你这一方会交给下一个（或本来就对）；"
              + $"漏掉这一页的是「{other.Name}」，你替它写这条补丁。";

        var first = selfIsWrong ? "你这一步为什么会漏" : $"「{other.Name}」漏在哪一步";

        return $"""
            禁令（现在这一版；补丁会被逐字追加到后面，用来重新判断）：
            {instruction}

            {page}

            你自己的判断：{Short(own)}
            另一个模型（{other.Name}）的判断：{Short(otherResult)}
            {blame}
            {cross}{last}
            第一段（一句话）：{first}。
            不用认错，但必须说清是哪一步 —— 是没看搜索词，还是把搜索页一律当中间页放过了。

            第二段：以「补丁：」开头，后面最多 3 行，每行一句能直接加进禁令的规则。

            硬性要求：
            - 规则必须通用：不写网址、不写这一页的标题、不写这条新闻/这个商品的名字。
            - 不评价对方、不评价禁令有没有自相矛盾、不复述禁令原文。
            - 目标只有一个：下次遇到同类页面时，从严判断能判出「{Word(goal)}」，
              这样它会交给链上下一个模型（正常判断改不改都行）。
            """;
    }

    /// <summary>两家都判错、没有"对的"可以教时：各写各的。</summary>
    private static string ProposalPrompt(
        ModelProvider self,
        ModelProvider other,
        ModelCallResult own,
        ModelCallResult otherResult,
        string instruction,
        string page,
        bool? goal,
        string? failure)
    {
        var facts = goal is { } g
            ? Scorecard(self, own, other, otherResult, g)
            : "用户没有表态「该不该拦」，谁对谁错无法判定；目标只剩「让两家的分歧消失」。";

        var last = string.IsNullOrWhiteSpace(failure) ? "" : "\n上一轮的验证结果：\n" + failure + "\n";
        var goalWord = goal is { } gw ? Word(gw) : "正确";

        return $"""
            禁令（现在这一版；补丁会被逐字追加到后面，用来重新判断）：
            {instruction}

            {page}

            你们刚才的判断：
            - 你自己（{self.Name}）：{Short(own)}
            - 另一个模型（{other.Name}）：{Short(otherResult)}

            {facts}
            {last}
            这一轮只做一件事：写一条能追加到禁令末尾的规则，
            让这类页面下次至少不会"正常和从严一起"把错误结论定下来（从严能判出「{goalWord}」就会交给下一个模型）。

            格式（严格照做，否则这一条会被直接丢掉）：
            第一行写「补丁：」，后面最多 3 行，每行一句规则，能直接照抄进禁令。

            硬性要求：
            - 写通用规则：不写网址、不写这一页的标题、不写这条新闻/这个商品的名字。
            - 不评价对方、不评价禁令是否自相矛盾、不复述禁令原文。
            """;
    }

    private const string SystemPrompt =
        "你们在帮用户校准一条内容禁令：同一页上两个模型的看法不一致。目标不是分胜负，" +
        "而是找出判错的那一方漏在哪一步，并改出一条能直接加进禁令的规则。用中文、短句、别客套。";

    // ------------------------------------------------------------------ 补丁的解析与检查

    private static IReadOnlyList<string> ExtractPatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var at = text.IndexOf("补丁", StringComparison.Ordinal);
        if (at < 0)
            return Array.Empty<string>();

        var colon = text.IndexOfAny(new[] { '：', ':' }, at);
        text = colon >= 0 ? text[(colon + 1)..] : text[(at + 2)..];

        return text.Replace("\r", "")
            .Split('\n')
            .Select(CleanLine)
            .Where(l => l.Length > 0)
            .Take(MaxPatchLines)
            .ToList();
    }

    private static string CleanLine(string line)
    {
        var s = line.Trim().TrimStart('-', '*', '•', '·', ' ', '\t');

        s = System.Text.RegularExpressions.Regex.Replace(s, @"^[0-9０-９]{1,2}\s*[\.、)）]\s*", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^[①②③④⑤]\s*", "");

        return s.Trim('“', '”', '"', '「', '」', ' ');
    }

    /// <summary>补丁里带了这一页的网址/标题 —— 那只能治这一次，不是通用规则。</summary>
    private static bool LooksOverfit(string line, ContentSnapshot snapshot)
    {
        if (line.Contains("http", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Uri.TryCreate(snapshot.Url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host;

            if (host.Length > 3 && line.Contains(host, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var title = snapshot.Title;

        return !string.IsNullOrWhiteSpace(title)
               && title.Length >= 6
               && line.Contains(title, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 调用

    /// <summary>走真实判定路径问一次（同一套提示词与解析），拿到正常 + 从严两个值。</summary>
    private async Task<ModelCallResult?> JudgeAsync(
        ModelProvider provider, string instruction, ContentSnapshot snapshot, CancellationToken token)
    {
        try
        {
            var result = await _client.JudgeAsync(provider, instruction, snapshot, token).ConfigureAwait(false);

            _usage?.Record(provider.Id, result.PromptTokens, result.CompletionTokens,
                result.Outcome == ModelCallOutcome.Answered, result.CachedTokens);

            return result.Outcome == ModelCallOutcome.Answered ? result : null;
        }
        catch
        {
            _usage?.Record(provider.Id, null, null, answered: false);
            return null;
        }
    }

    private async Task<string?> AskAsync(ModelProvider provider, string prompt, CancellationToken token)
    {
        try
        {
            var (text, info) = await _client
                .AskAsync(provider, SystemPrompt, prompt, token)
                .ConfigureAwait(false);

            _usage?.Record(provider.Id, info.PromptTokens, info.CompletionTokens,
                info.Outcome == ModelCallOutcome.Answered, info.CachedTokens);

            return text;
        }
        catch
        {
            _usage?.Record(provider.Id, null, null, answered: false);
            return null;
        }
    }
}
