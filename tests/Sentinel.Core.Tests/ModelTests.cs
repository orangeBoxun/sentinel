using System.Net;
using System.Net.Http;
using Sentinel.Core.Config;
using Sentinel.Core.Engine;
using Sentinel.Core.Model;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 模型监督这一层。
///
/// <para>
/// 最要紧的两块：**三种接口形状各发什么**，以及**看不懂的回答绝不能被当成"没问题"**。
/// 前者错了会得到 404 或者 400，用户拿着一份看不懂的报错回来；后者错了更糟 —— 该拦的会
/// 被悄悄放过去，而且全程没有任何迹象。
/// </para>
/// </summary>
public class ModelTests
{
    private static ContentSnapshot Snapshot(string url = "https://example.com/", string title = "标题",
                                            string body = "正文") =>
        new(url, title, body, DateTimeOffset.Now);

    // ------------------------------------------------------------------ 请求形状

    private sealed class CapturingHandler(string response, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        /// <summary>发了几次。用来看有没有在省钱上做对 —— 复用的时候这里不该涨。</summary>
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }

    /// <summary>头几次发送直接抛，之后才正常 —— 用来试传输层的重试。</summary>
    private sealed class FlakyHandler(int failFirst, string response) : HttpMessageHandler
    {
        private int _seen;

        public int Attempts => _seen;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _seen++;

            if (_seen <= failFirst)
                throw new HttpRequestException("The SSL connection could not be established, see inner exception.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });
        }
    }

    private static (ModelClient Client, CapturingHandler Handler) ClientReturning(
        string response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(response, status);

        // Timeout 给足，免得测试机慢的时候变成超时而不是它要测的那条路。
        return (new ModelClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) }), handler);
    }

    private static ModelProvider Provider(ApiFormat format) => new()
    {
        Id = "p",
        Name = "p",
        Format = format,
        Endpoint = format switch
        {
            ApiFormat.Anthropic => "https://api.anthropic.com/v1/messages",
            ApiFormat.Responses => "https://api.openai.com/v1/responses",
            _ => "https://api.openai.com/v1/chat/completions",
        },
        Model = "some-model",
        ApiKey = "test-key",
    };

    [Fact]
    public async Task OpenAi_goes_to_the_url_you_gave_with_a_bearer_token()
    {
        var (client, handler) = ClientReturning(
            """{"choices":[{"message":{"content":"[true, \"刷视频停不下来\"]"}}]}""");

        var result = await client.JudgeAsync(Provider(ApiFormat.OpenAi), "禁止刷短视频", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.Answered, result.Outcome);
        Assert.True(result.Violates);
        Assert.Equal("刷视频停不下来", result.Reason);

        // 地址就是我们给的那一条，没有拼任何东西上去 —— 中转服务的路径千奇百怪，猜不如让你填。
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.Request.Headers.Authorization.Parameter);

        Assert.Contains("\"messages\"", handler.Body);
        Assert.Contains("禁止刷短视频", handler.Body);
    }

    [Fact]
    public async Task Anthropic_uses_x_api_key_not_bearer()
    {
        var (client, handler) = ClientReturning("""{"content":[{"text":"[false, \"\"]"}]}""");

        var result = await client.JudgeAsync(Provider(ApiFormat.Anthropic), "禁止摸鱼", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.Answered, result.Outcome);
        Assert.False(result.Violates);

        // Anthropic 用的是 x-api-key。发成 Bearer 会得到 401，而报错只会说"钥匙不对"，
        // 让人回去翻自己的 key —— 方向完全错了。
        Assert.Null(handler.Request!.Headers.Authorization);
        Assert.Equal("test-key", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());

        Assert.Contains("\"max_tokens\"", handler.Body);
    }

    [Fact]
    public async Task Anthropic_reads_the_text_block_even_when_thinking_comes_first()
    {
        // 思考模型（DeepSeek 的 deepseek-flash、开了 extended thinking 的 Claude）把推理单独放一块，
        // type 是 "thinking"，真正的回答在它**后面**。原来是硬读第 0 块，于是读到一块没有 text 的
        // 推理，判定被丢掉 —— 模型答对了，是我们没去拿。这个形状是拿真接口抓下来的。
        var (client, _) = ClientReturning(
            """
            {"id":"x","type":"message","role":"assistant","model":"deepseek-flash",
             "content":[
               {"type":"thinking","thinking":"The page is a connectivity test...","signature":"sig"},
               {"type":"text","text":"[false, \"内容为普通测试文本\"]"}
             ],
             "stop_reason":"end_turn"}
            """);

        var result = await client.JudgeAsync(Provider(ApiFormat.Anthropic), "禁止摸鱼", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.Answered, result.Outcome);
        Assert.False(result.Violates);
    }

    [Fact]
    public async Task Anthropic_still_blocks_when_the_text_block_says_so()
    {
        // 上面那条只证明"读到了"，这条证明读到的确实是那个判定，而不是推理里的一句话。
        var (client, _) = ClientReturning(
            """
            {"content":[
               {"type":"thinking","thinking":"也许该返回 [false, \"\"]……不，这明显违规。"},
               {"type":"text","text":"[true, \"和编程无关的短视频\"]"}
             ],"stop_reason":"end_turn"}
            """);

        var result = await client.JudgeAsync(Provider(ApiFormat.Anthropic), "禁止刷短视频", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.Answered, result.Outcome);
        Assert.True(result.Violates);
        Assert.Equal("和编程无关的短视频", result.Reason);
    }

    [Fact]
    public async Task Anthropic_with_only_a_thinking_block_is_not_an_answer()
    {
        var (client, _) = ClientReturning(
            """{"content":[{"type":"thinking","thinking":"我还在想……"}],"stop_reason":"end_turn"}""");

        var result = await client.JudgeAsync(Provider(ApiFormat.Anthropic), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.NoAnswer, result.Outcome);
    }

    [Fact]
    public async Task Anthropic_cut_off_by_the_token_limit_says_exactly_that()
    {
        // 思考把额度吃光时，回答那块根本不会出现。不把这件事单独说出来，看到的就只是
        // "没有能读的文字"，而真正该做的是把模型换成不带思考的那种。
        var (client, _) = ClientReturning(
            """{"content":[{"type":"thinking","thinking":"想了很久……"}],"stop_reason":"max_tokens"}""");

        var result = await client.JudgeAsync(Provider(ApiFormat.Anthropic), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.NoAnswer, result.Outcome);
        Assert.Contains("max_tokens", result.Message);
    }

    [Fact]
    public async Task Responses_reads_the_output_text()
    {
        var (client, handler) = ClientReturning("""{"output_text":"[true, \"和编程无关\"]"}""");

        var result = await client.JudgeAsync(Provider(ApiFormat.Responses), "只许看编程相关", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.Answered, result.Outcome);
        Assert.True(result.Violates);
        Assert.Equal("和编程无关", result.Reason);
        Assert.Contains("\"input\"", handler.Body);
    }

    // ------------------------------------------------------------------ 看不懂的回答

    [Theory]
    [InlineData("我觉得这是违规的")]                    // 没人话里带数组
    [InlineData("[true")]                             // 只有一边括号
    [InlineData("[]")]                                // 空的
    [InlineData("""{"violates":true}""")]             // 对象，不是数组
    [InlineData("[maybe, 大概吧]")]                    // 第一个不是布尔
    [InlineData("""["maybe", "可能违规"]""")]          // 第一个读不出 —— 别把"原因"当成判定读
    [InlineData("""["违规", "在刷视频"]""")]            // 用中文词代替布尔：不认，要求换模型而不是猜
    [InlineData("""["不违规", "关注列表页"]""")]
    public void An_answer_that_cannot_be_read_is_not_treated_as_allowed(string text)
    {
        // 这是这一层最重要的一条：认不出来 = "这次没算"，**不是"允许"**。
        // 一旦默认成允许，该拦的就被悄悄放过去了，而且看不出任何区别。
        var result = ModelClient.ParseVerdict(text);

        Assert.Equal(ModelCallOutcome.NoAnswer, result.Outcome);
        Assert.False(result.Violates);
    }

    [Fact]
    public void A_quoted_boolean_is_still_a_verdict()
    {
        // 本地小模型常这么回：把 true 写成带引号的字符串。以前这里直接抛异常 → 整段判定被当成
        // "读不出"，看起来像模型监督坏了，其实只差两个引号。
        var result = ModelClient.ParseVerdict("""["true", "无违规，内容为技术测试"]""");

        Assert.Equal(ModelCallOutcome.Answered, result.Outcome);
        Assert.True(result.Violates);
    }

    [Fact]
    public void Fences_and_chatter_around_the_array_are_tolerated()
    {
        // 各家模型都爱加代码块和一句前言，因为多了三个反引号就丢掉一次判定太亏了。
        var result = ModelClient.ParseVerdict("好的，我的判断是：\n```json\n[true, \"分心\"]\n```");

        Assert.Equal(ModelCallOutcome.Answered, result.Outcome);
        Assert.True(result.Violates);
        Assert.Equal("分心", result.Reason);
    }

    [Fact]
    public void Saying_violation_without_a_reason_still_blocks()
    {
        var result = ModelClient.ParseVerdict("[true]");

        Assert.True(result.Violates);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task A_request_that_times_out_is_its_own_kind()
    {
        // 超时和"连不上"是两件事：服务在，只是慢或者卡住了（本机跑大模型尤其常见）。
        // 分开之后提示的话术和节奏才对得上 —— "拉起来就行"和"换个小的模型"不是一句话。
        var client = new ModelClient(new HttpClient(new SlowHandler()) { Timeout = TimeSpan.FromMilliseconds(300) });

        var result = await client.JudgeAsync(Provider(ApiFormat.OpenAi), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.Timeout, result.Outcome);
        Assert.Contains("秒", result.Message);
    }

    [Fact]
    public async Task A_stale_problem_clears_itself_once_the_model_answers_again()
    {
        // 模型恢复之后，那条"停摆"状态**自己会清掉** —— 不用等谁打开一个命中规则的页面。
        // 不修的话，"昨晚模型没起来"留下的状态会一直挂着、提醒一直响，而它早就好了。
        var provider = Provider(ApiFormat.OpenAi);

        // 用本地地址：公网连不上是**故意不吵**的，那样测不出这条。
        provider.Endpoint = "http://127.0.0.1:11434/v1/chat/completions";

        var config = new AppConfig();
        config.Models.Add(provider);
        config.ActiveModelId = provider.Id;

        var handler = new SwitchHandler { Fail = true };
        var supervisor = new ModelSupervisor(
            () => config,
            new ModelClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }));

        supervisor.Request("r", "禁令", Snapshot());

        await WaitUntil(() => supervisor.BlockingIssue is not null);
        Assert.NotNull(supervisor.BlockingIssue);
        Assert.Equal(ModelTroubleKind.LocalUnreachable, supervisor.BlockingKind);

        handler.Fail = false;   // 服务起来了

        Assert.True(await supervisor.ProbeAsync());
        Assert.Null(supervisor.BlockingIssue);
        Assert.Equal(ModelTroubleKind.None, supervisor.BlockingKind);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 60 && !condition(); i++)
            await Task.Delay(50);

        Assert.True(condition(), "等超时了");
    }

    /// <summary>一直拖到超时为止。</summary>
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    /// <summary>可以中途从"坏"翻成"好"，用来测自愈。</summary>
    private sealed class SwitchHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new HttpRequestException("目标计算机积极拒绝，无法连接。");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"[false, \"没问题\"]"}}]}"""),
            });
        }
    }

    // ------------------------------------------------------------------ 失败分类

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Problems_you_must_act_on_are_reported(HttpStatusCode status)
    {
        // 这几种不提醒的话，模型监督已经停了而你毫不知情 —— 那正是最坏的情况：
        // 你以为它在看着，其实什么也没在看。
        var (client, _) = ClientReturning("{}", status);

        var result = await client.JudgeAsync(Provider(ApiFormat.OpenAi), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.NeedsAttention, result.Outcome);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task A_server_error_is_not_worth_telling_the_user_about()
    {
        // 5xx 是服务端的事，会自己好。为它弹全屏提醒只会训练用户忽略提醒。
        var (client, _) = ClientReturning("{}", HttpStatusCode.InternalServerError);

        var result = await client.JudgeAsync(Provider(ApiFormat.OpenAi), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.NoAnswer, result.Outcome);
    }

    [Fact]
    public async Task A_wrong_address_is_not_reported_as_a_network_problem()
    {
        // 这是配服务商时最常见的一种错，而它以前被归进"没网"那一类 —— 于是界面上说
        // "连不上（没网、超时……）"，人就去查网络，而真正的问题是地址少了一段路径。
        var (client, _) = ClientReturning("{}", HttpStatusCode.NotFound);

        var result = await client.JudgeAsync(Provider(ApiFormat.Anthropic), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.NeedsAttention, result.Outcome);
        Assert.Contains("404", result.Message);
    }

    [Fact]
    public async Task A_rejected_request_body_is_not_reported_as_a_network_problem()
    {
        var (client, _) = ClientReturning("{}", HttpStatusCode.BadRequest);

        var result = await client.JudgeAsync(Provider(ApiFormat.OpenAi), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.NeedsAttention, result.Outcome);
        Assert.Contains("400", result.Message);
    }

    [Fact]
    public async Task A_200_with_nothing_readable_says_so_and_shows_what_came_back()
    {
        // 以前这一种也显示成"连不上"，而其实连通了 —— 只是回来的东西不是判定。
        var (client, _) = ClientReturning("<html>502 Bad Gateway</html>");

        var result = await client.JudgeAsync(Provider(ApiFormat.OpenAi), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.NoAnswer, result.Outcome);
        Assert.Contains("200", result.Message);
        Assert.Contains("502 Bad Gateway", result.Message);
    }

    [Fact]
    public async Task A_flaky_tls_handshake_is_retried_once_instead_of_being_reported()
    {
        // 实测过：同一台机器、同一个地址，curl 连着三次都是 200，而 .NET 这边时不时报
        // "The SSL connection could not be established"。一次握手抖动不该让用户看到"连不上"
        // —— 他只会以为是自己配错了。
        var handler = new FlakyHandler(failFirst: 1, """{"choices":[{"message":{"content":"[false, \"\"]"}}]}""");
        var client = new ModelClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) });

        var result = await client.JudgeAsync(Provider(ApiFormat.OpenAi), "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.Answered, result.Outcome);
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task A_connection_that_keeps_failing_is_reported_with_the_whole_exception_chain()
    {
        // 外层那句 "see inner exception" 本身什么都没说 —— 原因在内层，要一起带出来。
        var handler = new FlakyHandler(failFirst: 99, "{}");
        var client = new ModelClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) });

        var result = await client.JudgeAsync(Provider(ApiFormat.OpenAi), "x", Snapshot(), default);

        // 归"连不上"这一类（不是"没网"）：要不要为它吵，还要看那地址是不是你自己的服务。
        Assert.Equal(ModelCallOutcome.Unreachable, result.Outcome);
        Assert.Contains("SSL", result.Message);

        // 只重试一次：反复重试会把"慢"变成"卡"。
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task A_provider_with_no_endpoint_is_a_configuration_problem()
    {
        var (client, _) = ClientReturning("{}");
        var provider = Provider(ApiFormat.OpenAi);
        provider.Endpoint = "";

        var result = await client.JudgeAsync(provider, "x", Snapshot(), default);

        Assert.Equal(ModelCallOutcome.NeedsAttention, result.Outcome);
    }

    // ------------------------------------------------------------------ 缓存键：怎么做到不重复花钱

    [Fact]
    public async Task The_same_address_reuses_its_verdict_instead_of_asking_again()
    {
        // 内容指纹里含正文，而 Codex、豆包这类窗口的正文**一直在变**（流式输出、计时器、动画）
        // —— 每变一次就是一个新指纹。不按地址复用的话，每一轮都会发一次请求，
        // 一秒一变就是一分钟六十次，那正是"token 消耗太高"的来源。
        var (client, handler) = ClientReturning(
            """{"choices":[{"message":{"content":"[true, \"分心\"]"}}]}""");

        var config = new AppConfig();
        config.Models.Add(new ModelProvider
        {
            Id = "m",
            Name = "m",
            Format = ApiFormat.OpenAi,
            Endpoint = "https://api.example.com/v1/chat/completions",
            Model = "some-model",
            ApiKey = "k",
        });
        config.ActiveModelId = "m";

        var supervisor = new ModelSupervisor(() => config, client);

        var first = new ContentSnapshot("https://bilibili.com/", "首页", "内容第一版", DateTimeOffset.Now);
        Assert.Equal(ModelVerdictKind.Unknown, supervisor.Lookup("r", first).Kind);

        supervisor.Request("r", "禁止刷短视频", first);
        await WaitForAnswer(supervisor);

        Assert.Equal(1, handler.Attempts);

        // 同一个地址、正文变了 —— 页面自己在动，不等于结论变了。沿用上一次的，不再花钱。
        var churned = first with { BodyText = "内容第二版（又多了一条推荐）" };
        var reused = supervisor.Lookup("r", churned);

        Assert.Equal(ModelVerdictKind.Blocked, reused.Kind);
        Assert.Equal("分心", reused.Reason);
        Assert.Equal(1, handler.Attempts);

        // 换了地址（点进另一个视频）算新问题，该重新判。
        var elsewhere = new ContentSnapshot("https://bilibili.com/video/BV1", "某个视频", "内容", DateTimeOffset.Now);
        Assert.Equal(ModelVerdictKind.Unknown, supervisor.Lookup("r", elsewhere).Kind);
    }

    /// <summary>等后台那次判定落地 —— 判定是发出去就不管的，所以要轮一下。</summary>
    private static async Task WaitForAnswer(ModelSupervisor supervisor)
    {
        for (var i = 0; i < 60 && supervisor.LastAnswered is null; i++)
            await Task.Delay(50);
    }

    [Fact]
    public void The_same_page_fingerprints_the_same_and_a_changed_one_does_not()
    {
        Assert.Equal(ContentFingerprint.Of(Snapshot()), ContentFingerprint.Of(Snapshot()));

        Assert.NotEqual(
            ContentFingerprint.Of(Snapshot()),
            ContentFingerprint.Of(Snapshot(body: "换了一段正文")));
    }

    // ------------------------------------------------------------------ 放宽判定

    private static AppConfig WithSupervision(string? activeId)
    {
        var config = new AppConfig();
        config.Models.Add(new ModelProvider
        {
            Id = "m1", Name = "m1", Endpoint = "https://x/v1/chat/completions", Model = "x",
        });

        config.ActiveModelId = activeId;
        return config;
    }

    [Fact]
    public void Turning_model_supervision_off_is_a_relaxation()
    {
        // 这是模型监督唯一能被绕开的地方：清空 ActiveModelId 等于关掉所有模型规则，
        // 而界面上一切照旧 —— 比改规则隐蔽得多，因为改规则至少在列表里看得见。
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(WithSupervision("m1"), WithSupervision(null)));
    }

    [Fact]
    public void Turning_it_on_is_a_tightening()
    {
        Assert.Equal(ChangeDirection.Tightening,
            ConfigRelaxation.Classify(WithSupervision(null), WithSupervision("m1")));
    }

    [Fact]
    public void Switching_between_providers_is_not_a_relaxation()
    {
        // 换地址、换 key、换模型名都只是"用哪条管道"。把它也算成放宽，会让换一次 key
        // 都要去拿令牌 —— 而那件事既不削弱也不加强任何监督。
        var before = WithSupervision("m1");
        var after = WithSupervision("m1");
        after.Models[0].ApiKey = "另一个 key";
        after.Models[0].Endpoint = "https://y/v1/chat/completions";
        after.Models[0].Model = "另一个模型";

        Assert.Equal(ChangeDirection.Neutral, ConfigRelaxation.Classify(before, after));
    }

    // ------------------------------------------------------------------ 规则引擎那一支

    private sealed class FakeSupervisor : IModelSupervisor
    {
        private ModelVerdict _verdict;

        public FakeSupervisor(ModelVerdict verdict) => _verdict = verdict;

        public int Requests { get; private set; }
        public string? Asked { get; private set; }

        public ModelVerdict Lookup(string ruleId, ContentSnapshot snapshot) => _verdict;

        public void Request(string ruleId, string instruction, ContentSnapshot snapshot)
        {
            Requests++;
            Asked = instruction;
        }

        public int VerdictsVersion { get; private set; }

        /// <summary>模拟"异步判定回来了"：换个结论，并把版本推进一格。</summary>
        public void Land(ModelVerdict verdict)
        {
            _verdict = verdict;
            VerdictsVersion++;
        }
    }

    private static Target TargetWithModelRule() => new()
    {
        Id = "t",
        DisplayName = "t",
        ProcessNames = { "msedge" },
        Rules =
        {
            new Rule
            {
                Id = "r",
                Name = "模型规则",
                PolicyId = "p",
                Enabled = true,
                Match = new MatchSpec
                {
                    Type = MatchKind.Model,
                    Patterns = { "禁止看与工作无关的短视频" },
                },
            },
        },
    };

    [Fact]
    public void A_blocked_verdict_blocks_and_carries_the_reason()
    {
        var engine = new RuleEngine([], new FakeSupervisor(new ModelVerdict(ModelVerdictKind.Blocked, "在摸鱼")));

        var verdict = engine.Evaluate(TargetWithModelRule(), Snapshot(), DateTimeOffset.Now);

        Assert.True(verdict.IsBlocked);

        // 原因要能传到提醒里去 —— 用户写的是"禁止看短视频"，模型回的是"在摸鱼"，
        // 后者才是当下这一页到底怎么回事。
        Assert.Contains("在摸鱼", verdict.Reason);
    }

    [Fact]
    public void An_unjudged_page_is_allowed_for_now_and_asked_about()
    {
        var supervisor = new FakeSupervisor(ModelVerdict.Unknown);
        var engine = new RuleEngine([], supervisor);

        var verdict = engine.Evaluate(TargetWithModelRule(), Snapshot(), DateTimeOffset.Now);

        // 先放行：等一次几秒的调用会把整个监控循环卡住，而那期间最小化、关窗全都停摆。
        Assert.False(verdict.IsBlocked);

        // 但要真的去问 —— 否则这条规则永远不会生效。
        Assert.Equal(1, supervisor.Requests);
        Assert.Equal("禁止看与工作无关的短视频", supervisor.Asked);
    }

    [Fact]
    public void An_allowed_verdict_asks_nothing()
    {
        var supervisor = new FakeSupervisor(ModelVerdict.Allowed);
        var engine = new RuleEngine([], supervisor);

        Assert.False(engine.Evaluate(TargetWithModelRule(), Snapshot(), DateTimeOffset.Now).IsBlocked);
        Assert.Equal(0, supervisor.Requests);
    }

    [Fact]
    public void Without_a_supervisor_a_model_rule_does_nothing_at_all()
    {
        // 没接监督的时候这条规则当它不存在 —— 而不是当成"命中"。
        // 当成命中的话，一个还没配好服务商的规则会立刻开始拦。
        var engine = new RuleEngine([]);

        Assert.False(engine.Evaluate(TargetWithModelRule(), Snapshot(), DateTimeOffset.Now).IsBlocked);
    }

    [Fact]
    public void Rules_still_run_in_priority_order_around_a_model_rule()
    {
        // 模型规则排在中间：比它高的关键词规则要先判，比它低的要能兜底。
        var target = TargetWithModelRule();
        target.Rules.Insert(0, new Rule
        {
            Id = "high",
            Name = "高优先",
            PolicyId = "p",
            Enabled = true,
            Priority = 100,
            // Fields 留空只查网址和标题，不查正文 —— 这里要的是正文，就得写出来。
            Match = new MatchSpec { Type = MatchKind.Keyword, Fields = { MatchField.Body }, Patterns = { "锁机" } },
        });

        var engine = new RuleEngine([], new FakeSupervisor(ModelVerdict.Unknown));

        Assert.True(engine.Evaluate(target, Snapshot(body: "正文含锁机"), DateTimeOffset.Now).IsBlocked);
        Assert.False(engine.Evaluate(target, Snapshot(body: "普通正文"), DateTimeOffset.Now).IsBlocked);
    }
}
