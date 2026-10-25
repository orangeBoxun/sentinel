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

            var text = model == "a" ? "违规\nA 的理由：像在摸鱼" : "不违规\nB 的理由：只是搜索页";

            // 最后那次汇总（a 的第三次调用）给一段建议。
            if (model == "a" && Calls.Count(c => c.Model == "a") >= 3)
                text = "建议：把禁令里「搜索页不拦」写成「只有标题、没有播放内容的页面不拦」。";

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
    public async Task 第一轮是真独立判断_不带对方的结论()
    {
        var (_, handler) = await RunAsync(new ScriptedHandler());

        // 第一轮（a 的第一次、b 的第一次）里不该出现对方的判断。
        Assert.DoesNotContain("B 的理由", handler.Calls[0].Body);
        Assert.DoesNotContain("A 的理由", handler.Calls[1].Body);
    }

    [Fact]
    public async Task 第二轮把对方的结论和你的看法都带进去()
    {
        var (_, handler) = await RunAsync(new ScriptedHandler());

        // A 的第二轮：要看到 B 的判断，也要看到"用户认为违规 + 理由"。
        Assert.Contains("B 的理由", handler.Calls[2].Body);
        Assert.Contains("用户自己认为这页「违规」", handler.Calls[2].Body);
        Assert.Contains("我在摸鱼", handler.Calls[2].Body);

        // B 的第二轮：反过来看到 A 的。
        Assert.Contains("A 的理由", handler.Calls[3].Body);
    }

    [Fact]
    public async Task 汇总那一次要两人的第二轮意见()
    {
        var (lines, handler) = await RunAsync(new ScriptedHandler());

        Assert.Contains("A 的理由", handler.Calls[4].Body);
        Assert.Contains("B 的理由", handler.Calls[4].Body);

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
