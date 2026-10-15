using Sentinel.Core.Model;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 模型列表那段：地址怎么猜、回复怎么读。
///
/// <para>
/// 这两件事都只能靠实测确定 —— 列表**不在**请求那条路径下面。实测 DeepSeek 的
/// <c>https://api.deepseek.com/models</c> 和 <c>/v1/models</c> 都是 200，而
/// <c>https://api.deepseek.com/anthropic/models</c> 是 404。也就是说，从
/// <c>https://api.deepseek.com/anthropic/v1/messages</c> 这条请求地址出发，推不出列表在哪。
/// 所以"猜"只当作默认值，取不到的时候界面会把试的是哪个地址说出来。
/// </para>
/// </summary>
public sealed class ModelListTests
{
    [Theory]
    // 标准形状：去掉 /chat/completions 就是它的 /models —— 首选的候选
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/models")]
    // 带版本段的服务商（智谱就是这种）：…/api/paas/v4/chat/completions → …/api/paas/v4/models
    [InlineData("https://open.bigmodel.cn/api/paas/v4/chat/completions",
                "https://open.bigmodel.cn/api/paas/v4/models")]
    // 本地 Ollama
    [InlineData("http://127.0.0.1:11434/v1/chat/completions", "http://127.0.0.1:11434/v1/models")]
    public void 先猜请求地址里那一段(string endpoint, string expected)
    {
        Assert.Equal(expected, ModelClient.SuggestListEndpoint(endpoint));
    }

    [Theory]
    // 列表**不在**请求路径下面的那几家：DeepSeek 的列表在域名 + /v1/models，
    // 请求地址那一段（/anthropic/…）底下是 404 —— 所以第二个候选必须是域名那个。
    [InlineData("https://api.deepseek.com/anthropic/v1/messages", "https://api.deepseek.com/v1/models")]
    [InlineData("https://relay.example.com:8443/proxy/openai/v1/chat/completions",
                "https://relay.example.com:8443/v1/models")]
    public void 第二个候选是域名加V1models(string endpoint, string expected)
    {
        var candidates = ModelClient.SuggestListEndpoints(endpoint);

        Assert.Contains(expected, candidates);
        Assert.Equal(expected, candidates[^1]);   // 兜底的那个排在最后
    }

    [Fact]
    public void 两个候选都要有_取列表时挨个试()
    {
        // 两个都可能错（各家不一样），所以取列表时挨个试，谁先回出模型名就用谁。
        var candidates = ModelClient.SuggestListEndpoints("https://open.bigmodel.cn/api/paas/v4/chat/completions");

        Assert.Equal(2, candidates.Count);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4/models", candidates[0]);
        Assert.Equal("https://open.bigmodel.cn/v1/models", candidates[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api.deepseek.com/anthropic")]     // 没有协议，不是绝对 URL
    [InlineData("随便写点什么")]
    public void 地址不成形就不猜(string endpoint)
    {
        // 猜不出来返回空字符串，界面会说"还不知道去哪个地址取" —— 猜一个错的更糟。
        Assert.Equal("", ModelClient.SuggestListEndpoint(endpoint));
    }

    [Theory]
    // 只写到 base（这是最常见的错）：补上对应格式的那一段
    [InlineData("https://api.deepseek.com/anthropic", ApiFormat.Anthropic,
                "https://api.deepseek.com/anthropic/v1/messages")]
    [InlineData("https://api.openai.com/v1", ApiFormat.OpenAi,
                "https://api.openai.com/v1/chat/completions")]
    // 带版本段的服务商：只补最后那一段，别再塞一个 /v1
    [InlineData("https://open.bigmodel.cn/api/paas/v4", ApiFormat.OpenAi,
                "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [InlineData("http://127.0.0.1:11434/v1", ApiFormat.OpenAi,
                "http://127.0.0.1:11434/v1/chat/completions")]
    // **末尾带斜杠也补** —— 界面那条"斜杠 = 别动我写的"只管"悄悄改用户输入"；
    // 测试连接失败时拿这个函数再试一次，多试一种写法没有副作用。
    // 用户那台就是卡在这儿：…/api/paas/v4/ → 服务端 404 回的 path 是 /v4/
    [InlineData("https://open.bigmodel.cn/api/paas/v4/", ApiFormat.OpenAi,
                "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    public void 只写到base的地址能补全(string endpoint, ApiFormat format, string expected) =>
        Assert.Equal(expected, ModelClient.CompleteEndpoint(endpoint, format));

    [Theory]
    [InlineData("https://api.deepseek.com/anthropic/v1/messages")]   // 本来就完整
    [InlineData("https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("随便写点什么")]
    public void 补不出什么就不动它(string endpoint) =>
        Assert.Null(ModelClient.CompleteEndpoint(endpoint, ApiFormat.OpenAi));

    [Fact]
    public void 智谱那种列表也认()
    {
        // 真实返回（只留前三个）。顺带记一笔：**列表不是权威** ——
        // 实测能用的 glm-4-flash 就不在里面（免费的那些常常不列）。
        // 所以模型名那个框必须能手填，不能只让人从列表里挑。
        var body = """{"object": "list", "data": [{"id": "glm-4.5", "object": "model", "created": 1753632000, "owned_by": "z-ai"}, {"id": "glm-4.5-air", "object": "model", "created": 1753632000, "owned_by": "z-ai"}, {"id": "glm-4.6", "object": "model", "created": 1759276800, "owned_by": "z-ai"}]}""";

        var models = ModelClient.ParseModelIds(body);

        Assert.Equal(3, models.Count);
        Assert.Contains("glm-4.5", models);
        Assert.DoesNotContain("glm-4-flash", models);
    }

    [Fact]
    public void 认OpenAI和DeepSeek那种data数组()
    {
        var ids = ModelClient.ParseModelIds(
            """{"object":"list","data":[{"id":"deepseek-reasoner"},{"id":"deepseek-chat"}]}""");

        Assert.Equal(new[] { "deepseek-chat", "deepseek-reasoner" }, ids);
    }

    [Fact]
    public void 认Anthropic那种带displayname的data数组()
    {
        var ids = ModelClient.ParseModelIds(
            """{"data":[{"id":"claude-sonnet-5-5","display_name":"Sonnet 5.5"}],"has_more":false}""");

        Assert.Equal(new[] { "claude-sonnet-5-5" }, ids);
    }

    [Fact]
    public void 认Google那种models数组并且去掉前缀()
    {
        var ids = ModelClient.ParseModelIds(
            """{"models":[{"name":"models/gemini-2.5-pro"},{"name":"models/gemini-2.5-flash"}]}""");

        Assert.Equal(new[] { "gemini-2.5-flash", "gemini-2.5-pro" }, ids);
    }

    [Fact]
    public void 重复的只留一个()
    {
        var ids = ModelClient.ParseModelIds("""{"data":[{"id":"a"},{"id":"a"},{"id":"b"}]}""");

        Assert.Equal(new[] { "a", "b" }, ids);
    }

    [Theory]
    [InlineData("")]                                        // 空回复
    [InlineData("<html>404 Not Found</html>")]              // 不是 JSON
    [InlineData("""{"data":[]}""")]                         // 有结构但没东西
    [InlineData("""{"object":"list"}""")]                   // 没有可认的数组
    [InlineData("""{"data":[{"object":"model"}]}""")]       // 有元素但没有名字
    public void 读不出来就返回空(string body)
    {
        // 空列表会走到"回了 200 但没读出模型名"，那句话会让人去改地址 —— 比猜一个错的好。
        Assert.Empty(ModelClient.ParseModelIds(body));
    }
}
