using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Ipc;
using Sentinel.Core.Logging;
using Sentinel.Core.Vault;
using Sentinel.Platform;

namespace Sentinel.Service;

/// <summary>
/// The service does two things: it owns the config, and it makes sure an Agent is running on every
/// logged-on desktop. It cannot read UIA itself — session 0 has no desktop — so it never tries.
/// </summary>
public sealed class SentinelWorker : BackgroundService
{
    /// <summary>
    /// How long an Agent may go without checking in before it is assumed dead.
    ///
    /// <para>
    /// This is also the length of the gap anyone gets by ending the Agent's process, which needs no
    /// administrator rights at all — so it is kept just above one missed heartbeat (the Agent sends
    /// one every fifteen seconds) rather than comfortably above three. Guessing wrong is cheap: a
    /// replacement is started, finds the single-instance mutex held, and exits silently.
    /// </para>
    /// </summary>
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 监控循环多久没走完一轮就算它卡住了。
    ///
    /// <para>
    /// 心跳证明不了监控在跑 —— 它在自己的线程上，循环卡死它照样发。所以 Agent 顺带报"上一轮
    /// 是什么时候走完的"，这里按它判断。**这是"循环停摆"唯一能自动恢复的路**：一个不再推进的
    /// 监控循环，界面看上去一切正常（托盘图标在、窗口能开），只是规则已经不生效了。
    /// </para>
    /// <para>
    /// 三分钟是宽出来的：一轮里每个窗口最多等十秒（读取超时），而卡过的窗口会被静默一阵子，
    /// 所以正常情况下单轮是秒级的。三分钟没动静就是真的停摆了。
    /// </para>
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 看门狗多久没动静就算它被停掉了。它一分钟跑一次，这里给足容忍 ——
    /// 误判的代价是保险箱被封印（只有监护人码能开），漏判的代价是少了一分钟的发现时间。
    /// </summary>
    /// <summary>
    /// 心跳多久没动值得说一声。**只是说一声** —— 见 CheckWatchdogAlive 里为什么它不再是封印的判据。
    /// </summary>
    private static readonly TimeSpan WatchdogSilenceWorthMentioning = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 服务刚起来的这几分钟不判看门狗。安装/升级期间任务会被删掉重建，刚重建的任务
    /// 还没跑第一轮 —— 那时候"任务不在"是正常的，不是有人在拆。
    /// </summary>
    private static readonly TimeSpan WatchdogGraceAfterStart = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How often the liveness record is rewritten. Short enough that a crash — recovered by the
    /// failure actions in about five seconds — leaves a gap far below the stoppage threshold.
    /// </summary>
    private static readonly TimeSpan AliveInterval = TimeSpan.FromSeconds(20);

    private readonly ILogger<SentinelWorker> _log;
    private readonly ConfigAuthority _config;
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastSeen = new();

    /// <summary>每个会话最近一次报上来的"监控循环走到哪儿了"。</summary>
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastTick = new();

    public SentinelWorker(ILogger<SentinelWorker> log, ConfigAuthority config)
    {
        _log = log;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var agent = LocateAgent();
        _log.LogInformation("Sentinel 服务已启动。配置文件：{Config}", _config.Path);
        _log.LogInformation("Agent 可执行文件：{Agent}", agent ?? "（未找到，将只提供配置服务）");
        _log.LogInformation("解锁保护：{Status}", _config.UnlockStatus ?? "已启用（放宽限制需要机器外的令牌）");

        if (_config.StartupRefusal is not null)
            _log.LogWarning("{Refusal}", _config.StartupRefusal);

        // Must run before the first write: this compares the record left by the previous life of
        // this Service against how long the machine has been up.
        if (VaultGuard.DetectStoppage(_config.Path, DateTimeOffset.Now,
                SystemUptime.Awake()) is { } stoppage)
        {
            _log.LogError("保险箱：{Reason}", stoppage);
            VaultGuard.SealVault(_config.Path, stoppage);
        }

        VaultGuard.WriteAlive(_config.Path, "服务启动");

        var tasks = new List<Task> { ServePipeAsync(token), KeepAliveAsync(token) };
        if (agent is not null)
            tasks.Add(KeepAgentsRunningAsync(agent, token));

        tasks.Add(KeepWatchdogRegisteredAsync(token));
        tasks.Add(WatchTheWatchdogAsync(token));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrites the liveness record while the Service runs. Nothing reads it until the next start —
    /// see <see cref="VaultGuard"/> for why that is the only moment it can answer anything useful.
    /// </summary>
    private async Task KeepAliveAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(AliveInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            VaultGuard.WriteAlive(_config.Path, "运行中");

            // So that dropping the unlock public key into the install directory takes effect on its
            // own. Requiring a Service restart to switch the protection on would mean the setting-up
            // step is the one step the protection makes hard.
            _config.RefreshUnlockAuthority();
        }
    }

    /// <summary>
    /// Re-registers the watchdog task if it has gone missing.
    ///
    /// Without this, deleting the task once is enough: the service would then rely on its own
    /// failure recovery, which does not cover being stopped deliberately. With it, removing the task
    /// is undone within minutes, and stopping the service is undone by the task — neither action
    /// alone gets anyone anywhere.
    /// </summary>
    private async Task KeepWatchdogRegisteredAsync(CancellationToken token)
    {
        if (!Watchdog.IsSystem())
        {
            _log.LogInformation("当前不是 SYSTEM 身份，跳过对看门狗的看管（本地以控制台方式运行时属正常）");
            return;
        }

        var executable = Environment.ProcessPath ?? "";

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!string.IsNullOrEmpty(executable) && !ScheduledTask.Exists(ScheduledTask.WatchdogTaskName))
                {
                    var arguments = $"--watchdog --config \"{_config.Path}\"";

                    if (ScheduledTask.Ensure(ScheduledTask.WatchdogTaskName, executable, arguments, out var error))
                        _log.LogWarning("看门狗计划任务不见了，已重新注册");
                    else
                        _log.LogWarning("重新注册看门狗失败：{Error}", error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "检查看门狗任务失败");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------ IPC

    private async Task ServePipeAsync(CancellationToken token)
    {
        _log.LogInformation("命名管道：{Pipe}", Ipc.PipeName);

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreatePipe();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "创建命名管道失败，2 秒后重试");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                await HandleAsync(server, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Creating a second pipe instance requires CreateNewInstance on the first one's security
    /// descriptor — a right deliberately not granted to ordinary users here, since holding it means
    /// being able to impersonate the service. Handling one connection at a time avoids needing it.
    /// Requests are a config read and the occasional privileged call, so there is nothing to gain
    /// from concurrency.
    /// </summary>
    private static NamedPipeServerStream CreatePipe()
    {
        try
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            // Agents run as the signed-in user, so ordinary users must be able to connect — but only
            // to talk, not to create further instances.
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                Ipc.PipeName, PipeDirection.InOut,
                1,   // one instance: see the note above
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0, pipeSecurity: security);
        }
        catch (Exception)
        {
            // Hosting outside a SYSTEM service (a console run while developing) cannot set that ACL.
            // A working pipe without it beats no pipe at all.
            return new NamedPipeServerStream(
                Ipc.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken token)
    {
        try
        {
            var line = await IpcChannel.ReadAsync(server, token).ConfigureAwait(false);
            var request = line is null ? null : Ipc.Deserialize<IpcRequest>(line);

            var response = request is null
                ? IpcResponse.Failure("unreadable request")
                : Dispatch(request);

            await IpcChannel.WriteAsync(server, Ipc.Serialize(response), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "IPC 请求处理失败");
        }
    }

    // ------------------------------------------------------------------ 审计

    private readonly object _auditGate = new();
    private DailyAuditLog? _audit;

    /// <summary>
    /// 收下一条审计，写进权威的那一份。
    ///
    /// <para>
    /// 位置是 <c>config.json</c> 旁边的 <c>audit\</c> —— 那个目录由 install.ps1 设成
    /// "管理员可写、普通用户只读"。这就是它和 Agent 本地那份的区别：**那份改得动，这份改不动**。
    /// </para>
    /// <para>
    /// 失败只记不报：审计是"顺便留个底"，不该因为它让 Agent 那边出问题。真写出不去（比如装到
    /// 只读介质上）在日志里有痕迹可查。
    /// </para>
    /// </summary>
    private IpcResponse RecordAudit(IpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Payload))
            return IpcResponse.Failure("空的审计记录");

        AuditEntry? entry;

        try
        {
            entry = Ipc.Deserialize<AuditEntry>(request.Payload);
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure($"审计记录读不出来：{ex.Message}");
        }

        if (entry is null)
            return IpcResponse.Failure("审计记录读不出来");

        try
        {
            AuditWriter().Write(entry);
            return IpcResponse.Success();
        }
        catch (Exception ex)
        {
            _log.LogWarning("写审计失败：{Error}", ex.Message);
            return IpcResponse.Failure(ex.Message);
        }
    }

    private DailyAuditLog AuditWriter()
    {
        lock (_auditGate)
        {
            return _audit ??= new DailyAuditLog(
                Path.Combine(Path.GetDirectoryName(_config.Path)!, "audit"));
        }
    }

    private IpcResponse Dispatch(IpcRequest request)
    {
        switch (request.Command)
        {
            case Ipc.Ping:
                // 格式是 "会话号|上一轮走完的时刻"，时刻可省。老的 Agent 只发会话号 ——
                // 那种情况下只是没有进度信息，不该被当成卡死。
                if (HeartbeatPayload.TryParse(request.Payload, out var beat))
                {
                    _lastSeen[beat.SessionId] = DateTimeOffset.UtcNow;

                    if (beat.LastTick is { } tick)
                        _lastTick[beat.SessionId] = tick;
                }

                // Carry the config version back: the heartbeat is how an Agent notices that another
                // session changed the config, and a second round trip for that would be wasteful.
                return IpcResponse.Success(_config.SnapshotVersion());

            case Ipc.Audit:
                return RecordAudit(request);

            case Ipc.GetConfig:
                return IpcResponse.Success(_config.Snapshot());

            case Ipc.SaveConfig:
                if (request.Payload is null)
                    return IpcResponse.Failure("empty config");

                if (_config.TrySave(request.Payload, request.Token, out var error, out var unlockRequest))
                    return IpcResponse.Success(_config.Snapshot());

                if (unlockRequest is not null)
                    _log.LogWarning("配置修改被拒绝：会放宽限制，且没有可用的解锁令牌");

                return IpcResponse.Failure(error ?? "save rejected", unlockRequest);

            case Ipc.RequestKill:
                return Kill(request.Payload);

            case Ipc.AgentLeaving:
                // Forget it immediately: the next session pass, at most ten seconds away, will start
                // a replacement instead of waiting out the heartbeat timeout.
                if (int.TryParse(request.Payload, out var leaving))
                    _lastSeen.TryRemove(leaving, out _);

                return IpcResponse.Success();

            case Ipc.PauseRule:
                if (_config.TryPause(request.Payload ?? "", out var pauseReply, out var pauseError))
                {
                    _log.LogInformation("已暂停规则 {Rule}", request.Payload);
                    return IpcResponse.Success(pauseReply);
                }

                return IpcResponse.Failure(pauseError ?? "pause refused");

            case Ipc.EnableProtection:
                if (_config.TryEnableProtection(request.Payload, out var enableError, out var enableDetail))
                {
                    _log.LogInformation("解锁保护已开启");
                    return IpcResponse.Success(enableDetail);
                }

                return IpcResponse.Failure(enableError ?? "enable refused");

            case Ipc.VaultSecret:
            {
                // 保险箱的密码那道门绑着这份秘密。服务是唯一该拿到它的人（也确实是唯一收得紧权限的：
                // 文件建好之后只给 SYSTEM 和管理员读）。
                var secretPath = VaultGuard.MachineSecretPathFor(_config.Path);
                var secret = VaultGuard.LoadOrCreateMachineSecret(_config.Path);

                if (secret is null)
                    return IpcResponse.Failure("这台机器上那份秘密读不出来（权限？）");

                // 权限只在自己是 SYSTEM 的时候收 —— 否则收完这个进程自己也读不了了。
                // （控制台里以普通身份跑的时候属正常，不是错误。）
                if (Watchdog.IsSystem() && !FileAcl.RestrictToSystemAndAdmins(secretPath))
                {
                    _log.LogWarning(
                        "本机秘密的权限没收紧（{Path}）—— 它本该只有 SYSTEM 和管理员能读。",
                        secretPath);
                }

                return IpcResponse.Success(Convert.ToBase64String(secret));
            }

            case Ipc.ReleaseProtection:
                if (_config.TryReleaseProtection(request.Token, out var releaseError, out var releaseRequest, out var detail))
                {
                    _log.LogWarning("解锁保护已按令牌关闭");
                    return IpcResponse.Success(detail);
                }

                return IpcResponse.Failure(releaseError ?? "release refused", releaseRequest);

            default:
                return IpcResponse.Failure($"unknown command '{request.Command}'");
        }
    }

    private IpcResponse Kill(string? payload)
    {
        if (!int.TryParse(payload, out var pid))
            return IpcResponse.Failure("missing pid");

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            _log.LogInformation("已按请求结束进程 {Pid}（{Name}）", pid, process.ProcessName);
            return IpcResponse.Success();
        }
        catch (Exception ex)
        {
            _log.LogWarning("结束进程 {Pid} 失败：{Error}", pid, ex.Message);
            return IpcResponse.Failure(ex.Message);
        }
    }

    /// <summary>
    /// 盯着看门狗有没有在跑。
    ///
    /// <para>
    /// 和 <see cref="KeepWatchdogRegisteredAsync"/> 分工不同，两件事都要做：
    /// 那个管"任务**不见了**就重新注册"；这个管"任务**还在、但没在跑**"—— 后者
    /// <c>ScheduledTask.Exists</c> 看不见（被禁用的任务照样存在），而它同样能让那两层防护停摆。
    /// 区分办法是看门狗自己留的存活记录（它每分钟写一遍）：**见过、后来不写了**才算。
    /// </para>
    /// <para>
    /// 单独一趟，不挂在别的循环下面：它和"有没有找到 Agent""是不是 SYSTEM 身份"都没有关系，
    /// 挂在那些条件下会让这条检查在某些机器上根本不跑。
    /// </para>
    /// </summary>
    private async Task WatchTheWatchdogAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                CheckWatchdogAlive();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "检查看门狗是否在运行失败");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------ 看门狗

    private bool _watchdogSealReported;

    /// <summary>这个服务进程是什么时候起来的。看门狗那条判据要拿它做启动宽限。</summary>
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    /// <summary>上一次就"看门狗在空转"出声的时刻，用来节流。</summary>
    private DateTimeOffset? _watchdogSilenceReportedAt;

    /// <summary>
    /// 看门狗还在跑吗。不在跑就**封印保险箱**。
    ///
    /// <para>
    /// 看门狗自己发现不了自己不存在，所以这件事只能由服务来做 —— 而它只有在服务还活着的时候
    /// 才有意义（两边都没了，就没有任何东西在跑了，也就没有谁去封印）。
    /// </para>
    /// <para>
    /// <b>为什么用封印来回应。</b>看门狗每分钟把服务权限收紧一次、并在服务被停掉时把它拉回来。
    /// 把它删掉或者禁用掉，就是拆掉了那两层。往后怎么走是个选择：可以什么都不做（那这条捷径
    /// 就白送了），也可以让动手的人付出点代价 —— 保险箱因此封印，而那之后**只有监护人解锁码
    /// 能打开它**（密码不再管用，监护人码在另一台设备上）。这套东西本来就是靠这个约束自己的。
    /// </para>
    /// <para>
    /// 判据是"见过它、后来不见了"，不是"它不存在"：新装的机器、或者装的时候用了
    /// <c>-NoWatchdog</c>，都不该在任何一次正常安装之后就被封印。
    /// </para>
    /// </summary>
    /// <summary>
    /// 看门狗还在不在。
    ///
    /// <para>
    /// 判据是<b>计划任务真的不见了</b> —— 那才叫有人在拆。以前拿"心跳文件旧了"当判据，
    /// 而心跳变旧有一大堆无害的原因，最常见的是合盖待机：机器没在跑，任务和服务都写不出心跳，
    /// 醒来时那个文件就已经"过期"了。2026-10-12 就这样误封过一次真实的保险箱
    /// （合盖 8 分钟，而阈值是 5 分钟）—— 证据和后果差得太远。
    /// </para>
    /// <para>
    /// 心跳旧了仍然要说，只是不再拿它当证据：任务在、心跳却长时间不动，说明看门狗在空转，
    /// 那件事值得知道（日志和托盘都出声），但不值得把保险箱封掉。
    /// </para>
    /// </summary>
    private void CheckWatchdogAlive()
    {
        if (VaultGuard.InMaintenance(_config.Path))
            return;   // 装/卸期间服务本来就会被停一会儿，看门狗也不该在这时候被追究

        var lastAlive = ReadWatchdogAlive();
        TimeSpan? silence = lastAlive is null ? null : DateTimeOffset.UtcNow - lastAlive.Value;

        // 心跳长时间不动：出声。待机、关机、机器忙都会这样，所以这条**只是提醒**。
        WarnIfWatchdogSilent(silence);

        // **探不出任务在不在的时候，不谈封印。** 这条判据问的是"看门狗任务还在不在"，
        // 而这个问题只有 SYSTEM 问得出来：普通权限下 schtasks 返回的是"拒绝访问"，
        // 和"任务不存在"长得一模一样。把"看不懂"当成"被拆了"，就是拿一次误封去换一次猜错。
        if (!Watchdog.IsSystem())
            return;

        var registered = ScheduledTask.Exists(ScheduledTask.WatchdogTaskName);

        // "停用"和"删掉"是一回事：都是把它关掉了，而停用只需要点一下。
        var disabled = WatchdogTaskDefinition.IsDisabled(
            ScheduledTask.ReadDefinition(ScheduledTask.WatchdogTaskName));

        if (registered && !disabled)
            return;

        // 服务刚起来的这几分钟不封：安装/升级期间任务会被删掉重建，刚重建的任务还没跑第一轮，
        // 那时"任务不在"是正常的。出声不受这条限制 —— 说一声是廉价的。
        if (DateTimeOffset.UtcNow - _startedAt < WatchdogGraceAfterStart)
            return;

        // 封印是幂等的（保留最初的原因），但日志没必要每分钟重复一遍。
        if (_watchdogSealReported)
            return;

        _watchdogSealReported = true;

        _log.LogWarning("看门狗计划任务{How}（最后一次心跳 {At:u}）—— 封印保险箱",
            registered ? "被停用了" : "已经不在了", lastAlive);

        Watchdog.SealVault(_config.Path,
            $"看门狗计划任务{(registered ? "已被停用" : "已被删除")}。"
            + (lastAlive is null
                ? "它本该每分钟写一次存活记录，而现在这个任务根本不在了。"
                : $"它最后一次运行是 {lastAlive.Value:yyyy-MM-dd HH:mm} UTC。")
            + "没有它就没有人每分钟核对服务的权限和服务本身了。"
            + "保险箱因此封印：**只有监护人解锁码**能打开它，密码不再管用。");
    }

    /// <summary>心跳长时间不动：说一声。**不做别的** —— 待机、关机、机器忙都会让它变旧。</summary>
    private void WarnIfWatchdogSilent(TimeSpan? silence)
    {
        if (silence is not { } gap || gap < WatchdogSilenceWorthMentioning)
        {
            _watchdogSilenceReportedAt = null;   // 恢复了，下次沉默重新报
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (_watchdogSilenceReportedAt is { } last && now - last < TimeSpan.FromMinutes(30))
            return;

        _watchdogSilenceReportedAt = now;

        _log.LogWarning(
            "看门狗任务在，但已经 {Minutes:0} 分钟没写存活记录了 —— 它在空转。"
            + "（待机、关机、机器忙都会这样；这条只是提醒，不封印保险箱。）",
            gap.TotalMinutes);
    }

    private DateTimeOffset? ReadWatchdogAlive()
    {
        try
        {
            var path = VaultGuard.WatchdogAlivePathFor(_config.Path);

            return File.Exists(path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("读看门狗的存活记录失败：{Error}", ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ 会话守护

    private async Task KeepAgentsRunningAsync(string agentPath, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var session in SessionLauncher.ActiveSessionIds())
                {
                    var now = DateTimeOffset.UtcNow;

                    var hasSeen = _lastSeen.TryGetValue(session, out var seen);
                    var hasTick = _lastTick.TryGetValue(session, out var tick);

                    var stale = AgentLiveness.IsStale(
                        hasSeen ? seen : null,
                        hasTick ? tick : null,
                        now, HeartbeatTimeout, StallTimeout);

                    if (!stale)
                        continue;

                    var loopStale = hasTick && now - tick >= StallTimeout;

                    // 两种都走同一条恢复路径：结束掉它，重新拉一个。
                    //
                    // 为什么不是"让进程自己重启循环"：一个卡住的循环多半停在不会返回的 UIA 调用上，
                    // 取消不掉；进程里再起一个的话，旧的 UIA 自动化和没关掉的日志文件句柄都还留着 ——
                    // 新循环连日志都打不开。换个进程是干净的。
                    var reason = loopStale ? "监控循环停摆" : "已停止心跳";

                    var stopped = SessionLauncher.StopStaleAgentsIn(
                        session, agentPath, HeartbeatTimeout, out var stopError);

                    if (stopped > 0)
                    {
                        _log.LogWarning(
                            "会话 {Session} 的 Agent {Reason}，结束了 {Count} 个并重新拉起",
                            session, reason, stopped);

                        // 给新进程让出单实例互斥体，别紧接着就再判一次。
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    }
                    else if (stopError is not null)
                    {
                        _log.LogWarning("检查会话 {Session} 里的 Agent 时出错：{Error}", session, stopError);
                    }
                    else if (loopStale)
                    {
                        // 循环停摆、但没找到进程（可能是刚被用户关掉）—— 说一声，免得这个状态
                        // 看上去和"一切正常"一样。
                        _log.LogWarning("会话 {Session} 的监控循环停摆了，但没找到它的进程，直接重拉", session);
                    }

                    // Hand the authoritative config path down to the Agent: if the service is ever
                    // gone, the Agent falls back to reading that file directly, and it has to be the
                    // same one — not whatever it would have guessed for itself.
                    var arguments = $"--service --config \"{_config.Path}\"";

                    if (SessionLauncher.TryStartInSession(session, agentPath, arguments, out var error))
                    {
                        // Give it a moment to check in before the next pass tries again.
                        //
                        // 循环进度也要一起重置：上一任 Agent 报的那个时刻已经旧了，不重置的话
                        // 下一轮就会拿它去比，新进程刚起来就被判成"循环停摆"再被杀一次 ——
                        // 一个每十秒杀自己一次的循环。
                        var started = DateTimeOffset.UtcNow;
                        _lastSeen[session] = started;
                        _lastTick[session] = started;

                        _log.LogInformation("已在会话 {Session} 中启动 Agent", session);
                    }
                    else
                    {
                        _log.LogWarning("在会话 {Session} 中启动 Agent 失败：{Error}", session, error);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "扫描登录会话失败");
            }

            try
            {
                await Task.Delay(SessionPollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private string? LocateAgent()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SENTINEL_AGENT");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Sentinel.Agent.exe"),
            Path.Combine(baseDir, "..", "Sentinel.Agent", "Sentinel.Agent.exe"),
            // Development layout: the service and the agent build into sibling folders.
            Path.Combine(baseDir, "..", "..", "..", "..", "Sentinel.Agent", "bin", "Debug", "net8.0-windows", "Sentinel.Agent.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "Sentinel.Agent", "bin", "Release", "net8.0-windows", "Sentinel.Agent.exe"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
                return full;
        }

        return null;
    }
}
