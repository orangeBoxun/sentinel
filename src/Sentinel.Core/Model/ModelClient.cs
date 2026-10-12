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
    /// 没拿到：没网、超时、服务端 5xx、或者回来的东西看不懂。
    ///
    /// <b>这一类不管。</b>没网的时候那些娱乐网站自己也打不开 —— 拦不拦没有区别，
    /// 而为一件不会发生的事弹全屏提醒，只会让人把这个功能关掉。
    /// </summary>
    NoAnswer,

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
    /// <summary>发给模型的正文上限。再长的页面也很少需要更多，而 token 是要花钱的。</summary>
    private const int MaxContentChars = 2000;

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
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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
            // 连不上、DNS 失败、超时、TLS 握手失败 —— 都属于"没网"那一类。
            //
            // 但原因要带出去：配服务商的时候，"连不上"三个字帮不上任何忙，而
            // "No such host is known" 和 "timed out" 是两件完全不同的事。
            return new ModelCallResult(ModelCallOutcome.NoAnswer, Message: Describe(ex));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return ClassifyFailure(response.StatusCode, body);

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
                ? verdict with { Message = $"回了 200，但那段话读不出判定：{Shorten(text)}" }
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
            catch (Exception) when (attempt < Attempts)
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
    /// 按请求地址猜一个模型列表地址：同域名下的 <c>/v1/models</c>。
    ///
    /// <para>
    /// 这只是个**默认值**，不是真理。实测 DeepSeek 的 <c>/v1/models</c> 是 200，而请求地址上那一段
    /// <c>/anthropic</c> 底下是 404 —— 列表不在请求路径下面。所以填出来的东西可以改，取不到的时候
    /// 界面会把它到底试了哪个地址说出来。
    /// </para>
    /// </summary>
    public static string SuggestListEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "";

        return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}/v1/models"
            : "";
    }

    /// <summary>取服务商的模型列表。用**表单里现在填的**那份，理由和测试连接一样。</summary>
    public async Task<ModelListResult> ListModelsAsync(ModelProvider provider, CancellationToken token)
    {
        var explicitUrl = provider.ModelListEndpoint?.Trim() ?? "";
        var url = explicitUrl.Length > 0 ? explicitUrl : SuggestListEndpoint(provider.Endpoint);

        if (url.Length == 0)
        {
            return new ModelListResult(false, Array.Empty<string>(),
                "还不知道去哪个地址取 —— 先把「请求地址」填上，或者直接填「模型列表地址」。");
        }

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
            return new ModelListResult(false, Array.Empty<string>(), $"连不上「{url}」：{Describe(ex)}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 一定要带上试的是哪个地址：列表地址是猜的，猜错了只有这句话能让人改对。
                var hint = explicitUrl.Length == 0 ? "（这是按请求地址猜的，不对就直接填「模型列表地址」）" : "";
                return new ModelListResult(false, Array.Empty<string>(),
                    $"试的是「{url}」，它回了 {(int)response.StatusCode}。{Shorten(body)}{hint}");
            }

            var models = ParseModelIds(body);

            return models.Count > 0
                ? new ModelListResult(true, models, $"取到 {models.Count} 个模型（来自 {url}）。")
                : new ModelListResult(false, Array.Empty<string>(),
                    $"「{url}」回了 200，但里面没读出模型名 —— 地址可能不对。" +
                    (explicitUrl.Length == 0 ? "这是按请求地址猜的，可以直接填「模型列表地址」。" : ""));
        }
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

    private static ModelCallResult ClassifyFailure(HttpStatusCode status, string body)
    {
        var detail = Shorten(body);

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
                Message: "地址不对（404）—— 服务端没有这个路径。请求地址要写**完整**的，比如 "
                         + "https://api.deepseek.com/anthropic/v1/messages（base 后面那段 /v1/messages 不能少）。"
                         + detail),

            HttpStatusCode.BadRequest => new(ModelCallOutcome.NeedsAttention,
                Message: $"服务端拒了这个请求（400）。多半是模型名或者格式对不上。{detail}"),

            HttpStatusCode.UnprocessableEntity => new(ModelCallOutcome.NeedsAttention,
                Message: $"服务端说请求体的格式不对（422）。{detail}"),

            // 剩下的 4xx 也是"你得去处理"：它不会自己好，而且多半是配置或账号的事。
            >= HttpStatusCode.Ambiguous and < HttpStatusCode.InternalServerError =>
                new(ModelCallOutcome.NeedsAttention,
                    Message: $"服务端回了 {(int)status}。{detail}"),

            _ => new(ModelCallOutcome.NoAnswer, Message: $"服务端出错了（{(int)status}），等它自己好。{detail}"),
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
    /// </summary>
    private const string SystemPrompt =
        "你是一个内容审查器。用户给自己定了一条禁令，你要判断给定的页面内容是否违反它。\n" +
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
    /// 把模型那段话读成二元组。
    ///
    /// 容忍外面裹着的 ``` 围栏和前后废话 —— 各家模型都爱加，而因为多了三个反引号就把一次
    /// 判定丢掉太亏了。**但不容忍"看不懂"被当成"没问题"** —— 那种默认值会把该拦的放过去。
    /// 看不懂就返回 NoAnswer，也就是"这次没算"，不是"允许"。
    /// </summary>
    public static ModelCallResult ParseVerdict(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');

        if (start < 0 || end <= start)
            return new ModelCallResult(ModelCallOutcome.NoAnswer);

        try
        {
            var array = JsonNode.Parse(text[start..(end + 1)]) as JsonArray;
            if (array is null || array.Count == 0)
                return new ModelCallResult(ModelCallOutcome.NoAnswer);

            var violates = array[0]?.GetValue<bool>() ?? false;
            var reason = array.Count > 1 ? array[1]?.GetValue<string>() : null;

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
}
