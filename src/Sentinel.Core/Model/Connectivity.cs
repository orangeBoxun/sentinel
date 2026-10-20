using System.Net.Sockets;

namespace Sentinel.Core.Model;

/// <summary>
/// "这台机器现在有没有网" —— 用一个便宜的探测，把"你这边断了"和"服务商不给你用"分开。
///
/// <para>
/// 为什么必须分开：模型调用失败有很多种原因，但后果分两类。服务商那边的事（限流、欠费、5xx、
/// 拒绝连接）意味着<b>网站照样打得开、你照样能刷，只是没人判了</b> —— 那种要吵。而整台机器
/// 没网的时候，那些网站自己也打不开，拦不拦没有区别，为一件不会发生的事每 30 秒弹一次全屏
/// 只是在制造噪音，会把人训练成"看见 Sentinel 的提醒就关掉"。
/// </para>
/// <para>
/// 探测刻意**不去问模型服务商**：那家的连不上正是我们要判定的对象。这里只问"能不能在公网上
/// 建一条 TCP 连接" —— 能，就说明网是通的，模型仍然不通就是服务商的事。
/// </para>
/// <para>
/// 结果缓存 30 秒：一个页面一个页面地问会白花时间，而网络的恢复通常也就是几十秒内的事。
/// </para>
/// </summary>
public sealed class Connectivity
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 试两个域名。一个在国内通、一个在国外通 —— 只试一个的话，那个恰好被墙或被解析不了，
    /// 就会被误判成"整台机器没网"，而后者的后果是提醒全部安静下来。
    /// </summary>
    private static readonly string[] Hosts = { "www.baidu.com", "www.microsoft.com" };

    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Func<DateTimeOffset> _now;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private bool _online = true;

    /// <param name="probe">真去试一次网络。测试用它来假装通/不通。</param>
    /// <param name="now">取当前时刻。测试传一个能推的假时钟进来。</param>
    public Connectivity(Func<CancellationToken, Task<bool>>? probe = null, Func<DateTimeOffset>? now = null)
    {
        _probe = probe ?? ProbeTcpAsync;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>现在能不能上公网。缓存期内直接给上次的答案。</summary>
    public async Task<bool> IsOnlineAsync(CancellationToken token = default)
    {
        var now = _now();

        if (now - _checkedAt < CacheFor)
            return _online;

        await _gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            // 等锁的这一会儿，别人可能已经探完了。
            now = _now();

            if (now - _checkedAt < CacheFor)
                return _online;

            _online = await _probe(token).ConfigureAwait(false);
            _checkedAt = now;

            return _online;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>忘掉上一次的结果 —— 换了服务商、或者刚从停摆里恢复时用它。</summary>
    public void Forget()
    {
        _checkedAt = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// 真去建一条 TCP 连接。DNS 解析 + 握手都在这三秒里，所以"域名解析不了"和"连不上"都算不通。
    ///
    /// <para>
    /// 不走 HttpClient：那条路会带上系统代理配置，而"代理坏了"恰恰是一种常见的"看起来像没网"。
    /// 这里要回答的是"这台机器能不能到公网"，直连更能回答它。
    /// </para>
    /// </summary>
    private static async Task<bool> ProbeTcpAsync(CancellationToken token)
    {
        foreach (var host in Hosts)
        {
            try
            {
                using var client = new TcpClient();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ProbeTimeout);

                await client.ConnectAsync(host, 443, timeout.Token).ConfigureAwait(false);

                if (client.Connected)
                    return true;
            }
            catch
            {
                // 这个域名不通，试下一个。
            }
        }

        return false;
    }
}
