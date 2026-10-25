using System.Net;
using System.Net.Http;
using System.Text.Json;
using Sentinel.Core.Model;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 「模型讨论」：按用户填的标准找判错的一方 → 判对的指出它错在哪 → 判错的写补丁 →
/// 真加进禁令重判这一页；不过就继续，直到判对。
///
/// <para>
/// 这里验的是**流程和提示词**（谁先谁后、补丁有没有真的加进去重判、没通过会不会再来一轮），
/// 判断得对不对是模型的事，测试管不了。
/// </para>
/// </summary>
public class DebateTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(string Model, string Body)> Calls { get; } = new();

        /// <summary>这些模型一律连不上（模拟"某个服务商挂了"）。</summary>
        public HashSet<string> Broken { get; } = new();

        /// <summary>出现了这句话，就说明这次是"补丁已经加进禁令"之后的判定。</summary>
        public string Marker { get; set; } = "就从严判违规";

        /// <summary>加了补丁之后的判定；默认改对了。</summary>
        public Func<string, string> Patched { get; set; } = _ => "[true, \"改对了\", true]";

        /// <summary>没有补丁时的判定；默认 a 判错（正常/从严都不违规）、b 判对。</summary>
        public Func<string, string> Unpatched { get; set; } = model =>
            model == "a" ? "[false, \"只是搜索页\", false]" : "[true, \"热点新闻\", true]";

        public string Diagnosis { get; set; } = "它把搜索结果页当成了中间页，没看搜索词和结果标题。";

        public string Fix { get; set; } =
            "我漏在没把搜索词算进判断。\n补丁：\n- 搜索页只要搜索词或结果标题指向娱乐、热点新闻，就从严判违规。";

        public string Proposal { get; set; } = "补丁：\n- 搜索页只要搜索词指向娱乐或热点新闻，就从严判违规。";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            var model = "";
            try
            {
                model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString() ?? "";
            }
            catch
            {
                // 读不出来就当空模型名。
            }

            Calls.Add((model, body));

            if (Broken.Contains(model))
                throw new HttpRequestException("目标计算机积极拒绝，无法连接。");

            // 判定走的是真实判定路径（system 里是"你是一个内容审查器"），要回 JSON；
            // 诊断/补丁/各写各的，都是自由文本。
            var judging = body.Contains("内容审查器", StringComparison.Ordinal);

            string text;

            if (judging)
            {
                text = body.Contains(Marker, StringComparison.Ordinal)
                    ? Patched(model)
                    : Unpatched(model);
            }
            else if (body.Contains("只做诊断", StringComparison.Ordinal))
            {
                text = Diagnosis;
            }
            else if (body.Contains("第一段（一句话）", StringComparison.Ordinal))
            {
                text = model == "a" ? Fix : Proposal;
            }
            else
            {
                text = Proposal;
            }

            var json = "{\"choices\":[{\"message\":{\"content\":"
                       + JsonSerializer.Serialize(text) + "}}]}";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private static ModelProvider Provider(string id) => new()
    {
        Id = id,
        Name = id.ToUpperInvariant(),
        Format = ApiFormat.OpenAi,
        Endpoint = "https://relay.invalid/v1/chat/completions",
        Model = id,
        ApiKey = "k",
    };

    private static ContentSnapshot Snapshot() =>
        new("https://zhihu.com/search?q=1", "微波炉推荐 - 知乎搜索", "一堆标题和一个搜索结果列表", DateTimeOffset.Now);

    private static async Task<(List<DebateLine> Lines, ScriptedHandler Handler, DebateOutcome? Outcome)> RunAsync(
        ScriptedHandler handler, string? opinion = "违规", string? reason = "我在摸鱼")
    {
        var lines = new List<DebateLine>();

        var debate = new ModelDebate(new ModelClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }));

        var outcome = await debate.RunAsync(
            Provider("a"), Provider("b"),
            "禁止与学习工作无关的浏览", Snapshot(),
            opinion, reason,
            line => lines.Add(line),
            CancellationToken.None);

        return (lines, handler, outcome);
    }

    [Fact]
    public async Task 你的标准会先被念出来_它是这场讨论唯一的对错依据()
    {
        var (lines, _, _) = await RunAsync(new ScriptedHandler());

        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("你的标准：这一页「违规」"));
        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("理由：我在摸鱼"));
    }

    [Fact]
    public async Task 第一轮真独立判断_用的是和监控同一套提示词()
    {
        var (_, handler, _) = await RunAsync(new ScriptedHandler());

        // 第一轮里不该出现"另一个模型的判断"，也必须是判定那套 system 提示词。
        Assert.DoesNotContain("另一个模型", handler.Calls[0].Body);
        Assert.DoesNotContain("另一个模型", handler.Calls[1].Body);
        Assert.Contains("内容审查器", handler.Calls[0].Body);
    }

    [Fact]
    public async Task 判对的先指出判错的错在哪_再由判错的写补丁()
    {
        var (_, handler, _) = await RunAsync(new ScriptedHandler());

        // a 错（正常/从严都不违规），b 对 → b 诊断、a 写补丁。
        Assert.Equal(new[] { "a", "b", "b", "a" }, handler.Calls.Take(4).Select(c => c.Model));

        Assert.Contains("只做诊断", handler.Calls[2].Body);
        Assert.Contains("用户自己认为这页「违规」", handler.Calls[2].Body);

        // 判错的一家拿到了完整诊断。
        Assert.Contains("没看搜索词", handler.Calls[3].Body);
        Assert.Contains("第一段（一句话）", handler.Calls[3].Body);
    }

    [Fact]
    public async Task 补丁会被真的加进禁令_再判一次才算通过()
    {
        var (_, handler, outcome) = await RunAsync(new ScriptedHandler());

        Assert.NotNull(outcome);
        Assert.True(outcome!.Verified);
        Assert.Single(outcome.Patch);

        // 验证用的那次判定，提示词里必须出现补丁那句话。
        Assert.Contains("就从严判违规", handler.Calls[4].Body);
        Assert.Contains("就从严判违规", handler.Calls[5].Body);
    }

    [Fact]
    public async Task 加了补丁还判不对就继续下一轮_最多三轮()
    {
        var handler = new ScriptedHandler { Patched = _ => "[false, \"还是漏\", false]" };

        var (lines, calls, outcome) = await RunAsync(handler);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Verified);
        Assert.Equal(3, outcome.Rounds);

        // 每轮：诊断 + 写补丁 + 两次验证；外加最开始两次独立判断。
        Assert.Equal(14, calls.Calls.Count);

        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("还没对上"));
        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("没验证过"));
    }

    [Fact]
    public async Task 判错的那家不给补丁时_让判对的那家直接写()
    {
        var handler = new ScriptedHandler { Fix = "我没错。" };

        var (_, calls, outcome) = await RunAsync(handler);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Verified);

        // a b（判）→ b（诊断）→ a（没给）→ b 兜底 → a b（验证）
        Assert.Equal(new[] { "a", "b", "b", "a", "b", "a", "b" }, calls.Calls.Select(c => c.Model));
    }

    [Fact]
    public async Task 补丁里带了这一页的网址会被点出来()
    {
        var handler = new ScriptedHandler
        {
            Marker = "指向娱乐就从严",
            Fix = "漏在按站点放过了。\n补丁：\n- 知乎（zhihu.com）这类搜索页只要搜索词指向娱乐就从严判违规。",
        };

        var (lines, _, outcome) = await RunAsync(handler);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Verified);
        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("只能治这一次"));
    }

    [Fact]
    public async Task 没表态时不做对错评判_只让分歧消失()
    {
        var (lines, _, outcome) = await RunAsync(new ScriptedHandler(), opinion: null, reason: null);

        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("你没表态"));
        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("通过了也不等于判对了"));

        // 没有"判对的一方"，退回两家各写各的（a、b 各一次）。
        Assert.NotNull(outcome);
        Assert.True(outcome!.Verified);
    }

    [Fact]
    public async Task 正常判错但从严判对也算不漏_不用改禁令()
    {
        // a 正常判不违规，但从严判违规 → 两个判断不一致，会交给下一个模型，链上还有人兜。
        var handler = new ScriptedHandler
        {
            Unpatched = model => model == "a"
                ? "[false, \"只是搜索页\", true]"
                : "[true, \"热点新闻\", true]",
        };

        var (lines, calls, outcome) = await RunAsync(handler);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Verified);
        Assert.Empty(outcome.Patch);

        // 连补丁都不用提，两次独立判断就结束了。
        Assert.Equal(2, calls.Calls.Count);
        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("不会把这一页有把握地判死"));
    }

    [Fact]
    public async Task 有个模型连不上就停下来说明_不做无意义的讨论()
    {
        var handler = new ScriptedHandler();
        handler.Broken.Add("b");

        var (lines, calls, outcome) = await RunAsync(handler);

        // A 一次；B 连不上会被**重试一次**（那条重试是给 TLS 握手抖动准备的）——
        // 所以一共 3 次，但没有下一轮。
        Assert.Equal(3, calls.Calls.Count);
        Assert.Equal(new[] { "a", "b", "b" }, calls.Calls.Select(c => c.Model));
        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("没给出结论"));
        Assert.Null(outcome);
    }
}
