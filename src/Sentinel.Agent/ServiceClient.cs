using System.Diagnostics;
using Sentinel.Core.Config;
using Sentinel.Core.Ipc;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;

namespace Sentinel.Agent;

/// <summary>
/// The Agent's side of the pipe. When a Service is running it is the source of truth for the config
/// and the hand that performs privileged actions; when none is running, the Agent falls back to
/// reading and writing the file itself so it still works standalone.
/// </summary>
public sealed class ServiceClient
{
    private readonly object _gate = new();

    /// <summary>Set by the background heartbeat, so the UI never blocks on a dead service.</summary>
    public bool IsAvailable { get; private set; }

    public int KnownVersion { get; private set; } = -1;

    /// <summary>True when the Service has an unlock public key, so relaxing an edit needs a token.</summary>
    public bool UnlockConfigured { get; private set; }

    /// <summary>Why unlock protection is off, when it is. Shown in the UI.</summary>
    public string? UnlockStatus { get; private set; }

    public AppConfig? TryGetConfig(out int version)
    {
        version = -1;

        var reply = Send(new IpcRequest(Ipc.GetConfig));
        if (!reply.Ok || reply.Payload is null)
        {
            lock (_gate)
                IsAvailable = false;
            return null;
        }

        var envelope = Ipc.Deserialize<ConfigEnvelope>(reply.Payload);
        if (envelope is null)
            return null;

        lock (_gate)
        {
            IsAvailable = true;
            KnownVersion = envelope.Version;
            UnlockConfigured = envelope.UnlockConfigured;
            UnlockStatus = envelope.UnlockStatus;
        }

        version = envelope.Version;
        return envelope.Config;
    }

    /// <summary>
    /// <paramref name="unlockRequest"/> comes back non-null on exactly one failure: the edit weakens
    /// enforcement and the token was missing, wrong, or for a different config. It is what the user
    /// carries to the machine holding the private key.
    ///
    /// <para>
    /// <paramref name="reached"/> is false only when there was no Service to ask. A refusal is an
    /// answer, and the caller must not treat it as an invitation to write the file itself.
    /// </para>
    /// </summary>
    public bool TrySave(AppConfig config, string? token, out string? error, out string? unlockRequest, out bool reached)
    {
        var reply = Send(new IpcRequest(Ipc.SaveConfig, Ipc.Serialize(config), token));

        error = reply.Error;
        unlockRequest = reply.UnlockRequest;
        reached = reply.Reached;

        lock (_gate)
        {
            IsAvailable = reply.Reached;

            if (reply.Ok && reply.Payload is not null
                && Ipc.Deserialize<ConfigEnvelope>(reply.Payload) is { } envelope)
            {
                KnownVersion = envelope.Version;
            }
        }

        return reply.Ok;
    }

    /// <summary>
    /// Spends one pause on a rule. Only the Service can grant one: the budget is its bookkeeping, and
    /// the Agent runs as the user who wants to spend it.
    /// </summary>
    public bool TryPause(string ruleId, out PauseReply? reply, out string? error)
    {
        reply = null;

        var response = Send(new IpcRequest(Ipc.PauseRule, ruleId));
        error = response.Error;

        if (!response.Ok || response.Payload is null)
            return false;

        reply = Ipc.Deserialize<PauseReply>(response.Payload);
        return reply is not null;
    }

    /// <summary>
    /// Tells the Service this Agent is exiting, so the next one comes up in seconds rather than
    /// after the Service's heartbeat timeout. Best effort: this runs while the process is shutting
    /// down, and a reply is of no use to anyone.
    /// </summary>
    public void SayGoodbye(int sessionId) => Send(new IpcRequest(Ipc.AgentLeaving, sessionId.ToString()));

    /// <summary>
    /// Turns unlock protection off. With no token the Service answers with a request to sign; with a
    /// valid one it takes the protection down.
    /// </summary>
    public bool TryRelease(string? token, out string? message, out string? request)
    {
        var response = Send(new IpcRequest(Ipc.ReleaseProtection, null, token));

        message = response.Ok ? response.Payload : response.Error;
        request = response.UnlockRequest;

        lock (_gate)
            IsAvailable = response.Reached;

        return response.Ok;
    }

    /// <summary>
    /// Turns unlock protection on with a public key brought from the other device. No token: this is
    /// the tightening direction. The Service is the one that writes the file, because the install
    /// directory is SYSTEM-only.
    /// </summary>
    public bool TryEnableProtection(string publicKeyPem, out string? message)
    {
        var response = Send(new IpcRequest(Ipc.EnableProtection, publicKeyPem));

        message = response.Ok ? response.Payload : response.Error;

        lock (_gate)
            IsAvailable = response.Reached;

        return response.Ok;
    }

    /// <summary>
    /// 这台机器上那份秘密 —— 保险箱的密码那道门绑着它。拿不到（服务没在跑）时返回 false：
    /// 那就只能用监护码开保险箱。
    /// </summary>
    public bool TryGetVaultSecret(out byte[]? secret)
    {
        secret = null;

        var reply = Send(new IpcRequest(Ipc.VaultSecret));

        if (!reply.Ok || reply.Payload is null)
            return false;

        try
        {
            secret = Convert.FromBase64String(reply.Payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 请服务抹掉封印标记。**要带监护码** —— 服务会自己验一遍（那条命令不带码就是一个后门）。
    /// </summary>
    public bool TryClearSeal(string guardianCode, out string? error)
    {
        var reply = Send(new IpcRequest(Ipc.ClearSeal, guardianCode));
        error = reply.Ok ? null : reply.Error ?? "服务拒绝了。";
        return reply.Ok;
    }

    public bool TryKill(int processId, out string? error)
    {
        var reply = Send(new IpcRequest(Ipc.RequestKill, processId.ToString()));
        error = reply.Error;
        return reply.Ok;
    }

    /// <summary>
    /// 只报"这个会话还活着"，不带循环进度。
    ///
    /// <para>
    /// 给那些不是监控循环的调用方用（比如保险库窗口探"服务还在不在"）。**它不能瞎填一个
    /// 当前时刻** —— 那会让服务以为循环刚刚还在推进，从而看不到真正的停摆。
    /// </para>
    /// </summary>
    public bool Heartbeat(int sessionId, out int version) => Heartbeat(sessionId, null, out version);

    /// <summary>
    /// Tells the service this session still has a live Agent, and learns the current config version —
    /// one round trip, since the heartbeat is also how a change made in another session is noticed.
    ///
    /// <para>
    /// <paramref name="lastTick"/> 是监控循环上一轮走完的时刻。**心跳本身证明不了监控在跑** ——
    /// 它在自己的线程上，循环卡死它照样发。所以顺带把"循环走到哪儿了"报上去，服务才有依据
    /// 判断"这个 Agent 是不是卡住了"，而不是只有"进程还在不在"。
    /// </para>
    /// <para>
    /// 格式是 <c>会话号|时刻</c>，不给时刻就只发会话号。老的 Agent 只发会话号，服务那边解析不到
    /// 时刻就跳过这项判断 —— 升级过程中新服务遇上老 Agent，不能因为读不懂就把它杀了。
    /// </para>
    /// </summary>
    public bool Heartbeat(int sessionId, DateTimeOffset? lastTick, out int version)
    {
        version = KnownVersion;

        var payload = HeartbeatPayload.Format(sessionId, lastTick);
        var reply = Send(new IpcRequest(Ipc.Ping, payload));

        lock (_gate)
        {
            IsAvailable = reply.Ok;
            if (reply.Ok && int.TryParse(reply.Payload, out var current))
            {
                KnownVersion = current;
                version = current;
            }
        }

        return reply.Ok;
    }

    /// <summary>
    /// 把一条审计记录交给服务落盘。**失败不算数**：审计是"顺便留个底"，不该因为它把监控循环
    /// 拖住或者吵醒用户 —— 本地那份还在，服务那份补不回来而已。
    /// </summary>
    public bool SendAudit(Sentinel.Core.Abstractions.AuditEntry entry)
    {
        try
        {
            return Send(new IpcRequest(Ipc.Audit, Ipc.Serialize(entry))).Ok;
        }
        catch
        {
            return false;
        }
    }

    private static IpcResponse Send(IpcRequest request) =>
        IpcChannel.SendAsync(Ipc.PipeName, request, CancellationToken.None).GetAwaiter().GetResult();

    public static int CurrentSessionId
    {
        get
        {
            try
            {
                return Process.GetCurrentProcess().SessionId;
            }
            catch
            {
                return 0;
            }
        }
    }
}
