using System.Net;
using System.Net.Http;
using System.Text.Json;
using Sentinel.Core.Model;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 「模型讨论」：两个模型互相挑错、最后给禁令改法。
///
/// <para>
/// 这里验的是**流程和提示词**（谁先谁后、第二轮有没有把对方的结论和用户的看法带进去），
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

            // 第一轮走的是**真实判定路径**（system 里是"你是一个内容审查器"），要回 JSON 判定；
            // 第二轮和汇总是自由文本。
            var judging = body.Contains("内容审查器", StringComparison.Ordinal);

            var text = judging
                ? model == "a"
                    ? "[true, \"像在摸鱼\", true]"
                    : "[false, \"只是搜索页\", false]"
                : model == "a"
                    ? "A 的辩论意见：我从严也漏了，因为禁令没写搜索页怎么算。"
                    : "B 的辩论意见：我在从严那边是对的，因为……";

            // 最后那次汇总（a 的第二次自由文本调用）给几条建议。
            if (!judging && model == "a" && Calls.Count(c => c.Model == "a" && !c.Body.Contains("内容审查器", StringComparison.Ordinal)) >= 2)
                text = "建议：追加一句「搜索结果页只有标题、没有可播放内容时不拦」。";

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

    private static async Task<(List<DebateLine> Lines, ScriptedHandler Handler)> RunAsync(
        ScriptedHandler handler, string? opinion = "违规", string? reason = "我在摸鱼")
    {
        var lines = new List<DebateLine>();

        var debate = new ModelDebate(new ModelClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }));

        await debate.RunAsync(
            Provider("a"), Provider("b"),
            "禁止与学习工作无关的浏览", Snapshot(),
            opinion, reason,
            line => lines.Add(line),
            CancellationToken.None);

        return (lines, handler);
    }

    [Fact]
    public async Task 先各自判一次_再互相挑错_最后汇总()
    {
        var (_, handler) = await RunAsync(new ScriptedHandler());

        // A 判一次、B 判一次、A 看 B 的、B 看 A 的、A 汇总 —— 一共五次。
        Assert.Equal(5, handler.Calls.Count);
        Assert.Equal(new[] { "a", "b", "a", "b", "a" }, handler.Calls.Select(c => c.Model));
    }

    [Fact]
    public async Task 第一轮真独立判断_用的是和监控同一套提示词()
    {
        var (_, handler) = await RunAsync(new ScriptedHandler());

        // 第一轮里不该出现"另一个模型的判断"，也必须是判定那套 system 提示词。
        Assert.DoesNotContain("另一个模型", handler.Calls[0].Body);
        Assert.DoesNotContain("另一个模型", handler.Calls[1].Body);
        Assert.Contains("内容审查器", handler.Calls[0].Body);
    }

    [Fact]
    public async Task 第二轮把对方的判断和你的看法都带进去_并点明从严才要修()
    {
        var (_, handler) = await RunAsync(new ScriptedHandler());

        // A 的第二轮：要看到 B 的两个判断，也要看到"用户认为违规 + 理由"。
        Assert.Contains("另一个模型（B）", handler.Calls[2].Body);
        Assert.Contains("正常「不违规」", handler.Calls[2].Body);
        Assert.Contains("用户自己认为这页「违规」", handler.Calls[2].Body);
        Assert.Contains("我在摸鱼", handler.Calls[2].Body);

        // 提示词必须说清楚：从严判断只决定要不要往下问，正常判错不算问题。
        Assert.Contains("从严判断只用来决定", handler.Calls[2].Body);

        // B 的第二轮：反过来看到 A 的。
        Assert.Contains("另一个模型（A）", handler.Calls[3].Body);
    }

    [Fact]
    public async Task 汇总那一次要两人的第二轮意见()
    {
        var (lines, handler) = await RunAsync(new ScriptedHandler());

        Assert.Contains("A 的辩论意见", handler.Calls[4].Body);
        Assert.Contains("B 的辩论意见", handler.Calls[4].Body);

        Assert.Contains(lines, l => !l.IsSystem && l.Text.Contains("建议"));
    }

    [Fact]
    public async Task 有个模型连不上就停下来说明_不做无意义的讨论()
    {
        var handler = new ScriptedHandler();
        handler.Broken.Add("b");

        var (lines, calls) = await RunAsync(handler);

        // A 一次；B 连不上会被**重试一次**（那条重试是给 TLS 握手抖动准备的）——
        // 所以一共 3 次，但没有第二轮。
        Assert.Equal(3, calls.Calls.Count);
        Assert.Equal(new[] { "a", "b", "b" }, calls.Calls.Select(c => c.Model));
        Assert.Contains(lines, l => l.IsSystem && l.Text.Contains("没给出结论"));
    }

    [Fact]
    public async Task 没表态时提示词里写清楚()
    {
        var (_, handler) = await RunAsync(new ScriptedHandler(), opinion: null, reason: null);

        Assert.Contains("用户自己没有表态", handler.Calls[2].Body);
    }
}
