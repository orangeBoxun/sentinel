using System.IO;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Engine;
using Sentinel.Core.Logging;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;
using Sentinel.Platform;
using Sentinel.Platform.Actions;

namespace Sentinel.Agent;

/// <summary>
/// Runs the monitor loop on a background thread and hands outcomes to the UI. The platform objects
/// are created here rather than shared with the UI, because a UIA3Automation instance must not be
/// driven from two threads at once.
/// </summary>
public sealed class MonitorHost : IDisposable
{
    private readonly AgentState _state;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _task;
    private Thread? _heartbeat;
    private ManualResetEventSlim? _heartbeatStop;
    private volatile bool _configDirty;

    /// <summary>
    /// 配置被别的会话改了（心跳里看出来的）。由心跳线程置位、循环线程消费 —— 重载动作不能在心跳
    /// 线程上做，那样循环会读到改了一半的配置对象。
    /// </summary>
    private volatile bool _configStale;

    private volatile string? _forgetRule;

    /// <summary>心跳间隔。服务那边 <c>HeartbeatTimeout</c> 是 20 秒，这里宽一倍。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 多久没走完一轮就算卡住了。只用来**说**，不用来做任何动作 —— 报告总比悄悄停掉好。
    ///
    /// 3 分钟是照着最坏情况定的：一次读取最多等 10 秒（<see cref="ResilientContentReader.DefaultTimeout"/>），
    /// 而多个窗口可能在同一轮里一起卡住。定得太紧会把"这一轮特别慢"误报成卡死。
    /// </summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(3);

    public MonitorHost(AgentState state)
    {
        _state = state;

        // 用量统计活在日志目录里（用户可写），整个进程共用一份。
        Usage = new Sentinel.Core.Model.UsageStore(
            System.IO.Path.Combine(state.LogDirectory, Sentinel.Core.Model.UsageStore.FileName));
    }

    /// <summary>用量统计：请求数、失败数、token。界面拿它画「用量」页。</summary>
    public Sentinel.Core.Model.UsageStore Usage { get; }

    /// <summary>最近一轮走完的时刻。</summary>
    public DateTimeOffset LastTickAt { get; private set; }

    /// <summary>
    /// 循环卡住时收到一次。不是"停止"——循环还活着，只是某一轮没回来。
    /// </summary>
    public event Action<string>? Stalled;

    /// <summary>
    /// 内容读取器（带超时的那一层）。界面拿它说明"有几个窗口读不动"，因为跳过窗口是一条
    /// 潜在的绕过路径，必须能被看见。
    /// </summary>
    public ResilientContentReader? Reader { get; private set; }

    /// <summary>
    /// How to show a notification. Set by the tray so notices come from the Agent's own icon;
    /// without it the monitor creates a second tray icon of its own, which is just confusing.
    /// </summary>
    public Action<string, string, bool, AlertStyle>? Balloon { get; set; }

    /// <summary>
    /// 模型判定的调度，监控跑起来之后才有。界面按它的 <c>BlockingIssue</c> 决定要不要弹提醒 ——
    /// 那是"模型监督已经悄悄停了"和"我知道它停了"之间的唯一区别。
    /// </summary>
    public Sentinel.Core.Model.ModelSupervisor? ModelSupervisor { get; private set; }

    /// <summary>Raised on the monitor thread; subscribers must marshal to the UI thread.</summary>
    public event Action<MonitorOutcome>? Outcome;

    public event Action<string>? Failed;

    public string? LastError { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _task is { IsCompleted: false };
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false })
                return;

            _stoppedOnPurpose = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // 心跳单独一条线程，不在监控循环里。这一条是独立的：循环要是卡住了（某个窗口的 UIA
            // 不响应），心跳跟着停，服务就会以为这个会话没了、去拉一个新 Agent，而新 Agent 撞上
            // 单实例互斥体秒退 —— 于是永远拉不起来。心跳活着，至少服务那边的判断是对的。
            _heartbeatStop = new ManualResetEventSlim(false);
            _heartbeat = new Thread(() => HeartbeatLoop(ServiceClient.CurrentSessionId, _heartbeatStop))
            {
                IsBackground = true,
                Name = "sentinel-heartbeat",
            };
            _heartbeat.Start();

            _task = Task.Run(() => Loop(token), token);
        }
    }

    /// <summary>
    /// 停掉监控循环。**心跳照发。**
    ///
    /// <para>
    /// 心跳是这个会话"还活着"的凭据，不是"监控在跑"的凭据 —— 这两件事必须分开。连心跳一起停的
    /// 话，服务二十秒后会认为这个会话死了，把 Agent 杀掉重拉一个，**于是「停止监控」那个按钮
    /// 等于按不动**（而且表面上像是"它自己又开了"）。
    /// </para>
    /// <para>
    /// 真要退出 Agent 走 <see cref="Shutdown"/>：那条路已经先发过 <c>agent-leaving</c>，
    /// 服务会主动拉下一个。
    /// </para>
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stoppedOnPurpose = true;
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>整个 Agent 要退出了：连心跳一起停。</summary>
    public void Shutdown()
    {
        Stop();

        lock (_gate)
        {
            _heartbeatStop?.Set();
            _heartbeat = null;
        }
    }

    /// <summary>Ask the loop to pick up the config after the next tick.</summary>
    public void RequestConfigRefresh() => _configDirty = true;

    /// <summary>
    /// Ask the loop to drop what it remembers about a rule — what a pause needs, so an episode
    /// already in flight does not enforce the instant the break ends.
    ///
    /// A flag rather than a call: the loop's state is only safe to touch from the loop's own thread,
    /// and this is reached from the UI.
    /// </summary>
    public void ForgetRule(string ruleId) => _forgetRule = ruleId;

    private void Loop(CancellationToken token)
    {
        IAction? notify = null;
        IContentReader? reader = null;
        WindowEnumerator? enumerator = null;
        IAuditLog? audit = null;

        try
        {
            Directory.CreateDirectory(_state.LogDirectory);
            // 本地一份 + 服务一份。服务那份写在只有管理员能写的目录里 ——
            // 本地这份对用户是可写的（Agent 以用户身份跑），改得动。
            audit = new ServiceAuditLog(new DailyAuditLog(_state.LogDirectory), _state.Service);
            enumerator = new WindowEnumerator();

            // 读取放在带超时的那一层里：UIA 调用没有超时也没有取消令牌，一个不响应的窗口能把
            // 整条循环拖停，而外面看起来一切正常。
            Reader = new ResilientContentReader(() => new UiaContentReader());
            reader = Reader;

            notify = Balloon is { } balloon
                ? new TrayNotifyAction(balloon)
                : new NotifyAction();

            var controller = new ActionController();
            var actions = new Dictionary<ActionType, IAction>
            {
                [ActionType.Notify] = notify,
                [ActionType.Log] = new LogAction(audit),
                [ActionType.Minimize] = new MinimizeWindowAction(),
                [ActionType.Close] = new CloseWindowAction(),
                [ActionType.Kill] = new ServiceKillAction(_state.Service),
            };

            // 模型判定的调度。界面按它的 BlockingIssue 决定要不要弹那条每 5 分钟一次的提醒,
            // 所以要留一个引用。
            ModelSupervisor = new Sentinel.Core.Model.ModelSupervisor(
                () => _state.Config, usage: Usage);

            MonitorLoop? loop = null;
            loop = new MonitorLoop(enumerator, reader, controller, actions, audit, _state.Config, _state.ConfigPath, ModelSupervisor);

            LastTickAt = DateTimeOffset.UtcNow;

            while (!token.IsCancellationRequested)
            {
                // 心跳线程发现别的会话改了配置。重载放在这里做，不放那儿做 —— 配置对象界面也在动，
                // 只让一条线程写它。
                if (_configStale)
                {
                    _configStale = false;
                    _state.ReloadConfig();
                    _configDirty = true;
                }

                if (_configDirty)
                {
                    // Applied in place, not by rebuilding: rebuilding would drop every cached read and
                    // re-fire every rule for every open window.
                    _configDirty = false;
                    loop.UpdateConfig(_state.Config);
                }

                if (_forgetRule is { } forgotten)
                {
                    _forgetRule = null;
                    loop.ForgetRule(forgotten);
                }

                try
                {
                    foreach (var outcome in loop.Tick(DateTimeOffset.Now))
                        Outcome?.Invoke(outcome);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Failed?.Invoke(ex.Message);
                }
                finally
                {
                    // 卡住判定的依据。放在 finally 里：这一轮抛了异常也算走完了，不能因为
                    // 一条总在出错的规则被误报成"卡死"。
                    LastTickAt = DateTimeOffset.UtcNow;
                }

                token.WaitHandle.WaitOne(500);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            (notify as IDisposable)?.Dispose();
            (reader as IDisposable)?.Dispose();
            enumerator?.Dispose();
            (audit as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// 报给服务的心跳里那个"循环走到哪儿了"。<c>null</c> = 别按循环进度判我。
    ///
    /// <para>
    /// 三种情况要分开，混在一起会出两种都很糟的错：
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>用户主动停掉的</b>（概览页那个「停止监控」）—— 循环不推进正是他要的。照实报的话，
    /// 服务三分钟后会判它停摆、杀掉重拉，**等于这个按钮根本按不动**。所以报 null。
    /// </description></item>
    /// <item><description>
    /// <b>还没开始跑过</b> —— 报 null。刚起来的那一秒不该被当成停摆。
    /// </description></item>
    /// <item><description>
    /// <b>其余一切</b> —— 照实报最后一次走完的时刻。循环卡住、循环任务崩了退出，都会让那个
    /// 时刻停住，服务于是在阈值之后把它拉起来。这正是我们要的。
    /// </description></item>
    /// </list>
    /// </summary>
    private DateTimeOffset? LivenessTick =>
        _stoppedOnPurpose || LastTickAt == default ? null : LastTickAt;

    /// <summary>「停止监控」按过。区分"用户让它停"和"它自己停了"。</summary>
    private volatile bool _stoppedOnPurpose;

    /// <summary>
    /// 每 15 秒跟服务报到一次，顺便问一句配置版本 —— 别的会话改了配置就是从这里知道的。
    ///
    /// <para>
    /// 它**不在监控循环里**，这是故意的：循环可能被某个窗口的 UIA 调用卡住，而"卡住了"和
    /// "这个会话死了"在服务眼里长得一模一样。心跳独立以后，服务那边看到的始终是真相。
    /// </para>
    /// </summary>
    private void HeartbeatLoop(int sessionId, ManualResetEventSlim stop)
    {
        var knownVersion = -1;

        while (!stop.IsSet)
        {
            try
            {
                // 顺带把"循环走到哪儿了"报上去：心跳证明不了监控在跑（它在自己的线程上，
                // 循环卡死它照样发），服务要靠这个才能认出"循环停摆了"。
                if (_state.Service.Heartbeat(sessionId, LivenessTick, out var version))
                {
                    // 第一次收到的不算"变了"：那只是知道了当前的版本号。
                    if (knownVersion >= 0 && version != knownVersion)
                        _configStale = true;

                    knownVersion = version;
                }
            }
            catch (Exception ex)
            {
                // 服务没应答、管道断了之类。记下来但继续 —— 心跳线程死掉就等于回到老问题上。
                LastError = ex.Message;
            }

            WarnIfStalled();
            stop.Wait(HeartbeatInterval);
        }
    }

    /// <summary>
    /// 循环太久没走完一轮就说一声。**只说，不做任何事** —— 我们没法安全地中止一个卡在 UIA
    /// 里的调用，能做的是别让它悄悄发生。真正的防线是读取超时那层。
    /// </summary>
    private void WarnIfStalled()
    {
        if (LastTickAt == default || DateTimeOffset.UtcNow - LastTickAt < StallAfter)
            return;

        // 用 LastTickAt 本身当去重键：同一轮卡住只报一次；下一轮走完了，时刻变了，就重新开始算。
        if (_stallReportedFor == LastTickAt)
            return;

        _stallReportedFor = LastTickAt;

        var message = $"监控循环已经 {(DateTimeOffset.UtcNow - LastTickAt).TotalMinutes:0.#} 分钟没走完一轮"
                      + (Reader?.LastNotice is { } notice ? $"；{notice}" : "。");

        LastError = message;
        Stalled?.Invoke(message);

        // 说一声，但不弹全屏：卡住的可能是某一个不响应的窗口，而不是"某个东西坏了需要你去处理"。
        // 托盘气泡在这个量级上刚好。
        try
        {
            Balloon?.Invoke("监控卡住了", message, false, AlertStyle.Balloon);
        }
        catch
        {
            // 通知失败不影响别的：这句话同时也在状态栏和日志里。
        }
    }

    private DateTimeOffset _stallReportedFor;

    public void Dispose()
    {
        Shutdown();

        try
        {
            _task?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Shutting down; nothing useful to do if the loop is stuck.
        }
    }
}
