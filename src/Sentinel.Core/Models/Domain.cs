namespace Sentinel.Core.Models;

/// <summary>A window discovered by the enumerator.</summary>
/// <param name="IsExposed">
/// 有一块真的露在屏幕上吗（没被别的窗口完全盖住）。
///
/// <para>
/// 默认 true 是为了让不关心这件事的调用方（测试里的假窗口）保持原样；真实的枚举器总会给出
/// 算出来的值。
/// </para>
/// </param>
public sealed record WindowRef(
    int ProcessId,
    string ProcessName,
    string MainWindowTitle,
    string ExecutablePath,
    long NativeHandle,
    bool IsMinimized = false,
    bool IsForeground = false,
    bool IsExposed = true);

/// <summary>A single read of a target window's content.</summary>
public sealed record ContentSnapshot(
    string? Url,
    string? Title,
    string? BodyText,
    DateTimeOffset Timestamp);

public sealed class ReaderOptions
{
    public bool ScanUrl { get; set; } = true;
    public bool ScanTitle { get; set; } = true;
    public bool ScanBody { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public int MaxBodyChars { get; set; } = 4096;
}

public sealed class Target
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Process names (no extension) this target matches.</summary>
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>Optional regex the window title must also match.</summary>
    public string? WindowTitlePattern { get; set; }

    /// <summary>Reserved; v1 matching is ProcessNames + WindowTitlePattern.</summary>
    public string? MatchMode { get; set; }

    public bool Enabled { get; set; } = true;
    public ReaderOptions Reader { get; set; } = new();
    public List<Rule> Rules { get; set; } = new();

    /// <summary>UI automation peers use ToString() for an item's accessible name.</summary>
    public override string ToString() => DisplayName;
}

public sealed class MatchSpec
{
    /// <summary>Fields to match against. Empty defaults to Url + Title.</summary>
    public List<MatchField> Fields { get; set; } = new();

    public MatchKind Type { get; set; } = MatchKind.Keyword;
    public List<string> Patterns { get; set; } = new();
    public bool CaseSensitive { get; set; }
}

public sealed class Rule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? ScheduleId { get; set; }
    public MatchSpec Match { get; set; } = new();
    public string PolicyId { get; set; } = "";

    /// <summary>
    /// Optional line shown when this rule fires — written by the user, for the user. A reminder you
    /// wrote yourself while calm lands harder than any wording the program could pick.
    /// </summary>
    public string? Reminder { get; set; }

    /// <summary>Higher priority is evaluated first.</summary>
    public int Priority { get; set; }

    /// <summary>
    /// The bounded exception to "switching monitoring off needs a token", for this rule.
    ///
    /// <para>
    /// Per rule, not per target. The urge this exists for is for one specific thing —「这一条先放过我，
    /// 我今天就得看这个页面」—— 而不是"整个应用都别管我了"。挂在目标上等于把该应用下所有规则一起
    /// 关掉，额度也一起花掉，那就不是"小小的例外"了。
    /// </para>
    /// </summary>
    public PauseOptions Pause { get; set; } = new();

    public override string ToString() => Name;
}

public sealed class Policy
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ActionType> Actions { get; set; } = new();

    /// <summary>Seconds to wait (notifying only) before enforcing. 0 = enforce immediately.</summary>
    public int GraceSeconds { get; set; }

    /// <summary>Seconds to wait between successive enforcements. 0 = no cooldown.</summary>
    public int CooldownSeconds { get; set; }

    /// <summary>Consecutive hits required before reacting. 1 = immediate.</summary>
    public int DebounceCount { get; set; } = 1;

    /// <summary>
    /// While the offending window stays in the foreground, repeat the policy every this many seconds.
    /// 0 = off, which is the behaviour without it: report a page once and stay quiet.
    /// </summary>
    public int RepeatSeconds { get; set; }

    /// <summary>Only meaningful when the policy's actions include Notify.</summary>
    public AlertStyle AlertStyle { get; set; } = AlertStyle.Balloon;

    public override string ToString() => Name;
}

/// <summary>A time-of-day window, "HH:mm" (supports overnight e.g. 22:00-06:00).</summary>
public sealed record TimeWindow(string Start, string End);

public sealed class Schedule
{
    public string Id { get; set; } = "";

    /// <summary>Days this schedule applies. Empty = every day.</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();

    /// <summary>Time windows. Empty = all day (on the selected days).</summary>
    public List<TimeWindow> TimeWindows { get; set; } = new();

    public override string ToString() => Id;
}

public sealed class ProtectionConfig
{
    public bool Enabled { get; set; }
    public ProtectionMode Mode { get; set; } = ProtectionMode.None;
    public string? SecretHash { get; set; }
    public int DelaySeconds { get; set; }
}

public sealed class LoggingConfig
{
    public string LogDirectory { get; set; } = @"%ProgramData%\Sentinel\logs";
    public bool LogBodyText { get; set; }
    public bool LogUrlHash { get; set; } = true;
}

public sealed class UiConfig
{
    public string Locale { get; set; } = "zh-CN";

    /// <summary>Dry-run: log but never execute actions.</summary>
    public bool DryRun { get; set; }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<Target> Targets { get; set; } = new();
    public List<Policy> Policies { get; set; } = new();
    public List<Schedule> Schedules { get; set; } = new();

    /// <summary>大模型服务商。得有至少一个，而且要指到它，「模型监督」那种规则才判得动。</summary>
    public List<ModelProvider> Models { get; set; } = new();

    /// <summary>当前启用的服务商。换一个不算放宽 —— 那是配置，不是放宽限制。</summary>
    public string? ActiveModelId { get; set; }

    /// <summary>
    /// 判断的责任链：按顺序排的服务商 id，**任一模型说违规就算违规**，全都说不违规才放行。
    ///
    /// <para>
    /// 为什么要有它：小模型遇到模糊页面倾向"不违规"（尤其禁令里写着"要有确切把握才拦"的时候），
    /// 大模型判得准、却贵。便宜的放前面、贵的放后面 —— 便宜的说违规就地拦下，只有它说不违规时
    /// 才去问贵的。省钱的同时，判错的代价只落在"便宜的那个多拦了"这一头。
    /// </para>
    /// <para>
    /// 空 = 退回单模型行为（用 <see cref="ActiveModelId"/>）。链里指向不存在的服务商会被跳过，
    /// 所以删掉一个服务商不会让整条链失效。
    /// </para>
    /// <para>
    /// 它也是**判定缓存的一部分**：换链 / 换模型 / 改地址之后，旧结论不能再沿用 ——
    /// 否则"换成更准的模型"这件事永远不会生效，除非页面内容自己变了。
    /// </para>
    /// </summary>
    public List<string> ModelChain { get; set; } = new();

    public ProtectionConfig Protection { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
}

/// <summary>
/// 一个能用的大模型端点。
///
/// <para>
/// <b>API key 是明文存在配置文件里的，这是有意的。</b>那个文件本来就只有管理员能写，而坐在
/// 这台机器前的人就是钥匙的主人 —— 加密它只会变成"把密码也贴在旁边"，多一层看起来像防护、
/// 实际不防任何人的东西。
/// </para>
/// </summary>
public sealed class ModelProvider
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ApiFormat Format { get; set; } = ApiFormat.OpenAi;

    /// <summary>
    /// 完整的请求地址（含路径）。
    ///
    /// 各家路径不一样（<c>/chat/completions</c>、<c>/responses</c>、<c>/v1/messages</c>），
    /// 而且中转服务常常自己改路径 —— 猜一个拼上去，错了只会得到一个 404，还得回来猜为什么。
    /// 所以这里要的是整条 URL，界面上按格式给几个例子。
    /// </summary>
    public string Endpoint { get; set; } = "";

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>自己看的备注：这个 key 是谁的、什么时候到期之类。不参与任何判断。</summary>
    public string Note { get; set; } = "";

    /// <summary>控制台/官网地址。填了只为了让"key 不够用了"的时候有个地方点。</summary>
    public string Website { get; set; } = "";

    /// <summary>
    /// 这个服务商<b>保证能提供判断</b>。判断链上至少要有一个。
    ///
    /// <para>
    /// 它决定两件事，而且只决定这两件事：
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>连不上时要反复全屏提醒。</b>没标它的供应商连不上只是跳过（免费额度被抢光是常事，
    /// 为它一直弹全屏只会把人吵到关掉提醒）。标了它就连不上 = 你承诺的"每页必判"破了，
    /// 那必须让人知道。
    /// </description></item>
    /// <item><description>
    /// <b>没解锁就取消不掉。</b>取消它等于放弃那条保证，是放宽 —— 和"从链里删掉一个供应商"
    /// 同一类。
    /// </description></item>
    /// </list>
    /// </summary>
    public bool Stable { get; set; }

    /// <summary>
    /// 取模型列表用哪个地址。留空就按请求地址推一个（域名 + <c>/v1/models</c>）。
    ///
    /// <para>
    /// 之所以做成可填的：列表**不在**请求那条路径下，而且各家的位置不一样。实测 DeepSeek 的
    /// <c>/v1/models</c> 和 <c>/models</c> 都是 200，而 <c>/anthropic/models</c> 是 404 —— 也就是说
    /// 光看请求地址（<c>https://api.deepseek.com/anthropic</c>）推不出列表在哪。推不出来就让人填。
    /// </para>
    /// </summary>
    /// <summary>
    /// 失焦时要不要按上游格式自动补全请求地址。
    ///
    /// <para>
    /// 默认开 —— 配服务商时最常见的错就是"地址只写了一半"（base 后面那段路径没加，于是 404）。
    /// 但**有些服务商的路径不长标准那样** ✗：比如智谱的
    /// <c>https://open.bigmodel.cn/api/paas/v4</c>，正确写法是接 <c>/chat/completions</c>，
    /// 而"标准补全"会塞成 <c>/v4/v1/chat/completions</c> —— 补错了比不补更气人。
    /// 所以给一个明确的开关：关掉之后那个框里的东西**原样使用，一个字符都不动**。
    /// </para>
    /// </summary>
    public bool AutoCompleteEndpoint { get; set; } = true;

    public string ModelListEndpoint { get; set; } = "";

    public override string ToString() => Name;
}
