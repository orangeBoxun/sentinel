using System.Security.Cryptography;
using System.Text;
using Sentinel.Core.Models;

namespace Sentinel.Core.Model;

public enum ModelVerdictKind
{
    /// <summary>还没判过。</summary>
    Unknown,

    Allowed,
    Blocked,
}

public sealed record ModelVerdict(ModelVerdictKind Kind, string? Reason)
{
    public static readonly ModelVerdict Unknown = new(ModelVerdictKind.Unknown, null);
    public static readonly ModelVerdict Allowed = new(ModelVerdictKind.Allowed, null);

    public bool IsBlocked => Kind == ModelVerdictKind.Blocked;
}

/// <summary>
/// 这条判定是从哪儿来的。<paramref name="How"/> 是一句给人看的话，<paramref name="Age"/> 是
/// "多久以前判的"（能算出来才有）。
/// </summary>
public sealed record VerdictProvenance(ModelVerdict Verdict, string How, TimeSpan? Age);

/// <summary>规则引擎看模型判定的那一面。同步查，异步判。</summary>
public interface IModelSupervisor
{
    /// <summary>这条路规则 + 这份内容，判过没有。</summary>
    ModelVerdict Lookup(string ruleId, ContentSnapshot snapshot);

    /// <summary>让它在后台判一次。同一份内容只会发起一次，失败后也有退避。</summary>
    void Request(string ruleId, string instruction, ContentSnapshot snapshot);

    /// <summary>
    /// 判定结果变过几次。
    ///
    /// <para>
    /// 监控循环靠它认出"异步判定回来了"。没有这个的话会有一个很隐蔽的错：
    /// 第一轮问引擎时判定还没回来，于是记下"放行"；从第二轮起内容没变，循环就直接用那条
    /// 缓存 —— **模型说违规的那句话永远没人去读**，只要页面内容不再变。
    /// </para>
    /// </summary>
    int VerdictsVersion { get; }
}

/// <summary>
/// 模型判定的调度：缓存、去重、退避、以及"要不要把用户叫起来"。
///
/// <para>
/// <b>为什么必须是异步的。</b>监控循环每个 tick 是同步的，而一次模型调用要几秒 —— 直接在循环里
/// 等，会把最小化、关窗这些动作一起卡住。所以这里发起之后立刻返回，判定结果下次 tick 才用得上。
/// 代价是第一次命中会晚几秒，换来的是循环永远不堵。
/// </para>
/// <para>
/// <b>为什么要有退避。</b>没网的时候每份内容都判不出来，而循环每秒跑好几次 —— 不退避就是
/// 每秒几次往一个连不上的地址发请求。失败之后按类别等一会儿再试。
/// </para>
/// </summary>
public sealed class ModelSupervisor : IModelSupervisor
{
    /// <summary>判过的东西留多少份。按内容指纹存，页面一换就是一个新条目。</summary>
    private const int MaxEntries = 500;

    /// <summary>
    /// 同一个**地址**上的判定，这么久之内接着用，不再重新判。
    ///
    /// <para>
    /// 这一条是花钱的地方。内容指纹里含正文，而 Codex、豆包这类窗口的正文**一直在变**
    /// （流式输出、计时器、动画）—— 每变一次就是一个新指纹，于是每一轮都发一次请求，
    /// 一秒一变就是一分钟六十次。人盯着的那一个窗口尤其明显。
    /// </para>
    /// <para>
    /// 页面自己在动，不等于结论变了。所以：同一个地址上，上一次的判定在 60 秒内继续生效。
    /// 换了地址（翻页、点进新视频、新开的标签）算新问题，立刻判。
    /// </para>
    /// <para>
    /// 代价说清楚：在一个地址不变、内容乱跳的页面里（推荐流、聊天窗），最坏情况下有 60 秒
    /// 是按上一次的结论走的。按"帮自己别分心"这个用途，这个代价换掉的是"每分钟几十次请求"，
    /// 划算。想更跟手就把这个数调小，想更省就调大 —— 改这一个常量就行。
    /// </para>
    /// </summary>
    private static readonly TimeSpan VerdictLifetime = TimeSpan.FromSeconds(60);

    /// <summary>判不出来之后多久再试。它可能是临时的，也可能不是。</summary>
    private static readonly TimeSpan RetryAfterNoAnswer = TimeSpan.FromSeconds(30);

    /// <summary>需要用户去处理的问题：在他动手之前，快慢都没意义。</summary>
    private static readonly TimeSpan RetryAfterAttention = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, ModelVerdict> _verdicts = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttempt = new();
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _inFlight = new();

    /// <summary>按（规则 + 地址）记着最后一次判定，用来在上面的复用窗口里接着用。</summary>
    private readonly Dictionary<string, (ModelVerdict Verdict, DateTimeOffset At)> _byAddress = new();
    private readonly Queue<string> _addressOrder = new();

    private readonly ModelClient _client;

    /// <summary>每次判定现取一次配置 —— 用户在界面上换了服务商或者改了 key，下一次就该用新的。</summary>
    private readonly Func<AppConfig> _config;

    public ModelSupervisor(Func<AppConfig> config, ModelClient? client = null)
    {
        _config = config;
        _client = client ?? new ModelClient();
    }

    /// <summary>
    /// 需要用户去处理的问题（钥匙不对 / 欠费 / 超额）。null 表示当前没有。
    ///
    /// 界面按这个决定要不要弹那条每 5 分钟一次的全屏提醒。判定成功一次就清掉。
    /// </summary>
    public string? BlockingIssue { get; private set; }

    /// <summary>
    /// 停摆是哪一类 —— 界面靠它决定"多勤地提醒"。见 <see cref="ModelTroubleKind"/>。
    /// </summary>
    public ModelTroubleKind BlockingKind { get; private set; }

    /// <summary>最近一次成功判定的时间。用来判断"通了没有"。</summary>
    public DateTimeOffset? LastAnswered { get; private set; }

    /// <summary>判定结果变过几次。监控循环靠它认出"异步判定回来了"，见接口上的说明。</summary>
    public int VerdictsVersion
    {
        get
        {
            lock (_gate)
                return _version;
        }
    }

    private int _version;

    public ModelVerdict Lookup(string ruleId, ContentSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;

        lock (_gate)
        {
            // 一模一样的这份内容判过 —— 直接用，这种情况最常见（页面没变）。
            if (_verdicts.TryGetValue(Key(ruleId, ContentFingerprint.Of(snapshot)), out var known))
                return known;

            // 这份内容没判过，但**同一个地址**刚判过：正文在动而已，接着用上一次的结论。
            if (RecentForAddress(ruleId, snapshot.Url, now) is { } recent)
                return recent;

            return ModelVerdict.Unknown;
        }
    }

    /// <summary>
    /// 这条判定是从哪儿来的 —— 给「诊断」页用的。
    ///
    /// <para>
    /// 监控是<b>异步</b>判的，而且会缓存：同一份内容不重问，<b>同一个地址 60 秒内也沿用上一次的
    /// 结论</b>（正文在动而已）。于是诊断里会出现"模型这次说不违规、监控手上却是违规" ——
    /// 那不是链断了，是两件事凑在一起：模型对同一个问题的两次回答可以不一样，而监控手上那条是
    /// 另一个时刻留下的。说不清这一点，人只会觉得这套东西在乱判。
    /// </para>
    /// </summary>
    public VerdictProvenance Explain(string ruleId, ContentSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;

        lock (_gate)
        {
            if (_verdicts.TryGetValue(Key(ruleId, ContentFingerprint.Of(snapshot)), out var known))
                return new VerdictProvenance(known, "这份内容判过，直接沿用（没有再问一次）", null);

            var url = snapshot.Url;

            if (!string.IsNullOrEmpty(url)
                && _byAddress.TryGetValue(AddressKey(ruleId, url), out var recent)
                && now - recent.At <= VerdictLifetime)
            {
                return new VerdictProvenance(
                    recent.Verdict,
                    "同一个地址上一次的结论还没过期，沿用了它 —— **内容可能已经变了**",
                    now - recent.At);
            }

            return new VerdictProvenance(ModelVerdict.Unknown, "还没判过（下一轮会问）", null);
        }
    }

    /// <summary>同一个地址上、还没过期的那个判定。</summary>
    private ModelVerdict? RecentForAddress(string ruleId, string? url, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        if (!_byAddress.TryGetValue(AddressKey(ruleId, url), out var recent))
            return null;

        return now - recent.At <= VerdictLifetime ? recent.Verdict : null;
    }

    public void Request(string ruleId, string instruction, ContentSnapshot snapshot)
    {
        var fingerprint = ContentFingerprint.Of(snapshot);
        var key = Key(ruleId, fingerprint);
        var now = DateTimeOffset.Now;

        lock (_gate)
        {
            if (_verdicts.ContainsKey(key) || _inFlight.Contains(key))
                return;

            // 同一个地址上刚判过 —— 正文在动而已。引擎那边通常不会走到这里（查得到就不会问），
            // 这一道是给"查和问之间刚好判完"那种时序留的。
            if (RecentForAddress(ruleId, snapshot.Url, now) is not null)
                return;

            if (_lastAttempt.TryGetValue(key, out var last))
            {
                // 本地服务没起来那种，退避要短：它最好办（拉起来就行），早点试通早点恢复。
                var wait = BlockingKind == ModelTroubleKind.LocalUnreachable
                    ? RetryAfterNoAnswer
                    : BlockingIssue is null ? RetryAfterNoAnswer : RetryAfterAttention;
                if (now - last < wait)
                    return;
            }

            _lastAttempt[key] = now;
            _inFlight.Add(key);
        }

        _ = Task.Run(() => JudgeAsync(key, ruleId, instruction, snapshot));
    }

    private async Task JudgeAsync(string key, string ruleId, string instruction, ContentSnapshot snapshot)
    {
        try
        {
            // 现取：用户在界面上改完服务商，下一次判定就该用改完的那个。
            var provider = ActiveProvider();

            if (provider is null)
            {
                // 没启用任何服务商 —— 模型监督等于关着。规则引擎会因为查不到判定而放行，
                // 这正是"关掉监督"该有的样子。
                return;
            }

            var result = await _client.JudgeAsync(provider, instruction, snapshot, CancellationToken.None)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case ModelCallOutcome.Answered:
                    BlockingIssue = null;
                    BlockingKind = ModelTroubleKind.None;
                    LastAnswered = DateTimeOffset.Now;
                    Store(key, ruleId, snapshot.Url, result.Violates
                        ? new ModelVerdict(ModelVerdictKind.Blocked, result.Reason)
                        : ModelVerdict.Allowed);
                    break;

                case ModelCallOutcome.NeedsAttention:
                    BlockingIssue = result.Message;
                    BlockingKind = ModelTroubleKind.NeedsAttention;
                    break;

                case ModelCallOutcome.Unreachable:
                    // 连不上 —— 但**要不要吵取决于那是谁的服务**：本机/内网的没起来
                    // 是要吵的（网站照样打得开，只是没人判了），公网的按"没网"处理，不吵。
                    BlockingKind = ModelTrouble.Classify(result.Outcome, provider.Endpoint);
                    BlockingIssue = BlockingKind == ModelTroubleKind.None ? null : result.Message;
                    break;

                case ModelCallOutcome.NoAnswer:
                    // 没网、超时、服务端抽风：不管，也不提醒。记下判定为空，等退避过去再试。
                    break;
            }
        }
        catch
        {
            // 判定线程里的异常绝不能冒出去 —— 它会把进程带走，而这只是一种"这次没算"。
        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(key);
        }
    }

    private ModelProvider? ActiveProvider()
    {
        var config = _config();
        var id = config.ActiveModelId;

        if (string.IsNullOrWhiteSpace(id))
            return null;   // 没启用 = 没在监督。这是"停用"的定义，不是回退到第一个。

        return config.Models.FirstOrDefault(m => m.Id == id);
    }

    private void Store(string key, string ruleId, string? url, ModelVerdict verdict)
    {
        var now = DateTimeOffset.Now;

        lock (_gate)
        {
            if (!_verdicts.ContainsKey(key))
                _order.Enqueue(key);

            _verdicts[key] = verdict;

            // 判定结果动了 —— 循环那边用这个数字知道"该再看一眼了"。
            _version++;

            while (_order.Count > MaxEntries)
            {
                var oldest = _order.Dequeue();
                _verdicts.Remove(oldest);
                _lastAttempt.Remove(oldest);
            }

            // 按地址再记一份，供上面那条复用规则查。地址为空的（读不到网址的窗口）不记 ——
            // 那种情况下"同一个地址"根本没有意义，只能按内容指纹走。
            if (string.IsNullOrEmpty(url))
                return;

            var addressKey = AddressKey(ruleId, url);

            if (!_byAddress.ContainsKey(addressKey))
                _addressOrder.Enqueue(addressKey);

            _byAddress[addressKey] = (verdict, now);

            while (_addressOrder.Count > MaxEntries)
                _byAddress.Remove(_addressOrder.Dequeue());
        }
    }

    private static string Key(string ruleId, string fingerprint) => ruleId + "\u0000" + fingerprint;

    private static string AddressKey(string ruleId, string url) => ruleId + "\u0000url\u0000" + url;
}

/// <summary>
/// 一份内容认不认得出来是同一份。模型判定按它缓存 —— 页面没变就不该重复花钱。
/// </summary>
public static class ContentFingerprint
{
    public static string Of(ContentSnapshot snapshot)
    {
        var text = $"{snapshot.Url}\n{snapshot.Title}\n{snapshot.BodyText}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));

        // 前 16 个十六进制字符（64 位）足够区分同一次运行里的页面，也不用把整串哈希塞进字典键。
        return Convert.ToHexString(hash)[..16];
    }
}
