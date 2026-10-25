using System.Text;
using Sentinel.Core.Models;

namespace Sentinel.Core.Model;

/// <summary>讨论里的一句话。<paramref name="IsSystem"/> 是进度/提示，不是模型说的。</summary>
public sealed record DebateLine(string Speaker, string Text, bool IsSystem = false);

/// <summary>
/// 「模型讨论」：两个模型对同一页判断不一致时，让它们互相看对方的结论，
/// 找出更可能错的一方，并给出**可以直接粘进禁令**的修改建议。
///
/// <para>
/// 为什么要有它：责任链只能"往下问"，回答不了"到底谁错了、禁令该怎么改"。
/// 而用户真正想改的往往就是那句禁令 —— 一段话写得含糊，便宜的模型就会一直判错。
/// </para>
/// <para>
/// 它只负责跑流程、拼提示词；界面上边跑边显示（<c>say</c> 回调）。
/// </para>
/// </summary>
public sealed class ModelDebate
{
    private const int MaxBodyChars = 2000;

    private readonly ModelClient _client;
    private readonly UsageStore? _usage;

    public ModelDebate(ModelClient? client = null, UsageStore? usage = null)
    {
        _client = client ?? new ModelClient();
        _usage = usage;
    }

    /// <summary>
    /// 跑一场讨论。顺序是：各自独立判一次 → 各自看对方的结论 + 用户的看法 → 汇总建议。
    /// </summary>
    /// <param name="yourVerdict">用户自己的判断（"违规" / "不违规" / null = 不确定）。</param>
    public async Task RunAsync(
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

            return;
        }

        var context = YourTake(yourVerdict, yourReason);

        say(new DebateLine("系统",
            "从严判断只决定\"要不要交给链上下一个模型\"；真正拦不拦看正常判断。"
            + "所以这里要解决的是**从严也判错**的那一方。现在把两家的判断都摊开，互相挑错…",
            IsSystem: true));

        var a2 = await AskAsync(
            a, DebatePrompt(instruction, page, own: a1, otherName: b.Name, other: b1, context), token)
            .ConfigureAwait(false);

        say(new DebateLine(a.Name, a2 ?? "(没答上来)"));

        var b2 = await AskAsync(
            b, DebatePrompt(instruction, page, own: b1, otherName: a.Name, other: a1, context), token)
            .ConfigureAwait(false);

        say(new DebateLine(b.Name, b2 ?? "(没答上来)"));

        if (a2 is null && b2 is null)
        {
            say(new DebateLine("系统", "两边都没给出第二轮意见，只能到这儿了。", IsSystem: true));
            return;
        }

        say(new DebateLine("系统", "最后请「" + a.Name + "」把两家意见合成几条可以直接加进禁令的话…", IsSystem: true));

        var summary = await AskAsync(
            a, SummaryPrompt(instruction, a.Name, a2, b.Name, b2), token).ConfigureAwait(false);

        say(new DebateLine(a.Name, summary ?? "(没答上来)", IsSystem: false));
    }

    /// <summary>把一次判定的结果说清楚 —— 正常、从严、以及它们是否一致（会定论还是会往下问）。</summary>
    private static string Describe(ModelCallResult result)
    {
        var normal = result.Violates ? "违规" : "不违规";

        var strict = result.StrictViolates is { } s
            ? $"从严「{(s ? "违规" : "不违规")}」{(s == result.Violates ? "（一致 → 会定论）" : "（不一致 → 会交给下一个）")}"
            : "没给从严值（会被当成拿不准 → 会交给下一个）";

        var reason = result.Reason ?? result.Message;

        return $"正常「{normal}」，{strict}"
               + (string.IsNullOrWhiteSpace(reason) ? "" : $"\n理由：{reason}");
    }

    // ------------------------------------------------------------------ 提示词

    private static string JudgePrompt(string instruction, string page) =>
        $"""
         禁令（用户写给自己看的）：
         {instruction}

         {page}

         请判断这页是否违反禁令。**第一行只写「违规」或「不违规」**，然后用不超过 80 字说明理由。
         """;

    private static string DebatePrompt(
        string instruction, string page, ModelCallResult own, string otherName, ModelCallResult other, string context) =>
        $"""
         禁令（用户写给自己看的）：
         {instruction}

         {page}

         你的判断：正常「{(own.Violates ? "违规" : "不违规")}」，
                   从严「{(own.StrictViolates is { } os ? (os ? "违规" : "不违规") : "没给")}」
         另一个模型（{otherName}）的判断：正常「{(other.Violates ? "违规" : "不违规")}」，
                   从严「{(other.StrictViolates is { } bs ? (bs ? "违规" : "不违规") : "没给")}」

         {context}

         补充说明：**从严判断只用来决定"要不要把这一页交给链上下一个模型"**，
         真正拦不拦看的是正常判断。所以"正常判错、从严判对"不算问题（会被交给下一个模型）；
         真正要修的是**从严也判错**的那种情况 —— 它会被当成有把握，直接定论。

         请回答两点，各一段：
         1) 从严那一条，哪一方判错了（如果错在你，直接说），为什么会漏掉；
         2) 给出一句**可以直接加进上面那条禁令里的话**，让你这种页面上的从严判断不再漏。
            要求：具体、可照抄、一句话；不要写"更清楚一点"这种空话。
         """;

    private static string SummaryPrompt(string instruction, string aName, string? aText, string bName, string? bText) =>
        $"""
         禁令原文：
         {instruction}

         两人的第二轮意见：
         【{aName}】{aText ?? "(没答)"}
         【{bName}】{bText ?? "(没答)"}

         请给出最终建议，**不超过 3 条**，每条一行、直接可以追加到禁令后面（写成完整的一句话）。
         只写**要加的东西**，不要重写整条禁令。
         如果分歧其实是"用户的禁令自相矛盾"，用一句话点出来。
         """;

    private static string YourTake(string? verdict, string? reason)
    {
        if (string.IsNullOrWhiteSpace(verdict))
            return "用户自己没有表态。";

        var text = $"用户自己认为这页「{verdict}」";

        return string.IsNullOrWhiteSpace(reason) ? text + "（没写理由）。" : text + $"，理由是：{reason}";
    }

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

    // ------------------------------------------------------------------ 调用

    private const string SystemPrompt =
        "你们在帮用户校准一条内容禁令：两个模型对同一个页面的判断不一致，需要找出谁更可能错、" +
        "以及禁令该怎么改才能不再犯同样的错。用中文、短句、别客套；不要复述禁令原文。";

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
