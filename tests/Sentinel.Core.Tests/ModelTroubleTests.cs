using Sentinel.Core.Model;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 「连不上要不要吵」这件事的两条判据。
///
/// <para>
/// 它的后果是不对称的：漏判 = 模型规则悄悄失效而人不知道（最坏）；多判 = 多弹一次全屏。
/// 所以地址判据刻意做宽，这里把"该认的"和"不该认的"都钉住。
/// </para>
/// </summary>
public class ModelTroubleTests
{
    [Theory]
    // 本机
    [InlineData("http://localhost:11434/v1/chat/completions")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://127.5.5.5:8080/v1")]
    [InlineData("http://[::1]:11434/v1")]
    [InlineData("http://0.0.0.0:11434")]
    // 内网段
    [InlineData("http://192.168.1.50:11434/v1")]
    [InlineData("http://10.0.0.7/v1")]
    [InlineData("http://172.16.0.1/v1")]
    [InlineData("http://172.31.255.254/v1")]
    [InlineData("http://169.254.10.10/v1")]
    [InlineData("http://100.64.0.1/v1")]
    // 内网名字
    [InlineData("http://ollama:11434/v1")]
    [InlineData("http://mybox/v1")]
    [InlineData("http://nas.local:1234/v1")]
    [InlineData("http://judge.internal/v1")]
    [InlineData("http://host.docker.internal:11434/v1")]
    // 没有 scheme 也认
    [InlineData("127.0.0.1:11434")]
    public void Local_endpoints_are_recognised(string endpoint) =>
        Assert.True(ModelTrouble.IsLocal(endpoint), endpoint);

    [Theory]
    // 公网名字与公网 IP：连不上就按"没网"处理，不吵
    [InlineData("https://api.deepseek.com/anthropic/v1/messages")]
    [InlineData("https://cn.zhihuiai.top/v1/chat/completions")]
    [InlineData("http://8.8.8.8/v1")]
    [InlineData("http://172.32.0.1/v1")]      // 172.16/12 之外
    [InlineData("http://100.128.0.1/v1")]     // 100.64/10 之外
    [InlineData("http://192.169.1.1/v1")]     // 192.168/16 之外
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("这不是一个地址")]
    public void Public_endpoints_are_not_treated_as_local(string endpoint) =>
        Assert.False(ModelTrouble.IsLocal(endpoint), endpoint);

    [Fact]
    public void Local_unreachable_is_the_loud_one()
    {
        // 本机/内网连不上 → 要吵（网站照样打得开，只是没人判了）
        Assert.Equal(ModelTroubleKind.LocalUnreachable,
            ModelTrouble.Classify(ModelCallOutcome.Unreachable, "http://127.0.0.1:11434/v1"));

        // 公网连不上 → 不吵（那时候网站自己也打不开，拦不拦没区别）
        Assert.Equal(ModelTroubleKind.None,
            ModelTrouble.Classify(ModelCallOutcome.Unreachable, "https://api.deepseek.com/anthropic/v1/messages"));

        // 钥匙/欠费那类，不管地址在哪都要说
        Assert.Equal(ModelTroubleKind.NeedsAttention,
            ModelTrouble.Classify(ModelCallOutcome.NeedsAttention, "https://api.deepseek.com/anthropic/v1/messages"));
        Assert.Equal(ModelTroubleKind.NeedsAttention,
            ModelTrouble.Classify(ModelCallOutcome.NeedsAttention, "http://127.0.0.1:11434/v1"));

        // 拿到了判定 / 服务端抽风 → 不吵
        Assert.Equal(ModelTroubleKind.None, ModelTrouble.Classify(ModelCallOutcome.Answered, "http://127.0.0.1:11434/v1"));
        Assert.Equal(ModelTroubleKind.None, ModelTrouble.Classify(ModelCallOutcome.NoAnswer, "http://127.0.0.1:11434/v1"));
    }
}
