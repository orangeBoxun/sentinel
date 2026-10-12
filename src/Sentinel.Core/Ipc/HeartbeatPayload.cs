namespace Sentinel.Core.Ipc;

/// <summary>
/// 心跳里装的东西：会话号，加上"监控循环上一轮走完的时刻"。
///
/// <para>
/// <b>为什么心跳还要带循环进度。</b>心跳证明不了监控在跑 —— 它跑在自己的线程上，循环卡死它
/// 照样发。而"循环卡死"是这套东西里最难发现的一种坏法：托盘图标在、设置窗口能开、进程也在，
/// 看上去一切正常，只是规则已经不生效了。把"上一轮是什么时候走完的"一起报上去，服务才有依据
/// 认出它，并走恢复流程。
/// </para>
/// <para>
/// 格式是 <c>会话号|时刻</c>，时刻可省。老的 Agent 只发会话号 —— 服务读不到时刻就跳过这项
/// 判断，而不是把一个读不懂的老 Agent 当成卡死的杀掉。
/// </para>
/// </summary>
public readonly record struct HeartbeatPayload(int SessionId, DateTimeOffset? LastTick)
{
    public static string Format(int sessionId, DateTimeOffset? lastTick) =>
        lastTick is { } tick ? $"{sessionId}|{tick:O}" : sessionId.ToString();

    /// <summary>解析不出来就返回 false —— 调用方当作"这次心跳没带进度信息"处理。</summary>
    public static bool TryParse(string? text, out HeartbeatPayload payload)
    {
        payload = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('|');

        if (!int.TryParse(parts[0], out var session))
            return false;

        DateTimeOffset? tick = null;

        if (parts.Length > 1 && DateTimeOffset.TryParse(parts[1], out var parsed))
            tick = parsed;

        payload = new HeartbeatPayload(session, tick);
        return true;
    }
}

/// <summary>
/// 服务判断"这个会话的 Agent 还正常吗"。
///
/// <para>
/// 单独拿出来是为了**能测**：这条规则错了，要么每隔十秒杀一个健康的 Agent，要么永远发现不了
/// 停摆 —— 两种都很糟，而它们都不会报错，只会表现为"有时候监控莫名其妙断一下"。
/// </para>
/// </summary>
public static class AgentLiveness
{
    /// <summary>
    /// 该不该认为这个会话的 Agent 出问题了。
    /// </summary>
    /// <param name="lastSeen">最近一次心跳。null = 从来没心跳过。</param>
    /// <param name="lastTick">最近一次报上来的"循环走完一轮"时刻。null = 它不报这个（老版本）。</param>
    public static bool IsStale(
        DateTimeOffset? lastSeen,
        DateTimeOffset? lastTick,
        DateTimeOffset now,
        TimeSpan heartbeatTimeout,
        TimeSpan stallTimeout)
    {
        if (lastSeen is null || now - lastSeen >= heartbeatTimeout)
            return true;

        // 报过进度才按进度判 —— 不报的（老 Agent）不能被当成卡死。
        return lastTick is { } tick && now - tick >= stallTimeout;
    }
}
