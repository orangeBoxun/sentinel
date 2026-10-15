using System.Net;
using System.Net.Sockets;

namespace Sentinel.Core.Model;

/// <summary>
/// 模型监督停摆时，是<b>哪一类</b>停摆 —— 提醒的节奏不一样。
/// </summary>
public enum ModelTroubleKind
{
    /// <summary>没停，或者停的原因不值得吵（比如公网服务连不上，多半是那边或者你自己没网）。</summary>
    None,

    /// <summary>要你去处理：钥匙不对、欠费、超额、地址写错。外面的事，慢慢说就行。</summary>
    NeedsAttention,

    /// <summary>
    /// 你自己的服务没起来（本机或内网）。这一类要催得勤 —— 它最好办，拉起来就行。
    /// </summary>
    LocalUnreachable,
}

/// <summary>
/// 判定"这次失败要不要吵、以及吵得多勤"。
///
/// <para>
/// 背景：模型判不出来的时候规则会<b>先放行</b>（那是对的，等待会把整条监控卡住），代价是
/// 那几条模型规则**等于不存在**，而界面上一切照旧。所以失败分两类处理：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>要你去处理</b>的（钥匙、欠费、超额、地址）—— 必须说，不说就是悄悄失效。
/// </description></item>
/// <item><description>
/// <b>公网连不上</b> —— 不说。没网的时候那些娱乐网站自己也打不开，拦不拦没有区别，
/// 为一件不会发生的事弹全屏只会让人把这个功能关掉。
/// </description></item>
/// <item><description>
/// <b>本地服务连不上</b> —— <b>要说，而且要吵</b>。上面那条理由在这里不成立：本机/内网的
/// 模型没起来的时候，网站照样打得开、你照样能刷，只是没人管了 —— 那正是最该被提醒的情形。
/// </description></item>
/// </list>
/// </summary>
public static class ModelTrouble
{
    public static ModelTroubleKind Classify(ModelCallOutcome outcome, string? endpoint) => outcome switch
    {
        ModelCallOutcome.NeedsAttention => ModelTroubleKind.NeedsAttention,

        // 等超时：服务在，只是慢/卡。要处理（换模型或关掉读正文），但那不是"拉起来就行"，
        // 所以不归"本地没起来"那一类，也不按它的 30 秒节奏催。
        ModelCallOutcome.Timeout => ModelTroubleKind.NeedsAttention,

        ModelCallOutcome.Unreachable when IsLocal(endpoint) => ModelTroubleKind.LocalUnreachable,
        _ => ModelTroubleKind.None,
    };

    /// <summary>
    /// 这个地址指向的是不是"你自己的东西"（本机或内网）。
    ///
    /// <para>
    /// 判据刻意**宽**：漏判的后果是"该死吵的没吵"，而多认几个的代价几乎为零。所以除了
    /// <c>localhost</c>/<c>127.x</c>/<c>::1</c>，内网段（10/8、172.16/12、192.168/16、
    /// 169.254/16、100.64/10）、IPv6 的链路本地与 ULA、单段主机名（<c>ollama</c>、<c>mybox</c>）、
    /// <c>.local</c>/<c>.internal</c>、以及 Docker 的宿主机别名都算。
    /// </para>
    /// <para>
    /// 带点的公网名字（<c>api.deepseek.com</c>）和公网 IP 不算 —— 那些连不上就该按"没网"处理。
    /// </para>
    /// </summary>
    public static bool IsLocal(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            && !Uri.TryCreate("http://" + endpoint.Trim(), UriKind.Absolute, out uri))
        {
            return false;   // 既不是 URL，也不是"主机:端口"那样的东西
        }

        var host = uri.Host.Trim('[', ']').ToLowerInvariant();

        if (host.Length == 0)
            return false;

        if (host is "localhost" or "::1" or "0.0.0.0"
            or "host.docker.internal" or "host.containers.internal" or "gateway.docker.internal")
        {
            return true;
        }

        if (host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal))
        {
            return true;
        }

        // 单段主机名（没有点）：ollama、mybox、raspberrypi —— 几乎一定是内网名字。
        // 公网名字总有至少一个点，所以这条不会误伤。但**得像主机名**才算：纯 ASCII 的
        // 字母数字加连字符/下划线。否则"这不是一个地址"这种也会被当成内网主机名 ——
        // 而它其实是地址写错了，那件事有另一条路去说。
        if (!host.Contains('.')
            && host.Length <= 63
            && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IsLocalAddress(address);
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();

        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                return bytes[0] switch
                {
                    10 => true,                                        // 10/8
                    127 => true,                                       // 127/8（回环）
                    169 when bytes[1] == 254 => true,                   // 169.254/16 链路本地
                    172 when bytes[1] >= 16 && bytes[1] <= 31 => true,  // 172.16/12
                    192 when bytes[1] == 168 => true,                   // 192.168/16
                    100 when bytes[1] >= 64 && bytes[1] <= 127 => true, // 100.64/10（运营商级 NAT）
                    _ => false,
                };

            case AddressFamily.InterNetworkV6:
                // fc00::/7 唯一本地地址，fe80::/10 链路本地
                return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);

            default:
                return false;
        }
    }
}
