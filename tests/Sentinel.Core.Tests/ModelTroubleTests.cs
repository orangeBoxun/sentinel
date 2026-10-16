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
    public void 每一类失败都是没人判了()
    {
        // 只有**拿到判定**才算没停，其余每一类都要说 —— 这一条是这个功能的意义所在。
        Assert.Equal(ModelTroubleKind.None,
            ModelTrouble.Classify(ModelCallOutcome.Answered, "https://api.deepseek.com/anthropic/v1/messages"));

        // 本机/内网连不上 → 你自己的服务没起来（话最具体：拉起来就行）
        Assert.Equal(ModelTroubleKind.LocalUnreachable,
            ModelTrouble.Classify(ModelCallOutcome.Unreachable, "http://127.0.0.1:11434/v1"));

        // 超时：服务在，但慢到拿不到判定。"没起来"和"太慢"的后果一样，吵得也一样。
        Assert.Equal(ModelTroubleKind.TooSlow,
            ModelTrouble.Classify(ModelCallOutcome.Timeout, "http://127.0.0.1:11434/v1"));
        Assert.Equal(ModelTroubleKind.TooSlow,
            ModelTrouble.Classify(ModelCallOutcome.Timeout, "https://api.deepseek.com/anthropic/v1/messages"));

        // **公网连不上照样要说。** 原来这里判的是"不吵"，理由是"公网连不上多半是自己没网，
        // 那时候网站自己也打不开"。那条理由对"整台机器断网"成立，对真正会遇到的那件事不成立：
        // 免费的在线模型被限流、被拒绝、服务端出错 —— 网好好的、网站打得开、他照样能刷，
        // 只是没人判了。所以要说的不是"是不是你的服务"，而是"还判不判得出来"。
        Assert.Equal(ModelTroubleKind.NeedsAttention,
            ModelTrouble.Classify(ModelCallOutcome.Unreachable, "https://api.deepseek.com/anthropic/v1/messages"));

        // 钥匙/欠费/限流：不管地址在哪都要说
        Assert.Equal(ModelTroubleKind.NeedsAttention,
            ModelTrouble.Classify(ModelCallOutcome.NeedsAttention, "https://api.deepseek.com/anthropic/v1/messages"));
        Assert.Equal(ModelTroubleKind.NeedsAttention,
            ModelTrouble.Classify(ModelCallOutcome.NeedsAttention, "http://127.0.0.1:11434/v1"));

        // 服务端出错了（5xx）、或者回了 200 却读不出判定 —— 它没在判任何东西，一样要说。
        Assert.Equal(ModelTroubleKind.NeedsAttention,
            ModelTrouble.Classify(ModelCallOutcome.NoAnswer, "https://api.deepseek.com/anthropic/v1/messages"));
        Assert.Equal(ModelTroubleKind.NeedsAttention,
            ModelTrouble.Classify(ModelCallOutcome.NoAnswer, "http://127.0.0.1:11434/v1"));
    }
}
