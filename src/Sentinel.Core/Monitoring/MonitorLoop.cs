using Sentinel.Core.Abstractions;
using Sentinel.Core.Config;
using Sentinel.Core.Engine;
using Sentinel.Core.Model;
using Sentinel.Core.Models;

namespace Sentinel.Core.Monitoring;

/// <summary>What the loop decided for one window on one tick.</summary>
public sealed record MonitorOutcome(
    DateTimeOffset Now,
    Target Target,
    WindowRef Window,
    ContentSnapshot Snapshot,
    Verdict Verdict,
    ActionStage Stage,
    string? Detail);

/// <summary>
/// The polling loop: for each target that is due, find its windows, read content, judge, then let
/// the <see cref="ActionController"/> decide whether to notify or enforce this tick.
/// Pure orchestration — no platform calls, so it is unit-testable with fakes.
/// </summary>
public sealed class MonitorLoop
{
    private readonly IWindowEnumerator _enumerator;
    private readonly IContentReader _reader;
    private readonly ActionController _controller;
    private readonly IReadOnlyDictionary<ActionType, IAction> _actions;
    private readonly IAuditLog _audit;

    /// <summary>Where the Service keeps the pause bookkeeping. Null when the caller does not use pauses.</summary>
    private readonly string? _configPath;

    /// <summary>模型判定的调度。Null 表示这一轮没接监督，「模型监督」那种规则就不生效。</summary>
    private readonly IModelSupervisor? _models;

    // Held rather than passed in per tick: the loop owns its config so a change can be applied
    // selectively instead of by rebuilding the loop (which would throw away every cached read).
    private AppConfig _config;
    private RuleEngine _engine;
    private bool _dryRun;

    private readonly Dictionary<string, CacheEntry> _cache = new();

    /// <summary>跑过多少轮了。只为"最久没读过谁"排个队，不做别的判断。</summary>
    private long _ticks;

    /// <summary>
    /// 一轮最多读几个窗口（前台那个排最前，也算在里面）。UIA 一次读取几百毫秒，窗口一多、
    /// 一轮就赶不上间隔；超出上限的轮着来，最坏等几轮。
    /// </summary>
    private const int MaxReadsPerTick = 4;
    private readonly Dictionary<string, DateTimeOffset> _lastRunAt = new();

    private sealed record CacheEntry(
        string TargetId,
        ContentSnapshot Snapshot,
        Verdict Verdict,
        bool WasMinimized,
        bool WasForeground,
        bool WasExposed,
        long LastReadTick,
        int ModelStamp);

    /// <summary>
    /// How many windows the loop is currently tracking. Exposed for diagnostics and so a test can
    /// prove the state stays bounded — it was unbounded before windows that closed were pruned.
    /// </summary>
    public int TrackedWindowCount => _cache.Count;

    /// <summary>Targets with a grace window in flight right now.</summary>
    public int PendingEpisodeCount => _controller.PendingEpisodeCount;

    public MonitorLoop(
        IWindowEnumerator enumerator,
        IContentReader reader,
        ActionController controller,
        IReadOnlyDictionary<ActionType, IAction> actions,
        IAuditLog audit,
        AppConfig config,
        string? configPath = null,
        IModelSupervisor? models = null)
    {
        _enumerator = enumerator;
        _reader = reader;
        _controller = controller;
        _actions = actions;
        _audit = audit;
        _configPath = configPath;
        _models = models;

        // A private copy: the UI edits the live config object in place, so holding that same instance
        // would make every later comparison of "old vs new" trivially report no change.
        _config = ConfigStore.Clone(config);
        _engine = new RuleEngine(_config.Schedules, _models);
        _dryRun = _config.Ui.DryRun;
    }

    /// <summary>
    /// Applies an edited config. Only targets whose settings — or whose policies and schedules —
    /// actually changed get their cached content dropped, so re-judging is limited to what the edit
    /// touched. Rebuilding the loop instead would re-fire every rule for every open window.
    /// </summary>
    public void UpdateConfig(AppConfig config)
    {
        var changed = ConfigDiff.ChangedTargetIds(_config, config);

        if (changed.Count > 0)
        {
            foreach (var key in _cache
                         .Where(entry => changed.Contains(entry.Value.TargetId))
                         .Select(entry => entry.Key)
                         .ToList())
            {
                _cache.Remove(key);
            }
        }

        _config = ConfigStore.Clone(config);
        _engine = new RuleEngine(_config.Schedules, _models);

        if (_dryRun != _config.Ui.DryRun)
        {
            // Safe mode records simulated enforcements. Carried into live operation, their cooldowns
            // delay the first real action by a cooldown that never actually did anything.
            _controller.ClearAll();
            _dryRun = _config.Ui.DryRun;
        }
    }

    /// <summary>
    /// Drops everything remembered about one rule.
    ///
    /// Called when a rule is paused, for two reasons: the cached verdict is stale by the time the
    /// break ends, and — the one that shows — an episode already in flight would otherwise survive
    /// the pause and enforce the moment it ended, which is the opposite of what pausing is for.
    /// </summary>
    public void ForgetRule(string ruleId)
    {
        foreach (var key in _cache
                     .Where(entry => entry.Value.Verdict.RuleId == ruleId)
                     .Select(entry => entry.Key)
                     .ToList())
        {
            _cache.Remove(key);
            _controller.Clear(key);
        }
    }

    public IReadOnlyList<MonitorOutcome> Tick(DateTimeOffset now)
    {
        var outcomes = new List<MonitorOutcome>();
        var windows = _enumerator.Enumerate();
        _ticks++;

        // Read once per tick, not once per target: the loop runs a few times a second and the answer
        // is the same for every target in one pass.
        var pauses = _configPath is null ? null : PauseState.Load(_configPath);

        PruneStaleState(_config, windows);

        foreach (var target in _config.Targets)
        {
            if (!ShouldRunNow(target, now))
                continue;

            // 先挑出"值得读"的：前台那一个，以及**任何露在屏幕上的**窗口。
            //
            // 以前只看前台，于是有个明显的绕法：摆一个小窗口压在最上面，旁边那个违规视频就
            // 永远没人读 —— 可它明明露着、人明明在看。判据因此改成"看得见就得管"。
            var readable = new List<WindowRef>();

            foreach (var window in windows)
            {
                if (!TargetMatcher.Matches(target, window))
                    continue;

                if (!IsWorthReading(window))
                {
                    RememberWindowState(target, window);
                    continue;
                }

                readable.Add(window);
            }

            // 前台那个排最前（每轮都读），其余按"最久没读过的先读"轮转。
            var queue = readable
                .OrderByDescending(window => window.IsForeground)
                .ThenBy(window => _cache.TryGetValue(Key(target, window), out var entry) ? entry.LastReadTick : 0)
                .ToList();

            for (var index = 0; index < queue.Count; index++)
            {
                if (index >= MaxReadsPerTick)
                {
                    // 轮到后面的等下一轮，但状态要记下来（见 RememberWindowState）。
                    RememberWindowState(target, queue[index]);
                    continue;
                }

                var outcome = Evaluate(target, queue[index], _config, pauses, now);
                if (outcome is not null)
                    outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    /// <summary>
    /// 这个窗口现在值不值得读。
    ///
    /// <para>
    /// 只看**正在前台、且没有最小化**的窗口，两个理由：
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// 最小化的窗口没人看得见，里面的东西影响不到任何人，读它只是白花 UIA 的时间
    /// （实测一次完整读取几百毫秒，而每一轮都要遍历所有窗口）。
    /// </description></item>
    /// <item><description>
    /// 后台窗口的内容**会自己变**：视频在往下推、聊天窗一直在动。每变一次就是一次判定，
    /// 而判定是要花钱的。人在看的是前台那一个 —— 切过去的瞬间它就会被读到，不会漏。
    /// </description></item>
    /// </list>
    /// </summary>
    private static bool IsWorthReading(WindowRef window) => window.IsExposed && !window.IsMinimized;

    /// <summary>
    /// 这一轮没读它 —— 把"它现在是什么状态"记下来。
    ///
    /// <para>
    /// 三种"没读"都走这里：被别的窗口完全盖住、排在轮转队伍的后面、以及被最小化。
    /// 记下来是为了认出**状态变化**，而这正是最容易漏的一环：
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// 一个窗口被判违规、提醒过之后，人切走了、过一会儿又切回来 —— 那是"又看了一次"，
    /// 该重新提醒；认出来靠的正是"上一轮它不在前台"。
    /// </description></item>
    /// <item><description>
    /// 摆一个窗口挡在前面把违规的那个盖住，再把挡箭牌关掉 —— 那也属于"又看了一次"。
    /// 不记的话，被盖住时没读、内容也没变，缓存直接放过，违规的东西就这么回来了。
    /// </description></item>
    /// </list>
    /// <para>
    /// 所以这里**只记事实**（谁在前台、谁最小化、谁露着），判断留给下一次真的读到时做。
    /// </para>
    /// </summary>
    private void RememberWindowState(Target target, WindowRef window)
    {
        var key = Key(target, window);

        if (!_cache.TryGetValue(key, out var entry))
            return;   // 还没读过它，谈不上"变化"

        if (entry.WasForeground == window.IsForeground
            && entry.WasMinimized == window.IsMinimized
            && entry.WasExposed == window.IsExposed)
        {
            return;
        }

        _cache[key] = entry with
        {
            WasForeground = window.IsForeground,
            WasMinimized = window.IsMinimized,
            WasExposed = window.IsExposed,
        };
    }

    /// <summary>
    /// Drops state for windows that are gone and for targets that are no longer configured.
    /// Without this the per-window cache and the action controller's episodes grow for the lifetime
    /// of the process — every window that ever opened leaves an entry behind.
    /// </summary>
    private void PruneStaleState(AppConfig config, IReadOnlyList<WindowRef> windows)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in config.Targets)
        {
            foreach (var window in windows)
            {
                if (TargetMatcher.Matches(target, window))
                    live.Add(Key(target, window));
            }
        }

        // No count-based shortcut here: same count with different keys (a window replaced by another)
        // is exactly the case that needs pruning.
        foreach (var key in _cache.Keys.Where(k => !live.Contains(k)).ToList())
            _cache.Remove(key);

        _controller.RetainOnly(live);

        foreach (var key in _lastRunAt.Keys.Where(k => config.Targets.All(t => t.Id != k)).ToList())
            _lastRunAt.Remove(key);
    }

    private static string Key(Target target, WindowRef window) =>
        $"{target.Id}:{window.ProcessId}:{window.NativeHandle}";

    /// <summary>Per-target polling cadence: a body-scanning target is read far less often than a URL-only one.</summary>
    private bool ShouldRunNow(Target target, DateTimeOffset now)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(0, target.Reader.IntervalMs));
        if (interval <= TimeSpan.Zero)
            return true;

        if (_lastRunAt.TryGetValue(target.Id, out var last) && now - last < interval)
            return false;

        _lastRunAt[target.Id] = now;
        return true;
    }

    /// <summary>True when the rule behind this verdict is inside its pause window.</summary>
    private static bool IsRulePaused(AppConfig config, string? ruleId, PauseState? pauses, DateTimeOffset now)
    {
        if (pauses is null || string.IsNullOrEmpty(ruleId))
            return false;

        foreach (var target in config.Targets)
        {
            foreach (var rule in target.Rules)
            {
                if (rule.Id != ruleId)
                    continue;

                return rule.Pause.Enabled && pauses.IsPaused(rule.Id, rule.Pause, now);
            }
        }

        return false;
    }

    private MonitorOutcome? Evaluate(Target target, WindowRef window, AppConfig config,
                                     PauseState? pauses, DateTimeOffset now)
    {
        var cacheKey = Key(target, window);
        var controllerKey = cacheKey;

        // One read per target per tick, with the options that target was configured for. There is
        // no useful "cheap probe" for an app with no address bar (Codex): URL and window title are
        // constant, so only the body text can tell us the content changed.
        var snapshot = _reader.Read(window, target.Reader);
        if (snapshot is null)
        {
            _audit.Write(new AuditEntry(now, target.Id, null, "read-failed", null, "UIA returned no content"));
            return null;
        }

        var cached = _cache.TryGetValue(cacheKey, out var entry) ? entry : null;
        var stamp = _models?.VerdictsVersion ?? 0;

        // "没变"要同时满足两件事：内容一样，**而且**模型判定在这之间没有进展。
        //
        // 少了后一半会有一个很隐蔽的错：第一轮问引擎时判定还在路上，规则回的是"放行"，
        // 这一轮就把"放行"记了下来；从第二轮起内容没变，循环直接用那条缓存 ——
        // **模型后来判的"违规"永远没人去读**，只要页面内容不再变。表现就是"配了模型监督，
        // 打开那个网站什么也没发生"，而且没有任何报错。
        var unchanged = cached is not null
                        && SameContent(cached.Snapshot, snapshot)
                        && (!UsesModel(target) || cached.ModelStamp == stamp);

        // A window we minimized is back in front of the user.
        var reopened = cached is { WasMinimized: true } && !window.IsMinimized;

        // 上一轮它被别的窗口盖住、这一轮露出来了 —— 和人把它从最小化恢复是同一件事：
        // 又看到了。不认这一条，挡在前面的那个窗口一关，被判过的页面就靠缓存蒙混过去了。
        var revealed = cached is { WasExposed: false } && window.IsExposed;

        ContentSnapshot effective;
        Verdict verdict;

        if (unchanged)
        {
            effective = cached!.Snapshot;
            verdict = cached.Verdict;
        }
        else
        {
            effective = snapshot;
            verdict = _engine.Evaluate(target, effective, now);
        }

        // A rule inside its pause window is not enforced — and nothing is written about it either.
        // Pausing means "for these minutes this rule does not exist"; logging it would just be the
        // program arguing with a decision it already agreed to.
        if (verdict.IsBlocked && IsRulePaused(config, verdict.RuleId, pauses, now))
            return null;

        var policy = ResolvePolicy(verdict, config);

        // The foreground triggers are for reminding policies only. A policy that minimizes or closes
        // already changes the window, and the window coming back is its own signal (handled above).
        var reminding = policy is not null && PolicyKind.IsReminderOnly(policy);

        // The user brought the window back to the foreground: a fresh attempt to look at the blocked
        // page, not a repeat of the one already handled.
        var returned = reminding && cached is { WasForeground: false } && window.IsForeground;

        // A reminding policy can also ask to be repeated while the page stays in front of the user.
        var repeatDue = reminding
                        && window.IsForeground
                        && _controller.IsRepeatDue(controllerKey, policy!, now);

        // Each of these ends the previous episode and starts a fresh one, so none of them may wait out
        // a cooldown: a cooldown exists to stop a storm, not to excuse a second attempt.
        if (reopened || revealed || returned || repeatDue)
            _controller.Clear(controllerKey);

        // Record the live window state on every path — a later restore or return is only detectable
        // because the previous tick wrote it down.
        _cache[cacheKey] = new CacheEntry(
            target.Id, effective, verdict, window.IsMinimized, window.IsForeground, window.IsExposed, _ticks, stamp);

        if (policy is null)
        {
            // Allowed, or blocked by a rule that names no policy: nothing to act on. Only the
            // latter is worth an audit line, since it usually means a misconfigured rule.
            if (!verdict.IsBlocked)
                return null;

            if (!unchanged)
                _audit.Write(new AuditEntry(now, target.Id, verdict.RuleId, null, HashUrl(effective.Url, config), verdict.Reason));
            return new MonitorOutcome(now, target, window, effective, verdict, ActionStage.None, "no policy bound");
        }

        // Nothing new to judge, and no reason to speak up again.
        if (unchanged && !reopened && !revealed && !returned && !repeatDue
            && !_controller.HasPendingEpisode(controllerKey))
        {
            return null;
        }

        // A minimized window is not being looked at, so there is nothing to interrupt. Getting this
        // wrong is loud: starting the Agent (or saving the config) rebuilds the monitor, the content
        // cache goes empty, every blocked window is re-judged — and every rule fires again at once,
        // including against windows the user had already put away.
        if (window.IsMinimized && !_controller.HasPendingEpisode(controllerKey))
            return null;

        var decision = _controller.Decide(controllerKey, verdict.IsBlocked, policy, now);
        if (decision.Stage == ActionStage.None)
            return null;

        var context = new ActionContext(window, target, effective, policy, verdict, now, decision.Stage, decision.Episode);
        var executed = Execute(decision.Stage, context, policy, out var minimized, out var notice);

        // Record the minimize we just performed straight away. The next poll can still see the
        // window as not-yet-minimized (the animation is in flight), which would otherwise look like
        // the user reopening it — and act a second time.
        if (minimized)
        {
            var refreshed = _cache[cacheKey];
            _cache[cacheKey] = refreshed with { WasMinimized = true };
        }

        _audit.Write(new AuditEntry(
            now,
            target.Id,
            verdict.RuleId,
            executed,
            HashUrl(effective.Url, config),
            _dryRun ? $"[dry-run] {decision.Reason}" : decision.Reason,
            notice));

        return new MonitorOutcome(now, target, window, effective, verdict, decision.Stage, executed);
    }

    private static bool SameContent(ContentSnapshot a, ContentSnapshot b) =>
        a.Url == b.Url && a.Title == b.Title && a.BodyText == b.BodyText;

    /// <summary>
    /// 这个目标上有没有"模型监督"那种规则。
    ///
    /// 只有这种目标才需要在意"判定有没有进展"：别的规则是当场算出来的，内容没变结论就不会变。
    /// 不加这个限制的话，每落一条判定都会让所有窗口重算一遍 —— 那是白费力气，还可能把
    /// 提醒类的策略重新触发一次。
    /// </summary>
    private static bool UsesModel(Target target) =>
        target.Rules.Any(r => r.Enabled && r.Match.Type == MatchKind.Model);

    private static Policy? ResolvePolicy(Verdict verdict, AppConfig config)
    {
        if (!verdict.IsBlocked || verdict.PolicyId is null)
            return null;

        return config.Policies.FirstOrDefault(p => p.Id == verdict.PolicyId);
    }

    private string Execute(ActionStage stage, ActionContext context, Policy policy, out bool minimized, out string? notice)
    {
        minimized = false;
        notice = null;

        // Grace stage only ever notifies; enforcement runs the policy's action list.
        IEnumerable<ActionType> types = stage == ActionStage.Notify
            ? new[] { ActionType.Notify }
            : policy.Actions;

        if (_dryRun)
            return $"dry-run: {string.Join("+", types)}";

        var ran = new List<ActionType>();
        var failed = new List<string>();
        IAction? notification = null;

        foreach (var type in types)
        {
            if (!_actions.TryGetValue(type, out var action) || !action.CanRun(context))
                continue;

            // Deferred: the notification reports what the other actions actually did, so it has to
            // run after them. Emitted in list order it could only recite the policy's wish list.
            if (type == ActionType.Notify)
            {
                notification = action;
                continue;
            }

            try
            {
                action.Execute(context);
                ran.Add(type);

                if (type == ActionType.Minimize)
                    minimized = true;
            }
            catch (Exception ex)
            {
                // Recorded rather than swallowed: an action that ran but failed must not read as success.
                failed.Add($"{type}({ex.Message})");
            }
        }

        if (notification is not null)
        {
            var report = ran.Count > 0
                ? "已执行：" + string.Join("、", ran.Select(NotifyMessage.Describe))
                : "已提醒你（这条策略没有其他动作）";

            if (failed.Count > 0)
                report += "\n失败：" + string.Join("；", failed);

            try
            {
                var message = context with { ExecutedSummary = report };
                notification.Execute(message);

                // Record the whole notice, not just its title: when a notice and reality seem to
                // disagree, this is the only evidence of what was actually put on screen.
                var built = NotifyMessage.Build(message);
                notice = $"{built.Title} / {built.Body.Replace("\n", " / ")}";
            }
            catch (Exception ex)
            {
                // Recorded, not swallowed: a notice that failed to appear is the worst outcome here —
                // the user would believe they had been warned.
                notice = $"（通知发送失败：{ex.Message}）";
                failed.Add($"Notify({ex.Message})");
            }
        }

        var summary = ran.Count > 0
            ? string.Join("+", ran)
            : notification is not null ? "notified" : "no-action";

        if (failed.Count > 0)
            summary += " 失败: " + string.Join("; ", failed);

        return summary;
    }

    private static string? HashUrl(string? url, AppConfig config)
    {
        if (string.IsNullOrEmpty(url) || !config.Logging.LogUrlHash)
            return null;

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
