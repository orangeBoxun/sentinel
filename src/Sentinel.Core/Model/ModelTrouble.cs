using System.Net;
using System.Net.Sockets;

namespace Sentinel.Core.Model;

/// <summary>
/// 模型监督停摆时，是<b>哪一类</b>停摆 —— 决定提醒里那句话怎么说（节奏是一样的，见
/// <see cref="ModelAlertPacing"/>）。
/// </summary>
public enum ModelTroubleKind
{
    /// <summary>没停 —— 判定拿得到，一切正常。</summary>
    None,

    /// <summary>
    /// <b>服务商那边不给你用</b>：钥匙不对（401）、欠费（402）、超额或限流（429）、
    /// 免费额度被抢光、服务端自己出错（5xx）、或者干脆连不上（公网地址）。
    ///
    /// <para>
    /// 这些看着是几件不同的事，但后果和该做的事完全一样：<b>没人判了，去换一个能用的。</b>
    /// 所以归成一类，具体原因由判定结果自己那句消息带出来。
    /// </para>
    /// </summary>
    NeedsAttention,

    /// <summary>
    /// <b>你自己的服务没起来</b>（本机或内网）。这一类最好办 —— 拉起来就行。
    /// </summary>
    LocalUnreachable,

    /// <summary>
    /// 等超时了：服务在，但<b>慢到拿不到判定</b>。
    ///
    /// <para>
    /// 和"没起来"是同一个后果 —— 那几条规则等于不存在 —— 所以**要一样地吵**，
    /// 只是话不一样：那个是"拉起来就行"，这个是"换小一点的模型，或者换一个服务商"。
    /// </para>
    /// </summary>
    TooSlow,
}

/// <summary>
/// 这次失败是哪一类 —— 决定提醒里那句话怎么说。
///
/// <para>
/// 背景：模型判不出来的时候规则会<b>先放行</b>（那是对的，等待会把整条监控卡住），代价是
/// 那几条模型规则**等于不存在**，而界面上一切照旧。所以任何"拿不到判定"的结果都要说，
/// 而且要勤说 —— 它是"模型监督已经悄悄停了"和"我知道它停了"之间唯一的区别。
/// </para>
/// <para>
/// <b>踩过的坑（别再退回去）</b>：原来只对"本地服务没起来"和"太慢"吵，理由是"公网服务连不上
/// 多半是自己没网，那时候网站自己也打不开，拦不拦没区别"；钥匙/欠费那类则要五分钟才说一次。
/// 第一条理由对"整台机器断网"成立，对<b>用户真正遇到的那件事</b>不成立：免费的在线模型被限流、
/// 被拒绝、服务端 5xx —— 网好好的、网站打得开、他照样能刷，只是<b>没人判了</b>。
/// 而那两条路一条根本不提醒、一条五分钟才提醒一次，起不到"逼他换一个能用的模型"的作用。
/// </para>
/// </summary>
public static class ModelTrouble
{
    /// <summary>
    /// 这次失败是哪一类。<b>只有"拿到了判定"才算没停</b> —— 其余每一类都意味着没人判了。
    /// </summary>
    public static ModelTroubleKind Classify(ModelCallOutcome outcome, string? endpoint) => outcome switch
    {
        ModelCallOutcome.Answered => ModelTroubleKind.None,

        // 等超时：服务在，只是慢到拿不到判定。和"没起来"一样地吵，话不一样。
        ModelCallOutcome.Timeout => ModelTroubleKind.TooSlow,

        // 本机/内网连不上 = 你自己的服务没起来。这一类最好办，话说得最具体。
        ModelCallOutcome.Unreachable when IsLocal(endpoint) => ModelTroubleKind.LocalUnreachable,

        // 其余全部归"要你去处理"：
        //   4xx / 402 / 429 —— 服务商不给你用了
        //   5xx、或者回了 200 却读不出判定 —— 它没在判任何东西
        //   公网地址连不上 —— 没网，或者那个服务商就是不通
        // 话不一样（在结果的消息里），要做的事一样：换一个能用的。
        _ => ModelTroubleKind.NeedsAttention,
    };

    /// <summary>
    /// 这个地址指向的是不是"你自己的东西"（本机或内网）—— 只用来决定提醒里那句话怎么说
    /// （"拉起来就行" 还是 "换一个服务商"）。
    ///
    /// <para>
    /// 判据刻意**宽**：漏判的后果是"该说得更具体的话没说到"，多认几个的代价几乎为零。所以除了
    /// <c>localhost</c>/<c>127.x</c>/<c>::1</c>，内网段（10/8、172.16/12、192.168/16、
    /// 169.254/16、100.64/10）、IPv6 的链路本地与 ULA、单段主机名（<c>ollama</c>、<c>mybox</c>）、
    /// <c>.local</c>/<c>.internal</c>、以及 Docker 的宿主机别名都算。
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
