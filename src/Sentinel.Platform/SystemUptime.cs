using System.Runtime.InteropServices;

namespace Sentinel.Platform;

/// <summary>
/// 机器**真正醒着运行**了多久。
///
/// <para>
/// <c>Environment.TickCount64</c> 走的是 GetTickCount64，<b>把睡眠/待机也算在里面</b>。
/// 而我们真正要回答的是"那段时间机器在不在跑" —— 合盖待机半小时之后，拿带睡眠的时长去比，
/// 会得出"它消失过 30 分钟"这种假结论，把保险箱误封（2026-10-12 就这么封过一次）。
/// <c>QueryUnbiasedInterruptTime</c> 是系统给的那个不含睡眠的时钟。
/// </para>
/// </summary>
public static class SystemUptime
{
    [DllImport("kernel32.dll")]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    /// <summary>不含睡眠的运行时长的近似值（系统只在某些场合才更新它，够用）。</summary>
    public static TimeSpan Awake()
    {
        // 单位是 100 纳秒。
        if (QueryUnbiasedInterruptTime(out var hundredNanoseconds))
            return TimeSpan.FromTicks((long)(hundredNanoseconds / 100));

        // 取不到就退回带睡眠的那个：宁可保守，也不要算出一个零来。
        return TimeSpan.FromMilliseconds(Environment.TickCount64);
    }
}
