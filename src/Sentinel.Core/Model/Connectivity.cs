using System.Net.Http;

namespace Sentinel.Core.Model;

/// <summary>
/// "现在这条网络，能不能撑住一次真正的请求" —— 用来把"你这边网络的事"和"服务商不给你用"分开。
///
/// <para>
/// 为什么必须分开：模型调用失败有很多种原因，但后果分两类。服务商那边的事（限流、欠费、5xx、
/// 拒绝连接）意味着<b>网站照样打得开、你照样能刷，只是没人判了</b> —— 那种要吵。而你自己的网络
/// 不通的时候，那些网站自己也打不开，拦不拦没有区别，为一件不会发生的事每 30 秒弹一次全屏
/// 只会制造噪音，把人训练成"看见 Sentinel 的提醒就关掉"。
/// </para>
/// <para>
/// <b>判据的时间尺度要和被检查的那件事对上。</b>这一条踩过：最初的版本是"3 秒内能建一条
/// TCP 连接就算有网"，结果是网一抖，模型请求照样挂满 60 秒超时，而探测说"有网"，于是被归成
/// "模型太慢"、继续弹全屏。能勾一次 handshake 和能跑完一次一分钟的请求不是一回事。所以现在：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>连续三趟 HTTPS 请求都要完成</b>（不是一次 ping）。一次两次能通、三趟里有一趟卡住，
/// 就是"不稳"—— 而那正是长请求会失败的网络。
/// </description></item>
/// <item><description>
/// <b>走 HttpClient，也就是走系统代理</b>：模型请求走的是同一条路。代理坏了的时候两者一起坏，
/// 那本来就该算"你这边的事"。
/// </description></item>
/// <item><description>
/// <b>任何 HTTP 状态码都算通</b>。我们要的是"能不能完成一次交换"，不是"这个网址好不好用"。
/// </description></item>
/// </list>
/// <para>
/// 结果缓存 30 秒：网络的恢复通常也就是几十秒内的事，而每个页面都探一次是白花时间。
/// </para>
/// </summary>
public sealed class Connectivity
{
    /// <summary>连续几趟都通才算"稳"。三趟是"能跑完一次长请求"的最小写照。</summary>
    private const int Rounds = 3;

    private const string Baidu = "https://www.baidu.com/";
    private const string Bing = "https://cn.bing.com/";

    /// <summary>
    /// 三趟打两个国内可达的地址。**不放国外站点**：这里要回答的是"网通不通"，
    /// 而不是"能不能上外网"—— 后者不通的时候照样可能有一个能用的在线模型。
    /// </summary>
    private static readonly string[] RoundUrls = { Baidu, Bing, Baidu };

    private static readonly TimeSpan EachTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly Func<string, CancellationToken, Task<bool>> _get;
    private readonly Func<DateTimeOffset> _now;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private bool _stable = true;

    /// <param name="get">真去发一次 GET 并等响应头；返回 false 表示没换回响应。测试用它假装通/不通。</param>
    /// <param name="now">取当前时刻。测试传一个能推的假时钟进来。</param>
    public Connectivity(
        Func<string, CancellationToken, Task<bool>>? get = null,
        Func<DateTimeOffset>? now = null)
    {
        _get = get ?? HttpGetAsync;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// 网络能不能连续跑几趟。缓存期内直接给上次的答案。
    ///
    /// <para>
    /// 缓存的是<b>不稳</b>这个结论也要缓存 —— 网络一抖就反复去探，只会把本来就窄的带宽再挤一点。
    /// </para>
    /// </summary>
    public async Task<bool> IsStableAsync(CancellationToken token = default)
    {
        var now = _now();

        if (now - _checkedAt < CacheFor)
            return _stable;

        await _gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            // 等锁的这一会儿，别人可能已经探完了。
            now = _now();

            if (now - _checkedAt < CacheFor)
                return _stable;

            _stable = await ProbeRoundsAsync(token).ConfigureAwait(false);
            _checkedAt = now;

            return _stable;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 某一个地址现在换不换得回响应（任何状态码都算）。
    ///
    /// <para>
    /// 用在"超时"那类上：模型请求挂满了一分钟，但<b>那个服务商自己的地址</b>能不能在半秒内
    /// 换回一个响应？能，说明路是通的、慢的是它的推理；不能，说明这条路本身不通。
    /// </para>
    /// </summary>
    public Task<bool> IsReachableAsync(string url, CancellationToken token = default) =>
        string.IsNullOrWhiteSpace(url) ? Task.FromResult(false) : RunAsync(url, token);

    private async Task<bool> ProbeRoundsAsync(CancellationToken token)
    {
        foreach (var url in RoundUrls)
        {
            if (!await RunAsync(url, token).ConfigureAwait(false))
                return false;   // 三趟里有一趟卡住，就是不稳
        }

        return true;
    }

    private async Task<bool> RunAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(EachTimeout);

        return await _get(url, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 真去发一次 GET，只等响应头，不看内容、不看状态码。
    /// </summary>
    private static async Task<bool> HttpGetAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await SharedHttp
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            return true;   // 404/429 都算 —— 我们要的是"能完成一次交换"
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 不设总体超时（每次调用自己带一个），免得和这里的逐次截止打架。
    /// </summary>
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
