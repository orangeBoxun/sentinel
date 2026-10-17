using System.IO;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 暂停额度：工作日/周末两套、周期（每天/每周/每月）、期号重置。
///
/// <para>
/// 这一块是"自我约束的工具里唯一不需要令牌的口子"，所以每一处边界都值得钉住：
/// 额度和暂停本身是两件事（正在暂停时再点一次不该花掉下一次），两套额度互不影响
/// （周一的严格不该吃掉周六的宽松），跨零点不换套（周五 23:50 停的那一轮还是工作日的）。
/// </para>
/// </summary>
public class PauseTests : IDisposable
{
    private readonly string _config =
        Path.Combine(Path.GetTempPath(), "sentinel-pause-" + Guid.NewGuid().ToString("N"), "config.json");

    public PauseTests() => Directory.CreateDirectory(Path.GetDirectoryName(_config)!);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_config)!, true);
        }
        catch
        {
            // Leftover temp directory is not worth failing over.
        }
    }

    /// <summary>本机 +08:00 的某个时刻（2026 年 10 月的日历：16 周五、17 周六、19 周一、21 周三）。</summary>
    private static DateTimeOffset At(int month, int day, int hour, int minute = 0) =>
        new(new DateTime(2026, month, day, hour, minute, 0), TimeSpan.FromHours(8));

    private static PauseOptions Allowance(
        bool enabled = true,
        int workdayTimes = 1, int workdayMinutes = 5, PausePeriod workdayPeriod = PausePeriod.Day,
        int weekendTimes = 3, int weekendMinutes = 30, PausePeriod weekendPeriod = PausePeriod.Day) =>
        new()
        {
            Enabled = enabled,
            Workday = new PauseBudget { Period = workdayPeriod, Times = workdayTimes, Minutes = workdayMinutes },
            Weekend = new PauseBudget { Period = weekendPeriod, Times = weekendTimes, Minutes = weekendMinutes },
        };

    // ------------------------------------------------------------------ 两套额度

    [Fact]
    public void 工作日和周末各算各的()
    {
        var options = Allowance(workdayTimes: 1, workdayMinutes: 5, weekendTimes: 3, weekendMinutes: 30);
        var state = PauseState.Load(_config);

        // 周六：周末那套，3 次 / 每次 30 分钟
        state.Record("edge", options, At(10, 17, 10));

        Assert.Equal(2, state.LeftNow("edge", options, At(10, 17, 10)));
        Assert.Equal(30, (state.PausedUntil("edge", At(10, 17, 10))!.Value - At(10, 17, 10)).TotalMinutes);

        // 周一是另一套：一次都还没动，而且时长是工作日那条（5 分钟）
        Assert.Equal(1, state.LeftNow("edge", options, At(10, 19, 9)));
        Assert.False(state.IsPaused("edge", At(10, 19, 9)));
    }

    [Fact]
    public void 正在暂停中不能再花一次额度()
    {
        var options = Allowance(workdayTimes: 5, workdayMinutes: 10);
        var state = PauseState.Load(_config);

        state.Record("edge", options, At(10, 19, 9));

        Assert.False(state.CanPause("edge", options, At(10, 19, 9, 5)));
        Assert.Equal(At(10, 19, 9, 10), state.AvailableAt("edge", options, At(10, 19, 9, 5)));

        // 这一轮走完就行了，额度并没有多花
        Assert.True(state.CanPause("edge", options, At(10, 19, 9, 11)));
        Assert.Equal(4, state.LeftNow("edge", options, At(10, 19, 9, 11)));
    }

    [Fact]
    public void 周五晚上停的那一轮跨到周六也不换套()
    {
        // 开始那一刻决定用哪一套、多长：23:50 停 30 分钟，到周六 00:20 结束 ——
        // 周六的额度一次都没动。
        var options = Allowance(workdayTimes: 2, workdayMinutes: 30, weekendTimes: 1, weekendMinutes: 10);
        var state = PauseState.Load(_config);

        state.Record("edge", options, At(10, 16, 23, 50));

        Assert.True(state.IsPaused("edge", At(10, 17, 0, 10)));
        Assert.False(state.IsPaused("edge", At(10, 17, 0, 30)));
        Assert.Equal(1, state.LeftNow("edge", options, At(10, 17, 0, 10)));
    }

    // ------------------------------------------------------------------ 周期

    [Fact]
    public void 每天_过零点重新算()
    {
        var options = Allowance(weekendTimes: 3, weekendMinutes: 30);
        var state = PauseState.Load(_config);

        // 周六 23:50 用掉一次（这一期是 10-17）
        state.Record("edge", options, At(10, 17, 23, 50));
        Assert.Equal(2, state.LeftNow("edge", options, At(10, 17, 23, 59)));
        Assert.Equal(3, state.LeftNow("edge", options, At(10, 18, 0, 30)));
    }

    [Fact]
    public void 每周_周一零点重新算()
    {
        var options = Allowance(workdayTimes: 1, workdayPeriod: PausePeriod.Week);
        var state = PauseState.Load(_config);

        // 周三用掉这一周唯一的一次
        state.Record("edge", options, At(10, 21, 9));

        Assert.Equal(0, state.LeftNow("edge", options, At(10, 21, 10)));
        Assert.Equal(At(10, 26, 0), state.AvailableAt("edge", options, At(10, 21, 10)));   // 下周一零点

        Assert.Equal(1, state.LeftNow("edge", options, At(10, 26, 0)));
        Assert.True(state.CanPause("edge", options, At(10, 26, 0)));
    }

    [Fact]
    public void 每月_一号零点重新算()
    {
        var options = Allowance(workdayTimes: 2, workdayPeriod: PausePeriod.Month);
        var state = PauseState.Load(_config);

        state.Record("edge", options, At(10, 21, 9));

        Assert.Equal(1, state.LeftNow("edge", options, At(10, 30, 9)));

        // 11 月 2 号是周一，已经在新的一期里了
        Assert.Equal(2, state.LeftNow("edge", options, At(11, 2, 9)));
    }

    [Fact]
    public void 同样的次数摊在更长的周期上就是收紧()
    {
        // 每天 1 次比每周 1 次松得多 —— 配置里的放宽判定要能看出这一点。
        var daily = ConfigWithPause(Allowance(workdayPeriod: PausePeriod.Day));

        Assert.Equal(ChangeDirection.Tightening,
            ConfigRelaxation.Classify(daily, ConfigWithPause(Allowance(workdayPeriod: PausePeriod.Week))));
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(ConfigWithPause(Allowance(workdayPeriod: PausePeriod.Month)), daily));
    }

    // ------------------------------------------------------------------ 账本

    [Fact]
    public void 记录能跨重启存活()
    {
        var options = Allowance(workdayTimes: 2);

        var recorded = PauseState.Load(_config);
        recorded.Record("edge", options, At(10, 19, 9));
        recorded.Save(_config);

        var reloaded = PauseState.Load(_config);

        Assert.Equal(1, reloaded.LeftNow("edge", options, At(10, 19, 9, 30)));
        Assert.True(reloaded.IsPaused("edge", At(10, 19, 9, 2)));
    }

    [Fact]
    public void 老账本里那一轮暂停算已经结束()
    {
        // 老文件只有 Last、没有批过的分钟数。读成 0 分钟 = 那一轮已经结束 ——
        // 升级那一刻正开着的暂停会提前结束，这是唯一一次性的代价。
        var last = At(10, 17, 9).ToString("O");
        File.WriteAllText(PauseState.PathFor(_config), "{\"Last\":{\"edge\":\"" + last + "\"}}");

        var state = PauseState.Load(_config);

        Assert.False(state.IsPaused("edge", At(10, 17, 9, 1)));
        Assert.Equal(0, state.MinutesOf("edge"));
        Assert.Equal(3, state.LeftNow("edge", Allowance(), At(10, 17, 9, 1)));
    }

    [Fact]
    public void 坏文件读成没暂停过()
    {
        File.WriteAllText(PauseState.PathFor(_config), "{ not json");

        // Failing this way errs toward enforcing, which is the right direction for a mistake to go.
        Assert.True(PauseState.Load(_config).CanPause("edge", Allowance(), DateTimeOffset.Now));
    }

    [Fact]
    public void 一条规则的额度不影响另一条()
    {
        var options = Allowance();
        var recorded = PauseState.Load(_config);

        recorded.Record("edge", options, At(10, 19, 9));
        recorded.Save(_config);

        var state = PauseState.Load(_config);

        Assert.True(state.CanPause("codex", options, At(10, 19, 9)));
        Assert.False(state.IsPaused("codex", At(10, 19, 9)));
    }

    // ------------------------------------------------------------------ 放宽判定

    [Fact]
    public void 调松额度是放宽()
    {
        var before = ConfigWithPause(Allowance(workdayTimes: 1, workdayMinutes: 5, workdayPeriod: PausePeriod.Week));

        // 打开开关
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(ConfigWithPause(Allowance(enabled: false)), before));

        // 次数更多、每次更长、周期更短 —— 三种都是放宽
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(workdayTimes: 3, workdayMinutes: 5, workdayPeriod: PausePeriod.Week))));
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(workdayTimes: 1, workdayMinutes: 30, workdayPeriod: PausePeriod.Week))));
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(workdayTimes: 1, workdayMinutes: 5, workdayPeriod: PausePeriod.Day))));

        // 周末那一套改了也算（额度是按规则分开的，两套都在同一条规则上）
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(workdayTimes: 1, workdayMinutes: 5, workdayPeriod: PausePeriod.Week, weekendTimes: 9))));
    }

    [Fact]
    public void 调紧额度是免费的()
    {
        var before = ConfigWithPause(Allowance(workdayTimes: 3, workdayMinutes: 10, workdayPeriod: PausePeriod.Day));

        Assert.Equal(ChangeDirection.Tightening,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(workdayTimes: 1, workdayMinutes: 10, workdayPeriod: PausePeriod.Day))));
        Assert.Equal(ChangeDirection.Tightening,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(workdayTimes: 3, workdayMinutes: 5, workdayPeriod: PausePeriod.Day))));
        Assert.Equal(ChangeDirection.Tightening,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(enabled: false))));
    }

    private static AppConfig ConfigWithPause(PauseOptions pause)
    {
        var config = new AppConfig();
        config.Targets.Add(new Target
        {
            Id = "t",
            DisplayName = "t",
            ProcessNames = { "msedge" },
            // 暂停挂在规则上，所以两侧要用同一个规则 Id，比较才走到这条规则上。
            Rules = { new Rule { Id = "r", Name = "r", PolicyId = "p", Pause = pause } },
        });

        return config;
    }

    [Fact]
    public void 荒唐的额度会被校验拦下()
    {
        var config = new AppConfig();
        config.Policies.Add(new Policy { Id = "p", Name = "p", Actions = { ActionType.Notify } });
        config.Targets.Add(new Target { Id = "t", ProcessNames = { "msedge" } });
        config.Targets[0].Rules.Add(new Rule
        {
            Id = "r",
            Name = "r",
            PolicyId = "p",
            Match = new MatchSpec { Patterns = { "x" } },
        });

        var rule = config.Targets[0].Rules[0];

        rule.Pause = Allowance(workdayMinutes: 0);
        Assert.Contains(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);

        rule.Pause = Allowance(weekendTimes: 0);
        Assert.Contains(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);

        rule.Pause = Allowance();
        Assert.DoesNotContain(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void 额度会用一句话说清()
    {
        Assert.Equal("暂停：关", new PauseOptions { Enabled = false }.ToString());
        Assert.Equal("工作日 每天 1 次、每次 5 分钟；周末 每天 3 次、每次 30 分钟", Allowance().ToString());
    }
}
