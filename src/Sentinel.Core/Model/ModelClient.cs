using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.Core.Models;

namespace Sentinel.Core.Model;

/// <summary>一次判定的结果，分三类 —— 因为三类该做的事完全不同。</summary>
public enum ModelCallOutcome
{
    /// <summary>拿到判决了。</summary>
    Answered,

    /// <summary>
    /// 没拿到：服务端 5xx、或者回来的东西看不懂（没按要求的格式回）。
    ///
    /// <para>
    /// 和 <see cref="Unreachable"/> 分开，是因为<b>话不一样</b>（一个说"服务端出错了"，
    /// 一个说"连不上"）—— 但<b>后果和该做的事是一样的</b>：没人判了，去换一个能用的。
    /// 所以它一样会提醒（见 <see cref="ModelTrouble"/>）。
    /// </para>
    /// <para>
    /// 原来这一类的说明是"不管，多半是自己会好的事" —— 那是照着公网大厂的服务写的。
    /// 现实的版本是免费的在线模型：被抢光、被限流、5xx，一整天都"自己好不了"，
    /// 而用户以为监督还在。
    /// </para>
    /// </summary>
    NoAnswer,

    /// <summary>
    /// 压根没连上：DNS 失败、连接被拒、TLS 握手失败。
    ///
    /// <para>
    /// 单独分出来，是因为"要不要吵"取决于**那是谁的服务**：公网的连不上按"没网"处理（不管），
    /// 而本机/内网的连不上意味着"你自己的东西没起来" —— 网站照样打得开、你照样能刷，
    /// 只是没人判了。那种要吵。判断在 <see cref="ModelTrouble"/> 里。
    /// </para>
    /// </summary>
    Unreachable,

    /// <summary>
    /// 请求发出去了，但等到超时都没答上来。
    ///
    /// <para>
    /// 和"连不上"是两件事：这个说明服务在（只是慢或者卡住了）—— 本机跑大模型时很常见。
    /// 提醒的话术和节奏都不一样（那个是"拉起来就行"，这个得换小模型或者少喂点内容），
    /// 所以分成两类。
    /// </para>
    /// </summary>
    Timeout,

    /// <summary>
    /// 要你去处理：钥匙不对（401/403）、欠费（402）、超额限流（429）。
    /// 这个必须说 —— 不说的话模型监督已经悄悄停了，而你毫不知情。
    /// </summary>
    NeedsAttention,
}

public sealed record ModelCallResult(
    ModelCallOutcome Outcome,
    bool Violates = false,
    string? Reason = null,
    string? Message = null);

/// <summary>取模型列表的结果。<paramref name="Message"/> 无论成败都要能看懂 —— 取不到的时候，
/// 用户第一件要知道的事是"你试的是哪个地址"。</summary>
public sealed record ModelListResult(bool Ok, IReadOnlyList<string> Models, string? Message);

/// <summary>
/// 把一段内容发给大模型，问它"这违不违反我给自己定的禁令"。
///
/// <para>
/// 三种接口形状各发各的，但都归纳成同一件事：发一段话、拿一段话回来。回来那段话要求是一个
/// 二元组 <c>[true/false, "原因"]</c>。
/// </para>
/// <para>
/// 这里只负责一次调用。重试、缓存、去重、以及"要不要提醒用户"都在
/// <see cref="ModelSupervisor"/> 里 —— 那是策略，这是管道。
/// </para>
/// </summary>
public sealed class ModelClient
{
    /// <summary>
    /// 发给模型的正文上限。再长的页面也很少需要更多，而 token 是要花钱的。
    ///
    /// <para>
    /// 公开出来是给<b>探测</b>用的：探测要能和一次真实判定比快慢，就必须是同样的量级 ——
    /// 正文得填到这个数（见 <see cref="ModelSupervisor.ProbeAsync"/>）。
    /// </para>
    /// </summary>
    public const int MaxContentChars = 2000;

    private readonly HttpClient _http;

    /// <summary>
    /// 中文按原样发出去，不转成 <c>\uXXXX</c>。
    ///
    /// 转义在 JSON 里完全合法，接口也认；但请求体就变得没法读了 —— 调试的时候看到的是一长串
    /// <c>禁止</c>，而看不出自己到底发了句什么过去。
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 共用一个 HttpClient。超时 <b>60 秒</b>。
    ///
    /// <para>
    /// 共用的理由：以前每个 ModelClient 自己 new 一个，而 ModelClient 被监督器持有、监督器又随着
    /// 监控重建（Agent 重启、配置改动）。旧的一被回收，它名下的连接就关了，<b>正在飞的那次请求
    /// 就地夭折</b>，报出来正是"由于线程退出或应用程序请求，已中止 I/O 操作" —— 然后被记成
    /// "连不上"，弹"本地模型没起来"，而模型一直好好的。
    /// </para>
    /// <para>
    /// 超时为什么是 60 秒：<b>判定晚到等于没有判定</b>。一分钟还没回来的话，人早翻过去看别的了，
    /// 而每页都这么挂着，机器会被拖住（本机跑大模型尤其明显）。所以一分钟就是上限，
    /// 到点就归"太慢"这一类：停发新请求，反复提醒换模型 —— 见 <see cref="ModelTrouble"/>。
    /// </para>
    /// </summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    public ModelClient(HttpClient? http = null)
    {
        // 60 秒，不是 20。
        //
        // 超时的代价是**不对的**：读不到回答时按"没网"处理，也就是放行 —— 一个慢但有效的判定
        // 会被当成"这次没算"，于是该拦的没拦。而思考模型在一整页正文上想多久是没有保证的
        // （实测 deepseek-flash 在短文本上 1 秒，但那是短文本）。
        //
        // 反过来，等久一点的代价只是一次调用占着一条后台线程：判定是发出去就不管的，
        // 不挡监控循环，结果也会按内容指纹缓存起来。
        _http = http ?? SharedHttp;
    }

    public async Task<ModelCallResult> JudgeAsync(
        ModelProvider provider, string instruction, ContentSnapshot snapshot, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(provider.Endpoint) || string.IsNullOrWhiteSpace(provider.Model))
            return new ModelCallResult(ModelCallOutcome.NeedsAttention, Message: "这个服务商还没填地址和模型。");

        // 地址本身不成形，和"连不上"是两回事：前者要人去改配置，后者等一会儿可能自己好。
        // 混在一起的话，人只会一遍遍地点重试。
        if (!Uri.TryCreate(provider.Endpoint.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return new ModelCallResult(ModelCallOutcome.NeedsAttention,
                Message: $"请求地址不是一个完整的 http/https URL：{provider.Endpoint}");
        }

        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(() => Build(provider, instruction, snapshot), token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 原因要带出去：配服务商的时候，"连不上"三个字帮不上任何忙，而
            // "No such host is known" 和 "timed out" 是两件完全不同的事。
            if (LooksLikeTimeout(ex))
            {
                return new ModelCallResult(ModelCallOutcome.Timeout,
                    Message: $"等了 {_http.Timeout.TotalSeconds:0} 秒也没答上来 —— 判定晚到等于没有判定。"
                             + "换一个小一点的模型，或者换一个服务商（本机跑大模型最容易这样）。"
                             + "也可以先把「目标」页的「读取正文」关掉，少喂点内容给它。");
            }

            return new ModelCallResult(ModelCallOutcome.Unreachable, Message: Describe(ex));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return ClassifyFailure(provider, response.StatusCode, body);

            var text = ExtractAnswerText(provider.Format, body);
            if (string.IsNullOrWhiteSpace(text))
            {
                // 把"被额度截断"和"回来的是别的东西"分开说。这两种都表现为"没读到文字"，
                // 但一个是把 max_tokens 调大就好，另一个是地址或者格式不对 —— 混在一起就得靠猜。
                var why = WasCutOffByTokenLimit(body)
                    ? "模型的思考把 max_tokens 用完了，回答还没写就被截断。把模型换成不带思考的那种，或者让它想得短一点。"
                    : "回了 200，但里面没有能读的文字。";

                return new ModelCallResult(ModelCallOutcome.NoAnswer, Message: $"{why}{Shorten(body)}");
            }

            var verdict = ParseVerdict(text);
            return verdict.Outcome == ModelCallOutcome.NoAnswer
                ? verdict with
                {
                    Message = "回了 200，但它没按要求的格式回 —— 要的是一个 JSON 数组 [true/false, \"原因\"]。"
                              + $"它回的是：{Shorten(text)}",
                }
                : verdict;
        }
    }

    // ------------------------------------------------------------------ 传输

    /// <summary>
    /// 发一次请求，传输层失败时重试一次。
    ///
    /// <para>
    /// 为什么要有这个：到 api.deepseek.com 的 TLS 握手**偶发失败**（同一台机器、同一个地址，
    /// curl 连着三次都是 200，而 .NET 这边时不时报 "The SSL connection could not be established"）。
    /// 一次握手抖动不该让用户看到"连不上"—— 他只会以为是自己配错了。
    /// </para>
    /// <para>
    /// 只重试一次，不更多：一次判定最多等 60 秒，反复重试会把"慢"变成"卡"。真正的服务端故障
    /// （连不上、一直超时）重试一次也救不回来，那种情况该老实报错。
    /// </para>
    /// <para>
    /// <b>超时不重试。</b>重试是为握手抖动准备的（见上），而超时的意思是"对面就是慢" ——
    /// 再等一个 60 秒既救不回来，又让那台机器多烧一分钟（本机跑大模型尤其明显），
    /// 还把那句"太慢"往后推了一分钟才说得出口。
    /// </para>
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> build, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = build();
                return await _http.SendAsync(request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < Attempts && !LooksLikeTimeout(ex))
            {
                await Task.Delay(RetryDelay, token).ConfigureAwait(false);
            }
        }
    }

    private const int Attempts = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// 把异常链里每一层的说明拼起来。
    ///
    /// <para>
    /// 外层那句 "The SSL connection could not be established, see inner exception." 本身什么都
    /// 没说 —— 握手超时、证书不对、代理拦了，全都是这一句。真正的原因在内层，所以要一起带出来。
    /// </para>
    /// </summary>
    /// <summary>
    /// 这次失败是不是"等超时了"。
    ///
    /// <para>
    /// 认三个形状：<c>TaskCanceledException</c>（HttpClient 超时的标准形状）、
    /// 内层是 <c>TimeoutException</c>、以及那句"由于线程退出或应用程序请求，已中止 I/O 操作" ——
    /// 最后这句在 .NET 里既是超时的样子、也是"连接被回收"的样子，所以它必须靠共享 HttpClient
    /// 先把后一种消灭掉，这里才敢把它算进超时（见 <see cref="SharedHttp"/>）。
    /// </para>
    /// </summary>
    private static bool LooksLikeTimeout(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TaskCanceledException or TimeoutException)
                return true;

            if (current.Message.Contains("aborted", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("已中止", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();

        for (var e = ex; e is not null && parts.Count < 3; e = e.InnerException)
        {
            if (!parts.Contains(e.Message))
                parts.Add(e.Message);
        }

        return string.Join(" → ", parts);
    }

    // ------------------------------------------------------------------ 模型列表

    /// <summary>
    /// 可以拿去试的模型列表地址，**按可能性排在前面**。
    ///
    /// <para>
    /// 两个候选，因为各家不一样，光靠一条规则猜不准：
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>请求地址去掉最后一段，再接 <c>/models</c></b> —— 智谱就是这种：
    /// <c>…/api/paas/v4/chat/completions</c> → <c>…/api/paas/v4/models</c>（实测 200 ✓）。
    /// 本地 Ollama 也是这种：<c>…/v1/chat/completions</c> → <c>…/v1/models</c> ✓。
    /// </description></item>
    /// <item><description>
    /// <b>同域名下的 <c>/v1/models</c></b> —— DeepSeek 是这种：它的列表**不在**请求路径那一段下面
    /// （<c>/anthropic/…</c> 底下是 404），而在 <c>https://api.deepseek.com/v1/models</c> ✓。
    /// </description></item>
    /// </list>
    /// <para>
    /// 两个都可以错，所以取列表时**挨个试**，谁先回出模型名就用谁（见
    /// <see cref="ListModelsAsync"/>）。填了「模型列表地址」就只试填的那个 —— 人说了算。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SuggestListEndpoints(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return Array.Empty<string>();

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            return Array.Empty<string>();

        var candidates = new List<string>();

        // 一、请求地址去掉最后那段"叶子"，再接 /models。
        //
        // 注意 /chat/completions 是**一个整体**：只去掉 completions 会得到
        // ".../v1/chat/models"（错）。所以要按已知的叶子整段削。
        var path = uri.AbsolutePath.TrimEnd('/');

        foreach (var leaf in new[] { "/chat/completions", "/completions", "/messages", "/responses" })
        {
            if (path.EndsWith(leaf, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^leaf.Length];
                break;
            }
        }

        if (path.Length > 0)
        {
            var derived = $"{uri.Scheme}://{uri.Authority}{path}/models";

            if (!candidates.Contains(derived, StringComparer.OrdinalIgnoreCase))
                candidates.Add(derived);
        }

        // 二、域名 + /v1/models
        var domain = $"{uri.Scheme}://{uri.Authority}/v1/models";

        if (!candidates.Contains(domain, StringComparer.OrdinalIgnoreCase))
            candidates.Add(domain);

        return candidates;
    }

    /// <summary>界面提示用的那个：最可能的那个。</summary>
    /// <summary>
    /// 把一个"只写到 base"的地址补成完整的请求地址；已经完整、或者补不出来就返回 null。
    ///
    /// <para>
    /// 和界面失焦时的自动补全是同一套规则，区别只有一个：这里**不管末尾有没有斜杠**。
    /// 那个约定（斜杠 = 别动我写的）只属于"悄悄改用户输入"的场合；而"测试连接"是在问
    /// "这条路到底通不通"，多试一种写法没有任何副作用 —— 用户那个
    /// <c>…/api/paas/v4/</c> 就是这么卡在 404 上的。
    /// </para>
    /// </summary>
    public static string? CompleteEndpoint(string? endpoint, ApiFormat format)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var url = endpoint.Trim().TrimEnd('/');

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return null;

        var lower = url.ToLowerInvariant();

        // **已经以某个已知叶子结尾的，就不动它。** 光看"这个格式该有的关键词在不在"不够：
        // 一个 Anthropic 的完整地址（…/v1/messages）在 OpenAI 格式下会被那个判断漏过去，
        // 于是被补成 …/v1/messages/chat/completions。叶片是最直接的判据。
        foreach (var leaf in new[] { "/chat/completions", "/completions", "/messages", "/responses" })
        {
            if (lower.EndsWith(leaf, StringComparison.Ordinal))
                return null;
        }

        // 已经带版本段（/v4、/api/v1 这种）就只补最后那一段：再塞一个 /v1 进去是错的。
        var versioned = System.Text.RegularExpressions.Regex.IsMatch(lower, @"/v\d+(/|$)");

        var suffix = format switch
        {
            ApiFormat.Anthropic when !lower.Contains("message")
                => versioned ? "/messages" : "/v1/messages",
            ApiFormat.Responses when !lower.Contains("response")
                => versioned ? "/responses" : "/v1/responses",
            ApiFormat.OpenAi when !lower.Contains("completion")
                => versioned ? "/chat/completions" : "/v1/chat/completions",
            _ => null,
        };

        return suffix is null ? null : url + suffix;
    }

    public static string SuggestListEndpoint(string? endpoint) =>
        SuggestListEndpoints(endpoint).FirstOrDefault() ?? "";

    /// <summary>取服务商的模型列表。用**表单里现在填的**那份，理由和测试连接一样。</summary>
    public async Task<ModelListResult> ListModelsAsync(ModelProvider provider, CancellationToken token)
    {
        var explicitUrl = provider.ModelListEndpoint?.Trim() ?? "";

        // 填了就只试填的那个（人说了算）；没填就挨个试候选。
        var candidates = explicitUrl.Length > 0
            ? new[] { explicitUrl }
            : SuggestListEndpoints(provider.Endpoint).ToArray();

        if (candidates.Length == 0)
        {
            return new ModelListResult(false, Array.Empty<string>(),
                "还不知道去哪个地址取 —— 先把「请求地址」填上，或者直接填「模型列表地址」。");
        }

        var failures = new List<string>();

        foreach (var url in candidates)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendWithRetryAsync(() => BuildListRequest(provider, url), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"「{url}」连不上：{Describe(ex)}");
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"「{url}」回了 {(int)response.StatusCode}。{Shorten(body)}");
                    continue;
                }

                // 200 也可能是**一个网页** —— 有些平台把找不到的路径交给自己前台的页面兜底，
                // 于是你拿到一坨 HTML，看着像成功、其实地址是错的。这种要指名道姓地说出来。
                if (LooksLikeHtml(body))
                {
                    failures.Add($"「{url}」回的是网页，不是模型列表 —— 这个地址是错的。");
                    continue;
                }

                var models = ParseModelIds(body);

                if (models.Count > 0)
                    return new ModelListResult(true, models, $"取到 {models.Count} 个模型（来自 {url}）。");

                failures.Add($"「{url}」回了 200，但没读出模型名。");
            }
        }

        var advice = explicitUrl.Length > 0
            ? "这个地址是你自己填的，改一下「模型列表地址」。"
            : "都不对就直接填「模型列表地址」（有些平台的列表压根不开放，那就手填模型名）。";

        return new ModelListResult(false, Array.Empty<string>(),
            string.Join("\n", failures) + "\n" + advice);
    }

    /// <summary>这坨东西看着像网页吗 —— 用来识破"200 但其实是前端页面兜底"。</summary>
    private static bool LooksLikeHtml(string body)
    {
        var head = body.TrimStart();

        return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
               || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage BuildListRequest(ModelProvider provider, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (provider.Format == ApiFormat.Anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {provider.ApiKey}");
        }

        return request;
    }

    /// <summary>
    /// 从回复里挑出模型名。
    ///
    /// 认两种形状：<c>data[].id</c>（OpenAI、Anthropic、DeepSeek 都是）和 <c>models[].name</c>
    /// （Google 那一套，名字还带 <c>models/</c> 前缀）。认不出来的就返回空 —— 空列表会走到
    /// "回了 200 但没读出模型名"，那比猜一个错的好。
    /// </summary>
    public static IReadOnlyList<string> ParseModelIds(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            var ids = new List<string>();

            Collect(root?["data"], ids);
            Collect(root?["models"], ids);

            return ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static void Collect(JsonNode? node, List<string> into)
    {
        if (node is not JsonArray array)
            return;

        foreach (var item in array)
        {
            if (item?["id"]?.GetValue<string>() is { Length: > 0 } id)
            {
                into.Add(id);
                continue;
            }

            if (item?["name"]?.GetValue<string>() is { Length: > 0 } name)
                into.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name);
        }
    }

    // ------------------------------------------------------------------ 三类失败

    private static ModelCallResult ClassifyFailure(ModelProvider provider, HttpStatusCode status, string body)
    {
        var detail = Shorten(body);

        // 404 的时候顺手把"补全之后长什么样"算出来给人看 —— 那比举一个别家的例子有用得多。
        var complete = CompleteEndpoint(provider.Endpoint, provider.Format);

        return status switch
        {

            // 钥匙不对、欠费、超额：三条都得让人去处理，不然模型监督已经停了而他不知道。
            HttpStatusCode.Unauthorized => new(ModelCallOutcome.NeedsAttention,
                Message: $"API key 不对或者已经失效（401）。{detail}"),

            HttpStatusCode.Forbidden => new(ModelCallOutcome.NeedsAttention,
                Message: $"这个 key 没有权限（403）。{detail}"),

            HttpStatusCode.PaymentRequired => new(ModelCallOutcome.NeedsAttention,
                Message: $"余额不够了（402）。去充值，或者换一个服务商。{detail}"),

            HttpStatusCode.TooManyRequests => new(ModelCallOutcome.NeedsAttention,
                Message: $"被限流或者额度用完了（429）。{detail}"),

            // 地址写错是**配服务商时最常见的一种错**，而它以前被归进"没网"那一类 ——
            // 于是界面上说"连不上（没网、超时……）"，人就去查网络，而真正的问题是地址少了一段路径。
            HttpStatusCode.NotFound => new(ModelCallOutcome.NeedsAttention,
                Message: "地址不对（404）—— 服务端没有这个路径。"
                         + (complete is null
                             ? "请求地址要写**完整**的（base 后面那段路径不能少）。"
                             : $"你填的是「{provider.Endpoint}」，补全之后是「{complete}」—— 试一下这个。")
                         + detail),

            HttpStatusCode.BadRequest => new(ModelCallOutcome.NeedsAttention,
                Message: $"服务端拒了这个请求（400）。多半是模型名或者格式对不上。{detail}"),

            HttpStatusCode.UnprocessableEntity => new(ModelCallOutcome.NeedsAttention,
                Message: $"服务端说请求体的格式不对（422）。{detail}"),

            // 剩下的 4xx 也是"你得去处理"：它不会自己好，而且多半是配置或账号的事。
            >= HttpStatusCode.Ambiguous and < HttpStatusCode.InternalServerError =>
                new(ModelCallOutcome.NeedsAttention,
                    Message: $"服务端回了 {(int)status}。{detail}"),

            _ => new(ModelCallOutcome.NoAnswer, Message: $"服务端出错了（{(int)status}）—— 它现在没在判任何东西。{detail}"),
        };
    }

    private static string Shorten(string body) =>
        body.Length <= 200 ? body.Trim() : body[..200].Trim() + "…";

    // ------------------------------------------------------------------ 各家形状

    private static HttpRequestMessage Build(ModelProvider provider, string instruction, ContentSnapshot snapshot)
    {
        var user = BuildQuestion(instruction, snapshot);

        var json = provider.Format switch
        {
            ApiFormat.OpenAi => BuildOpenAi(provider, SystemPrompt, user),
            ApiFormat.Responses => BuildResponses(provider, SystemPrompt, user),
            ApiFormat.Anthropic => BuildAnthropic(provider, SystemPrompt, user),
            _ => throw new NotSupportedException($"不认识的格式：{provider.Format}"),
        };

        var request = new HttpRequestMessage(HttpMethod.Post, provider.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (provider.Format == ApiFormat.Anthropic)
        {
            // Anthropic 用 x-api-key，不是 Bearer。
            request.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {provider.ApiKey}");
        }

        return request;
    }

    private static string BuildOpenAi(ModelProvider p, string system, string user) =>
        new JsonObject
        {
            ["model"] = p.Model,
            ["temperature"] = 0,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        }.ToJsonString(Json);

    private static string BuildResponses(ModelProvider p, string system, string user) =>
        new JsonObject
        {
            ["model"] = p.Model,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = system } },
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = user } },
                },
            },
        }.ToJsonString(Json);

    private static string BuildAnthropic(ModelProvider p, string system, string user) =>
        new JsonObject
        {
            ["model"] = p.Model,

            // 比别处宽得多，因为**思考模型的推理也算在这里面**。
            //
            // 实测 DeepSeek 的 deepseek-flash：一句"这段文本违不违规"，推理就用掉了 150 个 token，
            // 加回答一共 170。原来写 200，这种简单输入刚好够；换成一整页正文，推理会长得多，
            // 200 一超，回答那块就根本不会出现 —— 那时我们只会看到"没有能读的文字"，
            // 而真实原因是额度被思考吃完了。这里宽到 2048：用多少算多少钱，没用到的不花钱。
            ["max_tokens"] = 2048,
            ["system"] = system,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        }.ToJsonString(Json);

    // ------------------------------------------------------------------ 提示词

    /// <summary>
    /// 要模型只回一个二元组。
    ///
    /// <para>
    /// 特意用 JSON 而不是"回 是/否"：自然语言的回答后面总会跟一句解释，而"是"和"否"混在
    /// 一整段话里，靠字符串匹配去认是会认错的 —— 一旦认反，结果就是**该拦的放过去**。
    /// </para>
    /// <para>
    /// <b>还要教它识破自相矛盾的补充说明。</b>别的规则改成什么样都要令牌，唯独**模型规则的
    /// 那段文字"追加内容"算收紧**（关键词规则里加一条确实是多拦一点，而模型规则里加一句
    /// "这类不用拦"是放宽）—— 那是这个功能唯一能不动钥匙就变松的地方。挡这一手的位置只有
    /// 提示词：让模型区分"为了不误伤而收窄"（照办）和"直接推翻主禁令"（不认）。
    /// </para>
    /// <para>
    /// 它是一道缓解，不是证明：把整段话改写成一个自洽的新禁令，模型就看不出来了 ——
    /// 那种改法是"替换内容"，按放宽算，要令牌。
    /// </para>
    /// </summary>
    private const string SystemPrompt =
        "你是一个内容审查器。用户给自己定了一条禁令，你要判断给定的页面内容是否违反它。\n" +
        "\n" +
        "禁令可能由好几段组成：主禁令，加上后来补的说明。\n" +
        "补充说明如果是在**收窄**范围（点名某类页面、某种情况不算，理由通常是避免误伤），照它办。\n" +
        "但如果它把主禁令点名的一整类内容都放掉 —— 哪怕它给了理由（「合理休闲」「可能在学东西」）\n" +
        "—— 那和主禁令是矛盾的（这是推翻，不是收窄）：**按主禁令判**，不要把那一整段当成许可，\n" +
        "也不要折中。\n" +
        "分不清是收窄还是推翻时，按最严格的判。\n" +
        "\n" +
        "只输出一个 JSON 数组，不要任何其他文字、不要代码块：\n" +
        "[是否违规, \"原因\"]\n" +
        "第一个元素是 true 或 false；第二个元素是一句凝练的中文原因，不超过 25 个字。\n" +
        "判断不了或者内容不足以判断时，一律返回 false。";

    private static string BuildQuestion(string instruction, ContentSnapshot snapshot)
    {
        var body = snapshot.BodyText ?? "";
        if (body.Length > MaxContentChars)
            body = body[..MaxContentChars] + "…";

        return new StringBuilder()
            .AppendLine("禁令（用户写给自己看的）：")
            .AppendLine(instruction.Trim())
            .AppendLine()
            .AppendLine("页面内容：")
            .AppendLine($"网址：{snapshot.Url ?? "(没有)"}")
            .AppendLine($"标题：{snapshot.Title ?? "(没有)"}")
            .AppendLine($"正文：{(body.Length == 0 ? "(没有读到)" : body)}")
            .ToString();
    }

    // ------------------------------------------------------------------ 取回答

    private static string? ExtractAnswerText(ApiFormat format, string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            if (root is null)
                return null;

            return format switch
            {
                ApiFormat.OpenAi => root["choices"]?[0]?["message"]?["content"]?.GetValue<string>(),

                // Responses 有一个 output_text 便捷字段；没有就按结构一层层走。
                ApiFormat.Responses => root["output_text"]?.GetValue<string>()
                    ?? FirstTextIn(root["output"]),

                ApiFormat.Anthropic => FirstAnthropicText(root["content"]),

                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 Anthropic 那套的 <c>content</c> 数组里挑出真正的回答。
    ///
    /// <para>
    /// <b>不能只看第 0 块。</b>思考模型（DeepSeek 的 <c>deepseek-flash</c>、开了 extended thinking 的
    /// Claude）会把推理单独放成一块，<c>type</c> 是 <c>"thinking"</c>，真正的回答在**后面**那块
    /// <c>type == "text"</c> 里。按第 0 块读的话，读到的是一块没有 <c>text</c> 字段的推理，
    /// 于是判定被丢成"没读到内容"—— 模型明明答对了，是我们没去拿。
    /// </para>
    /// </summary>
    /// <summary>回复是不是因为撞上 max_tokens 才没写完（Anthropic 那套会明说 stop_reason）。</summary>
    private static bool WasCutOffByTokenLimit(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["stop_reason"]?.GetValue<string>() == "max_tokens";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FirstAnthropicText(JsonNode? content)
    {
        if (content is not JsonArray blocks)
            return null;

        foreach (var block in blocks)
        {
            var type = block?["type"]?.GetValue<string>();

            if (type is not null and not "text")
                continue;   // thinking / redacted_thinking / tool_use 之类的块，跳过

            if (block?["text"]?.GetValue<string>() is { Length: > 0 } text)
                return text;
        }

        return null;
    }

    private static string? FirstTextIn(JsonNode? output)
    {
        if (output is not JsonArray items)
            return null;

        foreach (var item in items)
        {
            if (item?["content"] is not JsonArray parts)
                continue;

            foreach (var part in parts)
            {
                if (part?["text"]?.GetValue<string>() is { Length: > 0 } text)
                    return text;
            }
        }

        return null;
    }

    /// <summary>
    /// 从模型那句话里抠出判定。
    ///
    /// <para>
    /// 约定只有一种形状：<c>[布尔, "原因"]</c> —— 提示词里就是这么要求的。这里容忍的只有
    /// **外面的包装**（代码块、一句前言）以及**带引号的布尔**（<c>["true", …]</c>：语义没有
    /// 歧义，只是 JSON 写得不规范）。
    /// </para>
    /// <para>
    /// 除此之外一概不认：中文词、对象、整句人话都算"读不出"。**这不是苛刻，是不替模型猜** ——
    /// 猜意味着把结论判反，而判反意味着该拦的放过去、不该拦的拦下来。模型给了规范还不照着回，
    /// 那件事该由人换模型，不该由这里的代码糊过去；上层会把它的原始回答摊出来给人看。
    /// </para>
    /// </summary>
    public static ModelCallResult ParseVerdict(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');

        if (start < 0 || end <= start)
            return new ModelCallResult(ModelCallOutcome.NoAnswer);

        try
        {
            if (JsonNode.Parse(text[start..(end + 1)]) is not JsonArray array || array.Count == 0)
                return new ModelCallResult(ModelCallOutcome.NoAnswer);

            if (ReadFlag(array[0]) is not { } violates)
                return new ModelCallResult(ModelCallOutcome.NoAnswer);

            var reason = array.Count > 1
                         && array[1] is JsonValue value
                         && value.TryGetValue<string>(out var spoken)
                ? spoken
                : null;

            if (!violates)
                return new ModelCallResult(ModelCallOutcome.Answered);

            // 说违规但没给原因：照样拦，只是没法告诉他为什么。
            return new ModelCallResult(ModelCallOutcome.Answered, Violates: true,
                Reason: string.IsNullOrWhiteSpace(reason) ? "模型判定违规" : reason.Trim());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new ModelCallResult(ModelCallOutcome.NoAnswer);
        }
    }

    /// <summary>
    /// 数组第一个元素当"是/否"读：真布尔，或者带引号的 <c>"true"</c>/<c>"false"</c>。
    ///
    /// <para>
    /// 只多认这两个。位置已经说明它是判定，所以带引号的布尔没有歧义。别的一概不认 ——
    /// 一旦开始从人话里找"违规"两个字，<c>["maybe", "可能违规"]</c> 就会被读成"违规"：
    /// 那是判反，而判反意味着该拦的放过去。
    /// </para>
    /// </summary>
    private static bool? ReadFlag(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue<bool>(out var flag))
            return flag;

        if (!value.TryGetValue<string>(out var word))
            return null;

        return word.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
    }

}
