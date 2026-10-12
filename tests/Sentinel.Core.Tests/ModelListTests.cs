using Sentinel.Core.Model;
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
    [InlineData("https://api.deepseek.com/anthropic/v1/messages", "https://api.deepseek.com/v1/models")]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/models")]
    [InlineData("https://relay.example.com:8443/proxy/openai/v1/chat/completions",
                "https://relay.example.com:8443/v1/models")]
    public void 猜出来的地址是域名加V1models(string endpoint, string expected)
    {
        Assert.Equal(expected, ModelClient.SuggestListEndpoint(endpoint));
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
