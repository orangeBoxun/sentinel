using Sentinel.Core.Abstractions;
using Sentinel.Core.Engine;
using Sentinel.Core.Logging;
using Sentinel.Core.Model;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// Integration tests for the full Verdict -> Policy -> Action chain, per the handoff doc §19:
/// a mock IContentReader feeds simulated windows, and a recording IAction captures the calls.
/// </summary>
public class MonitorLoopTests
{
    private sealed class FakeEnumerator : IWindowEnumerator
    {
        private readonly List<WindowRef> _windows;

        public FakeEnumerator(params WindowRef[] windows) => _windows = windows.ToList();

        public IReadOnlyList<WindowRef> Enumerate() => _windows;
    }

    private sealed class FakeReader : IContentReader
    {
        private readonly ContentSnapshot? _snapshot;

        public FakeReader(ContentSnapshot? snapshot) => _snapshot = snapshot;

        /// <summary>读了几次 —— "压根没读它"这件事得能证明，不能靠"没发生什么"来推。</summary>
        public int Reads { get; private set; }

        public bool IsAvailable() => true;
        public ReaderCapabilities Capabilities => new(true, true, true);

        public ContentSnapshot? Read(WindowRef window, ReaderOptions? options = null)
        {
            Reads++;
            return _snapshot;
        }
    }

    private sealed class MutableEnumerator : IWindowEnumerator
    {
        public List<WindowRef> Windows { get; } = new();

        public IReadOnlyList<WindowRef> Enumerate() => Windows;
    }

    private sealed class MutableReader : IContentReader
    {
        public ContentSnapshot? Snapshot { get; set; }
        public int ReadCount { get; private set; }

        public bool IsAvailable() => true;
        public ReaderCapabilities Capabilities => new(true, true, true);

        public ContentSnapshot? Read(WindowRef window, ReaderOptions? options = null)
        {
            ReadCount++;
            return Snapshot;
        }
    }

    private sealed class RecordingAction : IAction
    {
        public List<ActionContext> Calls { get; } = new();

        public string Name => "recording";
        public bool CanRun(ActionContext context) => true;
        public void Execute(ActionContext context) => Calls.Add(context);
    }

    /// <summary>
    /// 测试里那个"用户看得见的窗口"。
    ///
    /// 默认**露在屏幕上**（在前台、没最小化）。循环现在的判据是"看得见就得管"：
    /// 前台那一个、以及**任何露在屏幕上的**窗口都读；被完全盖住的、最小化的不读
    /// （看不见的东西影响不到任何人，读它只是白花 UIA 的时间）。要测那两种情形，
    /// 用例里显式写 `IsExposed = false` / `IsMinimized = true`。
    /// </summary>
    private static readonly WindowRef Browser = new(
        1234, "Codex", "My Page", @"C:\app\Codex.exe", 0xABCD, IsMinimized: false, IsForeground: true);

    private static AppConfig ConfigWith(string urlPattern, ActionType[] actions, int grace = 0, int cooldown = 0)
    {
        var config = new AppConfig();
        config.Policies.Add(new Policy
        {
            Id = "p1",
            Name = "block",
            Actions = actions.ToList(),
            GraceSeconds = grace,
            CooldownSeconds = cooldown,
            DebounceCount = 1,
        });
        config.Targets.Add(new Target
        {
            Id = "codex",
            DisplayName = "Codex",
            ProcessNames = { "Codex" },
            // Interval 0: these tests drive time explicitly, so per-target pacing stays out of the way.
            Reader = new ReaderOptions { IntervalMs = 0 },
            Rules =
            {
                new Rule
                {
                    Id = "r1",
                    Name = "bilibili",
                    PolicyId = "p1",
                    Match = new MatchSpec
                    {
                        Type = MatchKind.Keyword,
                        Fields = { MatchField.Url },
                        Patterns = { urlPattern },
                    },
                },
            },
        });
        return config;
    }

    private static MonitorLoop BuildLoop(
        AppConfig config,
        IContentReader reader,
        RecordingAction action,
        IWindowEnumerator? enumerator = null,
        bool dryRun = false,
        string? configPath = null)
    {
        var controller = new ActionController();
        var actions = new Dictionary<ActionType, IAction> { [ActionType.Close] = action };

        // Safe mode now lives on the config the loop holds.
        config.Ui.DryRun = dryRun;

        return new MonitorLoop(
            enumerator ?? new FakeEnumerator(Browser),
            reader,
            controller,
            actions,
            new NullAuditLog(),
            config,
            configPath);
    }

    [Fact]
    public void A_paused_rule_is_not_enforced()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Close });
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var action = new RecordingAction();

        var directory = Path.Combine(Path.GetTempPath(), "sentinel-pauseloop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");

        try
        {
            var now = DateTimeOffset.Now;
            var rule = config.Targets[0].Rules[0];
            rule.Pause = new PauseOptions { Enabled = true };

            var state = PauseState.Load(configPath);
            state.Record(rule.Id, rule.Pause, now);
            state.Save(configPath);

            var loop = BuildLoop(config, reader, action, configPath: configPath);

            // 暂停挂在规则上：目标照样读、照样判定，只是这一条不动手，也不记审计。
            // 同一个目标下的其他规则不受影响 —— 那正是"按规则"而不是"按目标"的意义。
            Assert.Empty(loop.Tick(now));
            Assert.Empty(action.Calls);

            // And it comes back by itself, with nothing to undo.
            Assert.NotEmpty(loop.Tick(now.AddMinutes(11)));
            Assert.NotEmpty(action.Calls);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Pausing_ends_the_episode_that_was_already_in_flight()
    {
        // Grace in flight: the first tick only warns, the enforcement is still to come.
        var config = ConfigWith("bilibili", new[] { ActionType.Close }, grace: 60);
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action);

        var now = DateTimeOffset.Now;
        loop.Tick(now);
        Assert.True(loop.PendingEpisodeCount > 0);

        // A pause taken now must not leave that episode sitting there to fire the moment the break
        // ends — that would make pausing worse than not pausing.
        loop.ForgetRule(config.Targets[0].Rules[0].Id);

        Assert.Equal(0, loop.PendingEpisodeCount);
    }

    [Fact]
    public void Blocked_url_enforces_for_zero_grace()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Close });
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action);

        var outcomes = loop.Tick(DateTimeOffset.Now);

        Assert.Single(action.Calls);
        Assert.Equal(ActionStage.Enforce, Assert.Single(outcomes).Stage);
    }

    [Fact]
    public void Allowed_url_does_nothing()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Close });
        var reader = new FakeReader(new ContentSnapshot("https://example.com", "Example", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action);

        Assert.Empty(loop.Tick(DateTimeOffset.Now));
        Assert.Empty(action.Calls);
    }

    [Fact]
    public void Dry_run_logs_but_never_executes()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Close });
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action, dryRun: true);

        var outcomes = loop.Tick(DateTimeOffset.Now);

        Assert.Empty(action.Calls);
        Assert.Contains("dry-run", Assert.Single(outcomes).Detail);
    }

    [Fact]
    public void Grace_defers_enforcement_until_the_window_elapses()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Close }, grace: 10);
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action);
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(ActionStage.Notify, Assert.Single(loop.Tick(t0)).Stage);
        Assert.Empty(loop.Tick(t0.AddSeconds(5)));          // still inside grace
        Assert.Empty(action.Calls);
        Assert.Equal(ActionStage.Enforce, Assert.Single(loop.Tick(t0.AddSeconds(10))).Stage);
        Assert.Single(action.Calls);
    }

    [Fact]
    public void Unchanged_content_is_not_rejudged()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Close }, cooldown: 0);
        var reader = new MutableReader
        {
            Snapshot = new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now),
        };
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action);
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Single(loop.Tick(t0));
        // Identical snapshot: the loop skips it rather than firing the cooldown-free action again.
        Assert.Empty(loop.Tick(t0.AddSeconds(1)));
        Assert.Single(action.Calls);
    }

    [Fact]
    public void Non_matching_process_is_ignored()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Close });
        var other = new WindowRef(99, "notepad", "Untitled", "", 0x1111);
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action, new FakeEnumerator(other));

        Assert.Empty(loop.Tick(DateTimeOffset.Now));
    }

    [Fact]
    public void Target_interval_paces_reading()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Close });
        config.Targets[0].Reader.IntervalMs = 30_000;
        var reader = new MutableReader
        {
            Snapshot = new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now),
        };
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action);
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Single(loop.Tick(t0));
        Assert.Equal(1, reader.ReadCount);

        // Not due yet: the target is skipped outright, so the window is not even read.
        Assert.Empty(loop.Tick(t0.AddSeconds(5)));
        Assert.Equal(1, reader.ReadCount);

        loop.Tick(t0.AddSeconds(31));
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public void App_style_target_with_constant_url_and_title_reacts_to_body_changes()
    {
        // Codex has no address bar: URL is always null and the window title is always "ChatGPT".
        // Only the body text can signal that the content changed.
        var config = ConfigWith("bilibili", new[] { ActionType.Close });
        config.Targets[0].Reader.ScanBody = true;
        config.Targets[0].Rules[0].Match = new MatchSpec
        {
            Type = MatchKind.Keyword,
            Fields = { MatchField.Body },
            Patterns = { "锁机" },
        };

        var reader = new MutableReader
        {
            Snapshot = new ContentSnapshot(null, "ChatGPT", "帮我找一下锁机软件", DateTimeOffset.Now),
        };
        var action = new RecordingAction();
        var loop = BuildLoop(config, reader, action);
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Single(loop.Tick(t0));
        Assert.Single(action.Calls);

        // Body edited to something harmless: the app target must re-judge and stop firing.
        reader.Snapshot = new ContentSnapshot(null, "ChatGPT", "今天天气不错", DateTimeOffset.Now);
        Assert.Empty(loop.Tick(t0.AddSeconds(1)));
    }

    [Fact]
    public void State_is_pruned_for_windows_that_disappear()
    {
        // A different window handle every tick, as when the user opens and closes windows all day.
        // Without pruning, the content cache and the action controller's episodes both grow for the
        // lifetime of the process — nothing ever removes an entry for a window that simply closed.
        var config = ConfigWith("bilibili", new[] { ActionType.Close }, grace: 10);
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var enumerator = new MutableEnumerator();
        var loop = BuildLoop(config, reader, action, enumerator);
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 300; i++)
        {
            enumerator.Windows.Clear();
            enumerator.Windows.Add(new WindowRef(1000 + i, "Codex", "title", "", 0x1000 + i, IsForeground: true));
            loop.Tick(t0.AddSeconds(i));
        }

        Assert.Equal(1, loop.TrackedWindowCount);
        Assert.Equal(1, loop.PendingEpisodeCount);
    }

    [Fact]
    public void A_minimized_window_is_left_alone()
    {
        // Starting the Agent — or saving the config — rebuilds the monitor and empties the content
        // cache, so every blocked window is judged afresh. Without this guard every rule fired again
        // at once, including against windows the user had already put away, which reads as "it keeps
        // triggering on a minimized window".
        var config = ConfigWith("bilibili", new[] { ActionType.Close });
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var enumerator = new MutableEnumerator();
        enumerator.Windows.Add(new WindowRef(1234, "Codex", "title", "", 0xABCD, IsMinimized: true));
        var loop = BuildLoop(config, reader, action, enumerator);

        Assert.Empty(loop.Tick(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero)));
        Assert.Empty(action.Calls);
    }

    [Fact]
    public void Editing_one_target_does_not_re_fire_the_others()
    {
        // Two targets, same blocked content, no cooldown. Before selective invalidation, applying any
        // config rebuild re-judged everything and every rule fired again — a burst of notifications
        // for pages nobody had touched.
        var before = TwoTargets("A rule");
        var after = TwoTargets("A rule, renamed");

        var reader = new FakeReader(new ContentSnapshot("https://x.com", "x", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var enumerator = new MutableEnumerator();
        enumerator.Windows.Add(new WindowRef(100, "Alpha", "t", "", 0x1, IsForeground: true));
        enumerator.Windows.Add(new WindowRef(200, "Beta", "t", "", 0x2, IsForeground: true));

        var loop = new MonitorLoop(enumerator, reader, new ActionController(),
            new Dictionary<ActionType, IAction> { [ActionType.Close] = action },
            new NullAuditLog(), before);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(2, loop.Tick(t0).Count);
        Assert.Equal(2, action.Calls.Count);

        // Only target A's rule changed: A is re-judged and acts again, B is left alone.
        loop.UpdateConfig(after);
        Assert.Single(loop.Tick(t0.AddSeconds(1)));
        Assert.Equal(3, action.Calls.Count);

        // A change to the policy both targets share does re-judge both. (Editing the policy's
        // cooldown here instead would prove the opposite: a long cooldown still suppresses them.)
        var sharedPolicyEdited = TwoTargets("A rule, renamed");
        sharedPolicyEdited.Policies[0].Name = "renamed policy";
        loop.UpdateConfig(sharedPolicyEdited);

        Assert.Equal(2, loop.Tick(t0.AddSeconds(2)).Count);
        Assert.Equal(5, action.Calls.Count);
    }

    [Fact]
    public void Leaving_safe_mode_is_not_delayed_by_the_cooldown_it_accrued()
    {
        // Safe mode "enforces" on paper. If those simulated enforcements start a real cooldown, then
        // arming the monitor leaves the offending window alone for the rest of the cooldown.
        var config = TwoTargets("A rule");
        config.Policies[0].CooldownSeconds = 300;
        config.Ui.DryRun = true;

        var reader = new FakeReader(new ContentSnapshot("https://x.com", "x", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var enumerator = new MutableEnumerator();
        enumerator.Windows.Add(new WindowRef(100, "Alpha", "t", "", 0x1, IsForeground: true));

        var loop = new MonitorLoop(enumerator, reader, new ActionController(),
            new Dictionary<ActionType, IAction> { [ActionType.Close] = action },
            new NullAuditLog(), config);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Single(loop.Tick(t0));          // dry run: reported, nothing executed
        Assert.Empty(action.Calls);

        config.Ui.DryRun = false;              // the user arms it from the UI, editing in place
        loop.UpdateConfig(config);
        loop.Tick(t0.AddSeconds(1));

        Assert.Single(action.Calls);
    }

    private sealed class RecordingNotify : IAction
    {
        public List<string?> Reports { get; } = new();

        public string Name => "notify";
        public bool CanRun(ActionContext context) => true;
        public void Execute(ActionContext context) => Reports.Add(context.ExecutedSummary);
    }

    private sealed class ThrowingAction : IAction
    {
        public string Name => "boom";
        public bool CanRun(ActionContext context) => true;
        public void Execute(ActionContext context) => throw new InvalidOperationException("拒绝访问");
    }

    [Fact]
    public void The_notice_is_told_what_the_other_actions_actually_did()
    {
        // It used to be emitted in policy order, so it could only recite the configured list — claiming
        // a Close had happened even when the app ignored it.
        var config = ConfigWith("bilibili", new[] { ActionType.Notify, ActionType.Close });
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var closer = new RecordingAction();
        var notify = new RecordingNotify();

        var enumerator = new MutableEnumerator();
        enumerator.Windows.Add(Browser);

        var loop = new MonitorLoop(enumerator, reader, new ActionController(),
            new Dictionary<ActionType, IAction>
            {
                [ActionType.Close] = closer,
                [ActionType.Notify] = notify,
            },
            new NullAuditLog(), config);

        loop.Tick(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));

        Assert.Single(closer.Calls);
        Assert.Equal("已执行：关闭窗口", Assert.Single(notify.Reports));
    }

    [Fact]
    public void The_notice_admits_when_an_action_failed()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Notify, ActionType.Close });
        var reader = new FakeReader(new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now));
        var notify = new RecordingNotify();

        var enumerator = new MutableEnumerator();
        enumerator.Windows.Add(Browser);

        var loop = new MonitorLoop(enumerator, reader, new ActionController(),
            new Dictionary<ActionType, IAction>
            {
                [ActionType.Close] = new ThrowingAction(),
                [ActionType.Notify] = notify,
            },
            new NullAuditLog(), config);

        var outcome = Assert.Single(loop.Tick(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero)));

        var report = Assert.Single(notify.Reports)!;
        Assert.Contains("失败", report);
        Assert.Contains("拒绝访问", report);
        Assert.Contains("失败", outcome.Detail);
    }

    [Fact]
    public void A_model_verdict_that_lands_later_still_blocks_the_page_that_never_changed()
    {
        // 最难发现的一个错，出在"同步查、异步判"这个分工上：
        //
        //   第 1 轮：内容没见过 → 问引擎 → 判定还在路上 → 规则回"放行"，循环把这条记进缓存
        //   第 2 轮起：内容没变 → 直接用缓存那条"放行" → **模型后来判的"违规"永远没人去读**
        //
        // 表现就是"配了模型监督，打开那个网站什么也没发生"，而且没有任何报错、没有日志。
        // 真机上要靠页面自己又变了一次才会撞对，所以会显得时灵时不灵。
        var supervisor = new LocalSupervisor(ModelVerdict.Unknown);

        var config = new AppConfig();
        config.Policies.Add(new Policy
        {
            Id = "p",
            Name = "拦下来",
            Actions = { ActionType.Close },
            GraceSeconds = 0,
            CooldownSeconds = 0,
            DebounceCount = 1,
        });
        config.Targets.Add(new Target
        {
            Id = "edge",
            DisplayName = "Edge",
            ProcessNames = { "Codex" },
            Reader = new ReaderOptions { IntervalMs = 0 },
            Rules =
            {
                new Rule
                {
                    Id = "r",
                    Name = "模型监督",
                    Enabled = true,
                    PolicyId = "p",
                    Match = new MatchSpec { Type = MatchKind.Model, Fields = { MatchField.Url }, Patterns = { "和游戏有关" } },
                },
            },
        });

        var reader = new MutableReader { Snapshot = BlockedPage };
        var action = new RecordingAction();
        var enumerator = new MutableEnumerator { Windows = { Browser } };

        var loop = new MonitorLoop(enumerator, reader, new ActionController(),
            new Dictionary<ActionType, IAction> { [ActionType.Close] = action },
            new NullAuditLog(), config, null, supervisor);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        // 判定还在路上：这一轮不动手。
        Assert.Empty(loop.Tick(t0));
        Assert.Empty(action.Calls);

        // 判定回来了，说违规。**内容一点没变**。
        supervisor.Land(new ModelVerdict(ModelVerdictKind.Blocked, "游戏页"));

        Assert.Single(loop.Tick(t0.AddSeconds(1)));
        Assert.Single(action.Calls);
    }

    private sealed class LocalSupervisor : IModelSupervisor
    {
        private ModelVerdict _verdict;

        public LocalSupervisor(ModelVerdict verdict) => _verdict = verdict;

        public ModelVerdict Lookup(string ruleId, ContentSnapshot snapshot) => _verdict;

        public void Request(string ruleId, string instruction, ContentSnapshot snapshot) { }

        public int VerdictsVersion { get; private set; }

        public void Land(ModelVerdict verdict)
        {
            _verdict = verdict;
            VerdictsVersion++;
        }
    }

    private static MonitorLoop LoopWith(
        AppConfig config,
        IContentReader reader,
        IReadOnlyDictionary<ActionType, IAction> actions,
        IWindowEnumerator enumerator) =>
        new(enumerator, reader, new ActionController(), actions, new NullAuditLog(), config);

    private static readonly ContentSnapshot BlockedPage =
        new("https://bilibili.com", "首页", null, DateTimeOffset.Now);

    [Fact]
    public void A_minimized_window_is_not_even_read()
    {
        // 只看前台、没最小化的窗口。最小化的那个没人看得见，读它只是白花 UIA 的时间
        // —— 实测一次完整读取要几百毫秒，而每一轮都要遍历所有窗口。
        var config = ConfigWith("bilibili", new[] { ActionType.Close }, grace: 0);
        var reader = new MutableReader { Snapshot = BlockedPage };
        var action = new RecordingAction();
        var enumerator = new MutableEnumerator
        {
            Windows = { new WindowRef(1234, "Codex", "title", "", 0xABCD, IsMinimized: true, IsForeground: false) },
        };

        var loop = LoopWith(config, reader,
            new Dictionary<ActionType, IAction> { [ActionType.Close] = action }, enumerator);

        Assert.Empty(loop.Tick(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero)));

        // 不只是"读到了但没动手" —— 是**根本没读**。
        Assert.Equal(0, reader.ReadCount);
        Assert.Empty(action.Calls);
    }

    [Fact]
    public void Returning_to_the_foreground_re_reminds_a_reminding_policy()
    {
        // Nothing about the window changes under a notify-only policy, so "the user brought it back"
        // is the only signal that a fresh reminder is wanted.
        var config = ConfigWith("bilibili", new[] { ActionType.Notify }, grace: 0, cooldown: 300);
        var notify = new RecordingNotify();
        var enumerator = new MutableEnumerator { Windows = { Browser } };
        var loop = LoopWith(config, new FakeReader(BlockedPage),
            new Dictionary<ActionType, IAction> { [ActionType.Notify] = notify }, enumerator);

        void Show(bool foreground) => enumerator.Windows[0] = Browser with { IsForeground = foreground };

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Show(true);
        loop.Tick(t0);
        Assert.Single(notify.Reports);

        Show(false);
        loop.Tick(t0.AddSeconds(1));
        Assert.Single(notify.Reports);

        // Back in front, still blocked, and still inside a 300 s cooldown — which must not apply here.
        Show(true);
        loop.Tick(t0.AddSeconds(2));
        Assert.Equal(2, notify.Reports.Count);
    }

    [Fact]
    public void A_reminding_policy_repeats_on_its_interval_while_the_page_stays_in_front()
    {
        var config = ConfigWith("bilibili", new[] { ActionType.Notify }, grace: 0, cooldown: 0);
        config.Policies[0].RepeatSeconds = 300;

        var notify = new RecordingNotify();
        var enumerator = new MutableEnumerator { Windows = { Browser with { IsForeground = true } } };
        var loop = LoopWith(config, new FakeReader(BlockedPage),
            new Dictionary<ActionType, IAction> { [ActionType.Notify] = notify }, enumerator);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        loop.Tick(t0);
        Assert.Single(notify.Reports);

        loop.Tick(t0.AddMinutes(1));
        Assert.Single(notify.Reports);

        loop.Tick(t0.AddMinutes(5));
        Assert.Equal(2, notify.Reports.Count);

        loop.Tick(t0.AddMinutes(6));
        Assert.Equal(2, notify.Reports.Count);
    }

    [Fact]
    public void A_page_that_is_completely_covered_is_not_even_looked_at()
    {
        // 判据是"露没露在屏幕上"，不是"在不在前台"：被完全盖住的窗口一点都不露，
        // 对任何人都没有影响，读它只是白花 UIA 的时间（一次几百毫秒）。
        var config = ConfigWith("bilibili", new[] { ActionType.Notify }, grace: 0, cooldown: 0);
        config.Policies[0].RepeatSeconds = 300;

        var notify = new RecordingNotify();
        var reader = new FakeReader(BlockedPage);
        var enumerator = new MutableEnumerator
        {
            Windows = { Browser with { IsForeground = false, IsExposed = false } },
        };

        var loop = LoopWith(config, reader,
            new Dictionary<ActionType, IAction> { [ActionType.Notify] = notify }, enumerator);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Empty(loop.Tick(t0));
        Assert.Empty(loop.Tick(t0.AddMinutes(10)));

        Assert.Empty(notify.Reports);
        Assert.Equal(0, reader.Reads);   // 压根没读它
    }

    [Fact]
    public void A_page_that_is_merely_in_the_background_is_still_judged()
    {
        // 这一条堵的是那个洞：摆一个小窗口压在最上面当挡箭牌，旁边露着的违规视频
        // 以前永远没人读 —— 可它明明露着、人明明在看。现在"看得见就得管"。
        var config = ConfigWith("bilibili", new[] { ActionType.Minimize }, grace: 0, cooldown: 0);

        var minimize = new RecordingAction();
        var enumerator = new MutableEnumerator { Windows = { Browser with { IsForeground = false } } };

        var loop = LoopWith(config, new FakeReader(BlockedPage),
            new Dictionary<ActionType, IAction> { [ActionType.Minimize] = minimize }, enumerator);

        var outcomes = loop.Tick(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));

        Assert.Single(outcomes);
        Assert.Single(minimize.Calls);   // 后台、但露着 → 照样动手
    }

    [Fact]
    public void A_window_that_was_covered_and_comes_back_is_judged_again()
    {
        // 把挡箭牌关掉、违规的页面重新露出来 —— 那是"又看了一次"，**哪怕内容一个字都没变**
        // 也得重判。少了这一条，被判过的页面就靠缓存蒙混过去了：盖住期间没读、内容没变、
        // 缓存直接放过。
        var config = ConfigWith("bilibili", new[] { ActionType.Notify }, grace: 0, cooldown: 0);
        config.Policies[0].RepeatSeconds = 300;

        var notify = new RecordingNotify();

        // 后台但露着，而且**一直不在前台** —— 这样第二次提醒就只可能来自"露出来了"这一条触发
        // （重复提醒要 RepeatSeconds、回到前台要 WasForeground 变化，这里两者都不成立）。
        var window = Browser with { IsForeground = false };
        var enumerator = new MutableEnumerator { Windows = { window } };

        var loop = LoopWith(config, new FakeReader(BlockedPage),
            new Dictionary<ActionType, IAction> { [ActionType.Notify] = notify }, enumerator);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        loop.Tick(t0);
        Assert.Single(notify.Reports);                                   // 露着 → 提醒

        enumerator.Windows[0] = window with { IsExposed = false };       // 被别的窗口完全盖住
        loop.Tick(t0.AddSeconds(2));
        Assert.Single(notify.Reports);                                   // 盖住期间不响

        enumerator.Windows[0] = window;                                  // 又露出来，内容一模一样
        loop.Tick(t0.AddSeconds(4));

        Assert.Equal(2, notify.Reports.Count);                           // ★ 重新判、重新响
    }

    [Fact]
    public void More_visible_windows_than_the_cap_are_all_read_within_a_few_ticks()
    {
        // 一轮最多读四个（UIA 一次几百毫秒，窗口一多一轮就赶不上间隔）。多出来的轮着来 ——
        // 关键是不能有谁**永远**读不到。
        var config = ConfigWith("bilibili", new[] { ActionType.Notify }, grace: 0, cooldown: 0);

        var enumerator = new MutableEnumerator();

        foreach (var handle in new[] { 0x1001, 0x1002, 0x1003, 0x1004, 0x1005, 0x1006 })
            enumerator.Windows.Add(Browser with { NativeHandle = handle, IsForeground = false });

        var reader = new FakeReader(BlockedPage);
        var loop = LoopWith(config, reader, new Dictionary<ActionType, IAction>(), enumerator);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        loop.Tick(t0);
        Assert.Equal(4, reader.Reads);          // 一轮不超过上限

        loop.Tick(t0.AddSeconds(2));
        Assert.Equal(8, reader.Reads);
        Assert.Equal(6, loop.TrackedWindowCount);   // 两轮之内六张都读到了
    }

    [Fact]
    public void A_policy_that_acts_on_the_window_ignores_both_foreground_triggers()
    {
        // A minimize/close policy already changes the window, and the window coming back is its own
        // signal. The foreground triggers are only for policies that change nothing.
        var config = ConfigWith("bilibili", new[] { ActionType.Close }, grace: 0, cooldown: 0);
        config.Policies[0].RepeatSeconds = 300;

        var action = new RecordingAction();
        var enumerator = new MutableEnumerator { Windows = { Browser } };
        var loop = LoopWith(config, new FakeReader(BlockedPage),
            new Dictionary<ActionType, IAction> { [ActionType.Close] = action }, enumerator);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        enumerator.Windows[0] = Browser with { IsForeground = true };
        loop.Tick(t0);
        Assert.Single(action.Calls);

        enumerator.Windows[0] = Browser with { IsForeground = false };
        loop.Tick(t0.AddSeconds(1));
        enumerator.Windows[0] = Browser with { IsForeground = true };
        loop.Tick(t0.AddSeconds(2));

        loop.Tick(t0.AddMinutes(10));

        Assert.Single(action.Calls);
    }

    [Fact]
    public void Editing_the_live_config_object_takes_effect()
    {
        // The UI edits the config instance it holds, in place, and then asks for a refresh. If the
        // loop kept that same instance, comparing "old vs new" would compare an object with itself,
        // report nothing changed, and silently keep acting on the stale cached verdict.
        var config = TwoTargets("A rule");

        var reader = new FakeReader(new ContentSnapshot("https://x.com", "x", null, DateTimeOffset.Now));
        var action = new RecordingAction();
        var enumerator = new MutableEnumerator();
        enumerator.Windows.Add(new WindowRef(100, "Alpha", "t", "", 0x1, IsForeground: true));

        var loop = new MonitorLoop(enumerator, reader, new ActionController(),
            new Dictionary<ActionType, IAction> { [ActionType.Close] = action },
            new NullAuditLog(), config);

        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        Assert.Single(loop.Tick(t0));
        Assert.Single(action.Calls);

        // Repoint the rule at a policy that does nothing — mutating the live object, as the UI does.
        config.Policies.Add(new Policy
        {
            Id = "quiet",
            Name = "quiet",
            Actions = new List<ActionType>(),
            GraceSeconds = 0,
            CooldownSeconds = 0,
        });
        config.Targets[0].Rules[0].PolicyId = "quiet";

        loop.UpdateConfig(config);
        loop.Tick(t0.AddSeconds(1));

        Assert.Single(action.Calls);
    }

    private static AppConfig TwoTargets(string firstRuleName)
    {
        var config = new AppConfig();
        config.Policies.Add(new Policy
        {
            Id = "p",
            Name = "p",
            Actions = { ActionType.Close },
            GraceSeconds = 0,
            CooldownSeconds = 0,
        });

        config.Targets.Add(Target("a", "Alpha", firstRuleName));
        config.Targets.Add(Target("b", "Beta", "B rule"));
        return config;

        static Target Target(string id, string process, string ruleName) => new()
        {
            Id = id,
            DisplayName = id,
            ProcessNames = { process },
            Reader = new ReaderOptions { IntervalMs = 0 },
            Rules =
            {
                new Rule
                {
                    Id = "r",
                    Name = ruleName,
                    PolicyId = "p",
                    Match = new MatchSpec
                    {
                        Type = MatchKind.Keyword,
                        Fields = { MatchField.Url },
                        Patterns = { "x" },
                    },
                },
            },
        };
    }

    [Fact]
    public void Reopening_a_minimized_window_is_a_new_violation()
    {
        // The bug this guards: a rule acted once, and then never again for the same page. The user
        // could simply restore the window we minimized and keep watching — indefinitely.
        var config = ConfigWith("bilibili", new[] { ActionType.Close }, grace: 0, cooldown: 300);
        var reader = new MutableReader
        {
            Snapshot = new ContentSnapshot("https://bilibili.com", "首页", null, DateTimeOffset.Now),
        };
        var action = new RecordingAction();
        var enumerator = new MutableEnumerator();
        var loop = BuildLoop(config, reader, action, enumerator);
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        // 恢复一个最小化的窗口，就是把它调到前台 —— 所以"没最小化"在这里等于"在前台"。
        // 最小化的窗口不可能是前台窗口，两个标记要一起给。
        void ShowWindow(bool minimized)
        {
            enumerator.Windows.Clear();
            enumerator.Windows.Add(new WindowRef(1234, "Codex", "title", "", 0xABCD, minimized, !minimized));
        }

        ShowWindow(minimized: false);
        Assert.Single(loop.Tick(t0));
        Assert.Single(action.Calls);

        // Blocked, on screen, but deep inside a 300 s cooldown: correctly nothing more happens.
        Assert.Empty(loop.Tick(t0.AddSeconds(1)));
        Assert.Single(action.Calls);

        // Our action minimized it; the next poll observes that.
        ShowWindow(minimized: true);
        Assert.Empty(loop.Tick(t0.AddSeconds(2)));

        // The user restores it. That is a fresh attempt to view blocked content, so it must be
        // handled now rather than after the remaining ~299 seconds of cooldown.
        ShowWindow(minimized: false);
        Assert.Single(loop.Tick(t0.AddSeconds(3)));
        Assert.Equal(2, action.Calls.Count);
    }
}
