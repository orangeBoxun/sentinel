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
/// 每秒几次往一个连不上的地址发请求。失败之后等一会儿再试（同一份内容 30 秒内不重发）。
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

    /// <summary>判断"这台机器现在有没有网" —— 用来把"你这边断了"和"服务商不给你用"分开。</summary>
    private readonly Connectivity _connectivity;

    /// <summary>用量统计。null = 不记（测试和某些宿主）。</summary>
    private readonly UsageStore? _usage;

    public ModelSupervisor(
        Func<AppConfig> config,
        ModelClient? client = null,
        Connectivity? connectivity = null,
        UsageStore? usage = null)
    {
        _config = config;
        _client = client ?? new ModelClient();
        _connectivity = connectivity ?? new Connectivity();
        _usage = usage;
    }

    /// <summary>
    /// 需要用户去处理的问题（钥匙不对 / 欠费 / 超额）。null 表示当前没有。
    ///
    /// 拿不到判定的原因 —— 非 null 表示"模型监督现在等于不存在"。界面按它弹那条每 30 秒一次的
    /// 全屏提醒（见 <see cref="ModelAlertPacing"/>）；**拿到判定一次就清掉**。
    ///
    /// <para>
    /// 任何"拿不到判定"的失败都会设上它（限流、欠费、5xx、连不上、太慢、本地没起来）——
    /// 那些看起来是不同的事，后果是一样的：那几条模型规则现在一个人也没在管。
    /// </para>
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

    /// <summary>链上还有没有能给出判定的模型。没有 = 停发新请求，等探测问活。</summary>
    private volatile bool _usable = true;

    public ModelVerdict Lookup(string ruleId, ContentSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        var signature = ChainSignature();

        lock (_gate)
        {
            // 一模一样的这份内容判过 —— 直接用，这种情况最常见（页面没变）。
            if (_verdicts.TryGetValue(Key(signature, ruleId, ContentFingerprint.Of(snapshot)), out var known))
                return known;

            // 这份内容没判过，但**同一个地址**刚判过：正文在动而已，接着用上一次的结论。
            if (RecentForAddress(signature, ruleId, snapshot.Url, now) is { } recent)
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
        var signature = ChainSignature();

        lock (_gate)
        {
            if (_verdicts.TryGetValue(Key(signature, ruleId, ContentFingerprint.Of(snapshot)), out var known))
                return new VerdictProvenance(known, "这份内容判过，直接沿用（没有再问一次）", null);

            var url = snapshot.Url;

            if (!string.IsNullOrEmpty(url)
                && _byAddress.TryGetValue(AddressKey(signature, ruleId, url), out var recent)
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
    private ModelVerdict? RecentForAddress(string signature, string ruleId, string? url, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        if (!_byAddress.TryGetValue(AddressKey(signature, ruleId, url), out var recent))
            return null;

        return now - recent.At <= VerdictLifetime ? recent.Verdict : null;
    }

    /// <summary>
    /// 现在到底通不通 —— **真去问一次**，问通了就把停摆状态清掉。
    ///
    /// <para>
    /// 为什么要这个：停摆状态原来**只在拿到一次判定时才清**，而判定只在有人打开命中规则的页面
    /// 时才发起。于是"昨天模型没起来"留下的那条状态会一直挂着 —— 提醒器一直响，而它早就好了。
    /// 状态必须跟随事实，不能等下一次有人踩到页面才更新。
    /// </para>
    /// <para>
    /// 只在**已经处于停摆**时才发请求（见开头那句 return）：正常运行时它一次都不发，不花钱。
    /// </para>
    /// <para>
    /// <b>它不便宜。</b>它发的是一次和真实判定同样大小的请求（真实禁令 + 满额正文），慢模型上
    /// 要跑一分钟才超时 —— 所以"多久探一次"是件要拿捏的事，在
    /// <see cref="ModelAlertPacing.SlowProbeEvery"/> 那边定的。
    /// </para>
    /// <para>
    /// <b>只有真拿到判定才算"好了"</b>（见下面那个 if）。这一条踩过：原来凡是<b>分类不出毛病</b>
    /// 的结果都算好了，于是服务端 5xx、或者模型回的格式看不懂（<c>NoAnswer</c>）也被读成"通了"，
    /// 状态一清、提醒全停 —— 而那两次探测什么也没证明。提醒只能因为<b>监督真的在跑</b>而停，
    /// 不能因为一次没学到东西的探测而停。
    /// </para>
    /// </summary>
    public async Task<bool> ProbeAsync()
    {
        if (BlockingIssue is null && BlockingKind == ModelTroubleKind.None)
            return true;   // 本来就没停摆，不用探

        if (Chain().Count == 0)
            return false;

        // 网断着就不去问了 —— 现在去问只会挂满一分钟，而它必然失败。
        if (BlockingKind == ModelTroubleKind.Offline
            && !await _connectivity.IsStableAsync().ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            // 探的是**那些标了"稳定"的**：它们是"每页必判"的保证，只要有一个还答不上来，
            // 提醒就不该停。没标"稳定"的不探 —— 它们本来就只是尽力而为，且探一次就是真花钱。
            var chain = Chain();
            var stable = chain.Where(p => p.Stable).ToList();
            var toProbe = stable.Count > 0 ? stable : chain;

            ModelProvider? failedAt = null;
            ModelCallResult? failure = null;

            foreach (var provider in toProbe)
            {
                if (string.IsNullOrWhiteSpace(provider.Endpoint) || string.IsNullOrWhiteSpace(provider.Model))
                {
                    failedAt ??= provider;
                    failure ??= new ModelCallResult(ModelCallOutcome.NeedsAttention,
                        Message: "这个服务商还没填地址和模型。");
                    continue;
                }

                try
                {
                    var result = await _client
                        .JudgeAsync(provider, ProbeInstruction(), ProbeSnapshot(), CancellationToken.None)
                        .ConfigureAwait(false);

                    _usage?.Record(provider.Id, result.PromptTokens, result.CompletionTokens,
                        result.Outcome == ModelCallOutcome.Answered);

                    if (result.Outcome == ModelCallOutcome.Answered)
                        continue;

                    failedAt ??= provider;
                    failure ??= result;
                }
                catch
                {
                    // 抛异常 = 这次没问到，按"还不通"算。
                    failedAt ??= provider;
                }
            }

            // 没拿到判定：不能算好了。**只有真拿到判定才算好了** —— 服务端 5xx、回来的东西
            // 看不懂、连不上，都只说明"这次没学到东西"，把它们读成"通了"就等于提醒因为一次
            // 没有信息的探测停了，而真实判定照样在失败。
            if (failure is not null)
            {
                var online = await OnlineIfItMattersAsync(failure.Outcome, failedAt?.Endpoint)
                    .ConfigureAwait(false);

                var trouble = ModelTrouble.Classify(failure.Outcome, failedAt?.Endpoint, online);

                lock (_gate)
                {
                    BlockingKind = trouble;
                    BlockingIssue = trouble == ModelTroubleKind.Offline
                        ? OfflineIssue
                        : failedAt is null
                            ? failure.Message ?? "模型监督现在拿不到判定。"
                            : $"「{failedAt.Name}」{failure.Message}";
                }

                return false;   // 接着（该吵的时候）提醒
            }

            lock (_gate)
            {
                BlockingKind = ModelTroubleKind.None;
                BlockingIssue = null;
                LastAnswered = DateTimeOffset.Now;
            }

            _usable = true;
            return true;
        }
        catch
        {
            return false;   // 探测本身炸了：当作"还不通"，不动状态
        }
    }

    /// <summary>
    /// 探测要问的<b>就是真实判定会问的那句话</b> —— 当前启用的那条模型规则写给自己看的禁令。
    ///
    /// <para>
    /// 这一条是"探测算不算数"的关键，而且踩过一次：原来探测问的是"这是一次连通性探测。照常回答即可。"
    /// —— 一句话，模型没什么可想。于是一个**网页要两分钟**的慢模型，探测却答得上来，探测就说
    /// "通了"，停摆状态被清掉、提醒全停 —— 而真实判定照样次次超时。用户看到的是"提醒一阵一阵的，
    /// 远远没有 30 秒一次"，根因在这里：<b>探测量的是另一个问题，比真实判定便宜得多</b>。
    /// </para>
    /// <para>
    /// 禁令之所以是主要的成本项：它是人写给自己的规矩，可能很长、很绕，思考型模型在它上面花的
    /// 推理时间远多于在一句"照常回答即可"上。所以探测必须带上它，否则测出来的快慢不作数。
    /// </para>
    /// </summary>
    private string ProbeInstruction()
    {
        foreach (var rule in _config().Targets.SelectMany(t => t.Rules))
        {
            // 和 RuleEngine 里发出去的那句是同一个来源：规则的 Patterns 就是"要禁止自己看什么"。
            if (rule.Enabled && rule.Match.Type == MatchKind.Model && rule.Match.Patterns.Count > 0)
                return string.Join("\n", rule.Match.Patterns);
        }

        // 一条模型规则都没有（比如界面上单独点"测试连接"）时，用一句普通的禁令顶上。
        return "禁止浏览与工作无关的娱乐内容。";
    }

    /// <summary>
    /// 探测用的一份内容 —— **和真实页面一样大，而且不是一句话重复 300 遍**。
    ///
    /// <para>
    /// 大小要对上：探测如果只发一句"在吗"，慢模型也会秒回，于是刚记下的"太慢"立刻被自己抹掉，
    /// 提醒一阵一阵的，人根本不知道该干什么。所以正文填到客户端真正会发出去的那个上限
    /// （<see cref="ModelClient.MaxContentChars"/>），这样"探测答得上来"就意味着"真实判定也答得上来"。
    /// </para>
    /// <para>
    /// 内容要换着写：一页重复的同一句话是最好压缩的输入，测出来的耗时比真实页面乐观。这里是一段
    /// 平平常常、说不上违规的页面文字（探测只看耗时不看结论），量出来的就是一个<b>普通</b>页面的代价
    /// —— 比最重的那一页乐观一点，这个代价认了：探测本身也会变成每两分钟一次的负担。
    /// </para>
    /// </summary>
    private static ContentSnapshot ProbeSnapshot()
    {
        var body = new StringBuilder();

        for (var i = 0; body.Length < ModelClient.MaxContentChars; i++)
            body.Append(ProbeLines[i % ProbeLines.Length]).Append(' ');

        return new ContentSnapshot("(探测)", "(探测)", body.ToString(), DateTimeOffset.Now);
    }

    /// <summary>探测正文用的句子。多写几句、错着用，别让整段变成"同一句话重复 N 遍"。</summary>
    private static readonly string[] ProbeLines =
    {
        "周五的例会挪到了下午三点，会议室换到二层东侧那间。",
        "服务器昨晚重启过一次，日志里留下七条告警，都是磁盘写入变慢。",
        "新的版本号是 3.14，升级文档里提到两个不兼容的改动。",
        "这张表统计了上个月的访问量，排在第一的仍然是文档首页。",
        "请把这段配置复制到另一台机器上，注意路径里的反斜杠要转义。",
        "有人反馈说搜索框在输入法打开的时候会丢字，暂时没能复现。",
        "索引重建之后，这条查询从两秒降到了三十毫秒，效果比预想的好。",
        "会议记录里还有三条待办没有分配负责人，明天再问一次。",
        "这段代码把同一个列表复制了三遍，改成一次遍历就够，但先别动它。",
        "天气预报说晚上有雨，记得把阳台上的东西收进来。",
        "缓存命中率这周掉到了七成，原因是有一批请求每次都带着随机参数。",
        "把这个文件另存一份，改完之后再和原来的对比一次。",
    };

    public void Request(string ruleId, string instruction, ContentSnapshot snapshot)
    {
        // **已经知道它坏了（没起来、被限流、或者慢到拿不到判定）就不再发新的判定请求。**
        //
        // 对着一个慢模型，每页都发一次等于让这台机器一直满负荷转，而判定要一分钟以上才回来、
        // 回来也早没用了。对着一个限流的服务商，每页都发一次只会让限流更久。
        // 这时候该做的是反复提醒人去换模型，不是接着烧机器。
        // 恢复靠探测（见 ProbeAsync）：它每 30 秒到 2 分钟真问一次"现在通不通、够不够快"。
        // 一个都判不了才停发（别再烧机器）；只要链上还有一个能答，就接着判 ——
        // 某个稳定供应商挂了会另行走提醒，不该让整个监督一起停摆。
        if (!_usable)
            return;

        var fingerprint = ContentFingerprint.Of(snapshot);
        var signature = ChainSignature();
        var key = Key(signature, ruleId, fingerprint);
        var now = DateTimeOffset.Now;

        lock (_gate)
        {
            if (_verdicts.ContainsKey(key) || _inFlight.Contains(key))
                return;

            // 同一个地址上刚判过 —— 正文在动而已。引擎那边通常不会走到这里（查得到就不会问），
            // 这一道是给"查和问之间刚好判完"那种时序留的。
            if (RecentForAddress(signature, ruleId, snapshot.Url, now) is not null)
                return;

            if (_lastAttempt.TryGetValue(key, out var last) && now - last < RetryAfterNoAnswer)
            {
                // 同一份内容刚刚判过又没拿到结果：等一会儿再试。走到这里说明当前没在停摆
                // （上面那道 return 拦掉了停摆的情况），所以这只是防止同一页被反复重发。
                return;
            }

            _lastAttempt[key] = now;
            _inFlight.Add(key);
        }

        _ = Task.Run(() => JudgeAsync(signature, key, ruleId, instruction, snapshot));
    }

    private async Task JudgeAsync(string signature, string key, string ruleId, string instruction, ContentSnapshot snapshot)
    {
        try
        {
            // 现取：用户在界面上改完服务商，下一次判定就该用改完的那条链。
            var chain = Chain();

            if (chain.Count == 0)
            {
                // 没启用任何服务商 —— 模型监督等于关着。规则引擎会因为查不到判定而放行，
                // 这正是"关掉监督"该有的样子。
                return;
            }

            // 没网就别发这一次了：它要么立刻失败、要么挂满 60 秒，两种都白等，后果一样。
            // 这条判断自己有 30 秒缓存，所以不是每个页面都探一次。
            // （链上有本机/内网的服务就不看公网通不通 —— 那一个不需要公网。）
            var needsInternet = chain.Any(p => !ModelTrouble.IsLocal(p.Endpoint));

            if (needsInternet && !await _connectivity.IsStableAsync().ConfigureAwait(false))
            {
                BlockingKind = ModelTroubleKind.Offline;
                BlockingIssue = OfflineIssue;
                return;
            }

            var run = await RunChainAsync(instruction, snapshot).ConfigureAwait(false);

            if (run.Answer is { Outcome: ModelCallOutcome.Answered } answer)
            {
                // 有人判出来了：判定照存，请求也接着发（链上还有活的模型）。
                _usable = true;
                LastAnswered = DateTimeOffset.Now;
                Store(signature, key, ruleId, snapshot.Url, answer.Violates
                    ? new ModelVerdict(ModelVerdictKind.Blocked, answer.Reason)
                    : ModelVerdict.Allowed);
            }
            else
            {
                // 整条链都没给出判定：停发新请求（别再烧机器），等探测把它们问活。
                _usable = false;
            }

            // **告警只认"稳定"的那几个**：没标的挂了就跳过（免费额度被抢光是常事，
            // 为它一直弹全屏只会把人吵到关掉提醒）；**整条链都没答上来**也算停摆。
            var (failedAt, failure) = run.StableFailure is not null
                ? (run.StableFailedAt, run.StableFailure)
                : run.Answer is null
                    ? (run.FailedAt, run.Failure)
                    : (null, null);

            if (failure is null)
            {
                if (run.Answer is not null)
                {
                    BlockingIssue = null;
                    BlockingKind = ModelTroubleKind.None;
                }

                return;
            }

            // 分类只决定提醒里那句话怎么说（见 ModelTrouble），节奏是同一个
            // （见 ModelAlertPacing）。**整台机器没网**的时候不该吵（网站自己也打不开），
            // 所以要先问一句"现在有网吗"（见 Connectivity）。
            var online = await OnlineIfItMattersAsync(failure.Outcome, failedAt?.Endpoint)
                .ConfigureAwait(false);

            BlockingKind = ModelTrouble.Classify(failure.Outcome, failedAt?.Endpoint, online);
            BlockingIssue = BlockingKind == ModelTroubleKind.Offline
                ? OfflineIssue
                : failedAt is null
                    ? failure.Message ?? "模型监督现在拿不到判定。"
                    : $"「{failedAt.Name}」{failure.Message}";
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

    /// <summary>
    /// 网断着的时候那句说明。它<b>不会</b>被弹成全屏（见 <see cref="ModelAlertPacing"/>）——
    /// 只出现在「大语言模型」页的状态行里，让人知道监督为什么不动。
    /// </summary>
    private const string OfflineIssue = "网络看起来是断的 —— 模型判定发不出去。等网回来之后会自动恢复。";

    /// <summary>
    /// 这次失败，该算"你这边网络的事"还是"服务商的事"。
    ///
    /// <para>
    /// <b>时间尺度要对上。</b>最初这里只问"能不能建一条 TCP 连接"，结果网一抖，模型请求照样挂满
    /// 60 秒，而那个检查说"有网"，于是被判成"模型太慢"、继续弹全屏 —— 用户看到的就是又开始了。
    /// 现在的标准是：连续三趟 HTTPS 请求都完成，**并且**那个服务商自己的地址也能换回响应。
    /// 后者是关键：路通、但换不回服务商的响应，才说明慢的是它的推理。
    /// </para>
    /// </summary>
    private async Task<bool> OnlineIfItMattersAsync(ModelCallOutcome outcome, string? endpoint)
    {
        if (outcome == ModelCallOutcome.Timeout)
        {
            return await _connectivity.IsStableAsync().ConfigureAwait(false)
                && await _connectivity.IsReachableAsync(endpoint ?? "").ConfigureAwait(false);
        }

        if (outcome == ModelCallOutcome.Unreachable && !ModelTrouble.IsLocal(endpoint))
            return await _connectivity.IsStableAsync().ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// 按顺序排好的判断链。空 = 模型监督关着（不是"回退到第一个"）。
    /// </summary>
    private IReadOnlyList<ModelProvider> Chain()
    {
        var config = _config();
        var providers = new List<ModelProvider>();

        if (config.ModelChain is { Count: > 0 })
        {
            // 链里指向不存在的服务商会被跳过 —— 删掉一个服务商不该让整条链失效。
            foreach (var id in config.ModelChain)
            {
                if (config.Models.FirstOrDefault(m => m.Id == id) is { } provider)
                    providers.Add(provider);
            }

            return providers;
        }

        // 老配置（没有链）：就是一个模型。
        if (!string.IsNullOrWhiteSpace(config.ActiveModelId)
            && config.Models.FirstOrDefault(m => m.Id == config.ActiveModelId) is { } single)
        {
            providers.Add(single);
        }

        return providers;
    }

    /// <summary>
    /// 链的"身份"：换了模型、地址、顺序都会变，它参与缓存键。
    ///
    /// <para>
    /// 没有这一层会出现一个很难查的现象：一个页面被小模型判过"不违规"之后，那份结论会一直
    /// 缓存着 —— 你换成更准的模型也没用，因为根本不会再去问，除非页面内容自己变了
    /// （刷新一次）。而"换模型"本来就该让旧结论作废。
    /// </para>
    /// </summary>
    private string ChainSignature() =>
        string.Join(">", Chain().Select(p => $"{p.Id}:{p.Model}:{p.Endpoint}"));

    /// <summary>
    /// 诊断用：**按判断链逐个真问一次**，和监控走同一条路（含"任一说违规就停"）。
    ///
    /// <para>
    /// 自检必须问监控会问的那条链 —— 只问第一个模型的话，链上第二个（往往才是判得准的那个）
    /// 永远看不到，人就会以为"我明明配了两个，怎么只问了一个"。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<(ModelProvider Provider, ModelCallResult Result)>> DiagnoseAsync(
        string instruction, ContentSnapshot snapshot, CancellationToken token)
    {
        var asked = new List<(ModelProvider, ModelCallResult)>();

        foreach (var provider in Chain())
        {
            ModelCallResult result;

            try
            {
                result = await _client.JudgeAsync(provider, instruction, snapshot, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new ModelCallResult(ModelCallOutcome.NoAnswer, Message: $"抛异常：{ex.Message}");
            }

            _usage?.Record(provider.Id, result.PromptTokens, result.CompletionTokens,
                result.Outcome == ModelCallOutcome.Answered);

            asked.Add((provider, result));

            // 和监控一样：有人说了违规就地停，后面的不再花钱。
            if (result.Outcome == ModelCallOutcome.Answered && result.Violates)
                break;
        }

        return asked;
    }

    /// <summary>
    /// 跑一遍链的结果。失败分两种：**稳定供应商**失败（要吵）和其他失败（跳过就好）。
    /// </summary>
    private sealed record ChainRun(
        ModelCallResult? Answer,
        ModelProvider? AnsweredBy,
        ModelProvider? FailedAt,
        ModelCallResult? Failure,
        ModelProvider? StableFailedAt,
        ModelCallResult? StableFailure);

    /// <summary>
    /// 按链一路问下去，**任一模型说违规就停在那里**；说不违规就交给下一个；
    /// 全都说不违规才算放行。全都答不上来才算失败（带第一个失败的原因）。
    /// </summary>
    private async Task<ChainRun> RunChainAsync(string instruction, ContentSnapshot snapshot)
    {
        var answered = 0;
        ModelCallResult? answer = null;
        ModelProvider? answeredBy = null;
        ModelProvider? failedAt = null;
        ModelCallResult? failure = null;
        ModelProvider? stableFailedAt = null;
        ModelCallResult? stableFailure = null;

        foreach (var provider in Chain())
        {
            if (string.IsNullOrWhiteSpace(provider.Endpoint) || string.IsNullOrWhiteSpace(provider.Model))
            {
                failedAt ??= provider;
                failure ??= new ModelCallResult(ModelCallOutcome.NeedsAttention,
                    Message: "这个服务商还没填地址和模型。");
                continue;
            }

            ModelCallResult result;

            try
            {
                result = await _client
                    .JudgeAsync(provider, instruction, snapshot, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                _usage?.Record(provider.Id, null, null, answered: false);
                continue;   // 抛异常 = 它这次没意见
            }

            _usage?.Record(provider.Id, result.PromptTokens, result.CompletionTokens,
                result.Outcome == ModelCallOutcome.Answered);

            if (result.Outcome == ModelCallOutcome.Answered)
            {
                answered++;
                answer = result;
                answeredBy = provider;

                if (result.Violates)
                    break;   // 有人说了违规 —— 后面的模型不用再花钱
            }
            else
            {
                // 失败只算"它没意见"，继续问下一个：链上有别的模型能判，监督就还活着。
                failedAt ??= provider;
                failure ??= result;

                // 但标了"稳定提供服务"的那个不一样 —— 它挂了就是"每页必判"的保证破了，
                // 要反复提醒（哪怕这条链上还有别人在判）。
                if (provider.Stable)
                {
                    stableFailedAt ??= provider;
                    stableFailure ??= result;
                }
            }
        }

        return new ChainRun(answer, answeredBy, failedAt, failure, stableFailedAt, stableFailure);
    }

    private void Store(string signature, string key, string ruleId, string? url, ModelVerdict verdict)
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

            var addressKey = AddressKey(signature, ruleId, url);

            if (!_byAddress.ContainsKey(addressKey))
                _addressOrder.Enqueue(addressKey);

            _byAddress[addressKey] = (verdict, now);

            while (_addressOrder.Count > MaxEntries)
                _byAddress.Remove(_addressOrder.Dequeue());
        }
    }

    private static string Key(string signature, string ruleId, string fingerprint) =>
        signature + " " + ruleId + " " + fingerprint;

    private static string AddressKey(string signature, string ruleId, string url) =>
        signature + " " + ruleId + " url " + url;
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
