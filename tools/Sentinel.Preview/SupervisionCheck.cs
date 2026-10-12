using System.IO;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Config;
using Sentinel.Core.Engine;
using Sentinel.Core.Model;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;

namespace Sentinel.Preview;

/// <summary>
/// 模型监督**端到端**：从"一个窗口里有这么一页"一直到"真的拦下来"。
///
/// <para>
/// 这是唯一从来没验过的一层。API 那一层验得很细（思考模型的解析、失败分类、取列表、重试），
/// 但中间还隔着规则引擎、监督调度、内容指纹、策略动作 —— 任何一处错位，表现都是
/// "配了模型监督，打开那个网站什么也没发生"，而且没有报错、没有日志、界面上看不出区别。
/// </para>
/// <para>
/// UIA 读取那一段用假的（这里没法驱动真的浏览器），**其余每一层都是真的**，包括那次网络请求。
/// 服务商用你自己配置里的那个 —— 所以这里验的就是你手上那套能不能跑通。
/// </para>
/// <para>
/// 不写任何配置文件、不启动服务，只读一次你的配置拿服务商。用完即走。
/// </para>
/// </summary>
internal static class SupervisionCheck
{
    /// <summary>被监控的那一页：一个游戏搜索页。挑它是因为它够典型 —— 网址里就有"游戏"两个字。</summary>
    private static readonly ContentSnapshot GamePage = new(
        "https://www.bing.com/search?q=%E6%B8%B8%E6%88%8F",
        "游戏 - 搜索",
        "游戏排行榜 2026 · 最新游戏攻略 · 单机游戏下载 · 网页游戏在线玩 · 游戏资讯与评测",
        DateTimeOffset.Now);

    public static int Run()
    {
        // 只读，不改。服务商从你装好的那份配置里取 —— 这样验的就是你手上这一套。
        var realPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sentinel", "config.json");

        if (!File.Exists(realPath))
            realPath = @"E:\Sentinel\config.json";

        if (!ConfigStore.TryLoad(realPath, out var installed, out var loadError))
        {
            Console.WriteLine($"读不到你的配置（{realPath}）：{loadError}  —— 用 --provider 手动指定吧。");
            return 1;
        }

        var activeId = installed.ActiveModelId;
        var provider = installed.Models.FirstOrDefault(m => m.Id == activeId);

        if (provider is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            Console.WriteLine("你的配置里没有启用中的服务商（或者它没有 key）。先去「大语言模型」页配一个。");
            return 1;
        }

        Console.WriteLine("用你配置里的服务商（key 不打印）：");
        Console.WriteLine($"  名称  : {provider.Name}");
        Console.WriteLine($"  地址  : {provider.Endpoint}");
        Console.WriteLine($"  模型  : {provider.Model}");
        Console.WriteLine($"  格式  : {provider.Format}");
        Console.WriteLine();

        var config = BuildConfig(provider);
        var action = new RecordingAction();
        var enumerator = new OneWindow();
        var reader = new FixedReader(GamePage);

        var supervisor = new ModelSupervisor(() => config);

        var loop = new MonitorLoop(
            enumerator,
            reader,
            new ActionController(),
            new Dictionary<ActionType, IAction>
            {
                [ActionType.Notify] = action,
                [ActionType.Minimize] = action,
            },
            new SilentAudit(),
            config,
            configPath: null,
            models: supervisor);

        Console.WriteLine($"窗口：msedge 在前台，页面 = {GamePage.Url}");
        Console.WriteLine("规则：模型监督 —— 「和游戏有关的页面」");
        Console.WriteLine();
        Console.WriteLine("开始轮询（判定是异步的，第一轮一定放行，看它几秒后会不会拦）：");

        var now = DateTimeOffset.Now;
        var blockedAt = (int?)null;
        var deadline = DateTime.UtcNow.AddSeconds(45);

        for (var tick = 1; DateTime.UtcNow < deadline; tick++)
        {
            var outcomes = loop.Tick(now.AddSeconds(tick * 2));
            var blocked = outcomes.FirstOrDefault(o => o.Verdict.IsBlocked);

            if (blocked is not null)
            {
                blockedAt = tick;
                Console.WriteLine($"  第 {tick} 轮：**拦下来了** —— 原因「{blocked.Verdict.Reason}」，执行了 {blocked.Detail}");
                break;
            }

            // 每轮之间等一会儿：判定在后台跑，不等的话会连着空转很多轮，看不出在等什么。
            Thread.Sleep(1000);
        }

        var calls = action.Calls.Count;

        // 分出两件完全不同的事：模型说"不违规"（正常，只是我挑的页面不够典型），
        // 还是模型说"违规"而链条没把它变成拦截（那是真 bug）。
        var held = supervisor.Lookup("r", GamePage);

        Console.WriteLine();
        Console.WriteLine($"监督层现在记着的判定: {held.Kind}"
                          + (held.Reason is null ? "" : $"  原因：「{held.Reason}」"));
        Console.WriteLine($"模型调用过吗（监督层记录）: {(supervisor.LastAnswered is null ? "没有 —— 请求根本没发出去" : supervisor.LastAnswered.ToString())}");

        if (supervisor.BlockingIssue is { } issue)
            Console.WriteLine($"监督层报告的问题: {issue}");

        Console.WriteLine($"执行过的动作次数: {calls}");

        var ok = held.IsBlocked && calls > 0;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论：模型监督这条链是通的 —— 页面被判违规，并且真的执行了策略。"
            : held.Kind == ModelVerdictKind.Allowed
                ? "结论：链是通的（模型答了、判定也存下来了），只是这次它判的是「不违规」 —— 换一个更典型的页面再试。"
                : "结论：这条链没走通，见上面（判定没回来 / 没拦 / 没执行）。");

        return ok ? 0 : 1;
    }

    private static AppConfig BuildConfig(ModelProvider provider)
    {
        var config = new AppConfig();
        config.Ui.DryRun = false;

        config.Models.Add(provider);
        config.ActiveModelId = provider.Id;

        config.Policies.Add(new Policy
        {
            Id = "p",
            Name = "提醒并最小化",
            Actions = { ActionType.Notify, ActionType.Minimize },
            GraceSeconds = 0,
            CooldownSeconds = 0,
            DebounceCount = 1,
            AlertStyle = AlertStyle.Balloon,
        });

        config.Targets.Add(new Target
        {
            Id = "edge",
            DisplayName = "Edge",
            ProcessNames = { "msedge" },
            // 正文开着：模型要看的是页面内容，只有网址的话它只能猜。
            Reader = new ReaderOptions { ScanUrl = true, ScanTitle = true, ScanBody = true, IntervalMs = 0 },
            Rules =
            {
                new Rule
                {
                    Id = "r",
                    Name = "模型监督：游戏页",
                    Enabled = true,
                    PolicyId = "p",
                    Match = new MatchSpec
                    {
                        Type = MatchKind.Model,
                        Fields = { MatchField.Url, MatchField.Body },
                        Patterns = { "和游戏有关的页面，尤其是攻略、排行榜和搜索页" },
                    },
                },
            },
        });

        return config;
    }

    private sealed class OneWindow : IWindowEnumerator
    {
        public IReadOnlyList<WindowRef> Enumerate() => new[]
        {
            new WindowRef(4242, "msedge", "游戏 - 搜索", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                0xED6E, IsMinimized: false, IsForeground: true),
        };
    }

    private sealed class FixedReader(ContentSnapshot snapshot) : IContentReader
    {
        public bool IsAvailable() => true;
        public ReaderCapabilities Capabilities => new(true, true, true);
        public ContentSnapshot? Read(WindowRef window, ReaderOptions? options = null) => snapshot;
    }

    private sealed class RecordingAction : IAction
    {
        public List<ActionContext> Calls { get; } = new();

        public string Name => "recording";
        public bool CanRun(ActionContext context) => true;
        public void Execute(ActionContext context) => Calls.Add(context);
    }

    private sealed class SilentAudit : IAuditLog
    {
        public void Write(AuditEntry entry) { }
    }
}
