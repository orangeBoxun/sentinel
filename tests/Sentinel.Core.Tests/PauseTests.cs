using System.IO;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;
using Xunit;

namespace Sentinel.Core.Tests;

public class PauseTests : IDisposable
{
    private readonly string _config =
        Path.Combine(Path.GetTempPath(), "sentinel-pause-" + Guid.NewGuid().ToString("N"), "config.json");

    /// <summary>2026-10-17 上午九点（本机 +08:00）。用固定日期，免得测试跟着当天日期漂。</summary>
    private static DateTimeOffset Morning => new(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(8));

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

    private static PauseOptions Allowance(int timesPerDay = 1, int minutes = 10) =>
        new() { Enabled = true, TimesPerDay = timesPerDay, Minutes = minutes };

    // ------------------------------------------------------------------ 额度

    [Fact]
    public void 从没暂停过_现在就可以()
    {
        var options = Allowance();
        var state = PauseState.Load(_config);

        Assert.False(state.IsPaused("edge", options, Morning));
        Assert.True(state.CanPause("edge", options, Morning));
        Assert.Null(state.AvailableAt("edge", options, Morning));
    }

    [Fact]
    public void 同一天里可以停好几次_不必等到明天()
    {
        // 这就是这次改动的全部意义：原来是"每 N 天一次"（N 是整数，最小 1 天），
        // 于是上午停过一次，今天再没有第二次机会。
        var options = Allowance(timesPerDay: 3);
        var state = PauseState.Load(_config);

        state.Record("edge", Morning);

        // 上午九点停的那一次，十点就走完了；十一点还能再停。
        Assert.True(state.CanPause("edge", options, Morning.AddHours(2)));
        Assert.Equal(2, state.LeftToday("edge", options, Morning.AddHours(2)));
    }

    [Fact]
    public void 次数用完要等明天零点()
    {
        var options = Allowance(timesPerDay: 2);
        var state = PauseState.Load(_config);

        state.Record("edge", Morning);
        state.Record("edge", Morning.AddMinutes(40));

        Assert.Equal(0, state.LeftToday("edge", options, Morning.AddMinutes(41)));
        Assert.False(state.CanPause("edge", options, Morning.AddMinutes(60)));

        // 下一次能停的时刻就是明天零点 —— 不是"从最后一次算起再等一天"。
        var tomorrow = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(tomorrow, state.AvailableAt("edge", options, Morning.AddHours(6)));

        // 过零点就是新的一天，额度是满的。
        Assert.Equal(2, state.LeftToday("edge", options, tomorrow));
        Assert.True(state.CanPause("edge", options, tomorrow));
    }

    [Fact]
    public void 正在暂停中不能再花一次额度()
    {
        var options = Allowance(timesPerDay: 5);
        var state = PauseState.Load(_config);

        state.Record("edge", Morning);

        // 一分钟之后还在暂停里：再点一次不该把第二次额度花在一段没走完的暂停上。
        Assert.False(state.CanPause("edge", options, Morning.AddMinutes(1)));
        Assert.Equal(Morning.AddMinutes(10), state.AvailableAt("edge", options, Morning.AddMinutes(1)));

        // 这一轮走完就行了，额度还有 4 次。
        Assert.True(state.CanPause("edge", options, Morning.AddMinutes(11)));
        Assert.Equal(4, state.LeftToday("edge", options, Morning.AddMinutes(11)));
    }

    [Fact]
    public void 一次暂停按配置的长度走_不再多()
    {
        var options = Allowance(minutes: 10);
        var state = PauseState.Load(_config);

        state.Record("edge", Morning);

        Assert.True(state.IsPaused("edge", options, Morning));
        Assert.True(state.IsPaused("edge", options, Morning.AddMinutes(9)));
        Assert.False(state.IsPaused("edge", options, Morning.AddMinutes(11)));
    }

    [Fact]
    public void 记录能跨重启存活()
    {
        var options = Allowance(timesPerDay: 2);

        var recorded = PauseState.Load(_config);
        recorded.Record("edge", Morning);
        recorded.Save(_config);

        var reloaded = PauseState.Load(_config);

        Assert.Equal(1, reloaded.LeftToday("edge", options, Morning.AddMinutes(30)));
        Assert.True(reloaded.IsPaused("edge", options, Morning.AddMinutes(5)));

        // 第二天重新数
        Assert.Equal(2, reloaded.LeftToday("edge", options, Morning.AddDays(1)));
    }

    [Fact]
    public void 老格式的账本还能读_只是把额度当成今天已经用过()
    {
        // 老版本只有 Last。读进来之后"IsPaused"照样对（那一项还在），
        // 额度则从今天重新算 —— 一次性的事，第一次保存之后就是新格式了。
        var stamped = Morning.AddMinutes(-5).ToString("O");
        File.WriteAllText(PauseState.PathFor(_config), "{\"Last\":{\"edge\":\"" + stamped + "\"}}");

        var state = PauseState.Load(_config);
        var options = Allowance();

        Assert.True(state.IsPaused("edge", options, Morning));       // 那一轮还没走完
        Assert.Equal(1, state.LeftToday("edge", options, Morning));
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

        recorded.Record("edge", Morning);
        recorded.Save(_config);

        var state = PauseState.Load(_config);

        Assert.True(state.CanPause("codex", options, Morning));
        Assert.False(state.IsPaused("codex", options, Morning));
    }

    // ------------------------------------------------------------------ 放宽判定

    [Fact]
    public void 调松额度是放宽()
    {
        var before = ConfigWithPause(Allowance(timesPerDay: 1, minutes: 10));

        // 三种调松：允许暂停、次数更多、每次更长 —— 都要令牌。
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(ConfigWithPause(new PauseOptions { Enabled = false, TimesPerDay = 1, Minutes = 10 }), before));
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(timesPerDay: 3, minutes: 10))));
        Assert.Equal(ChangeDirection.Relaxing,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(timesPerDay: 1, minutes: 30))));
    }

    [Fact]
    public void 调紧额度是免费的()
    {
        var before = ConfigWithPause(Allowance(timesPerDay: 3, minutes: 10));

        Assert.Equal(ChangeDirection.Tightening,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(timesPerDay: 1, minutes: 10))));
        Assert.Equal(ChangeDirection.Tightening,
            ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(timesPerDay: 3, minutes: 5))));
        Assert.Equal(ChangeDirection.Tightening,
            ConfigRelaxation.Classify(before, ConfigWithPause(new PauseOptions { Enabled = false, TimesPerDay = 3, Minutes = 10 })));
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

        rule.Pause = new PauseOptions { Enabled = true, TimesPerDay = 1, Minutes = 0 };
        Assert.Contains(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);

        rule.Pause = new PauseOptions { Enabled = true, TimesPerDay = 0, Minutes = 10 };
        Assert.Contains(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);

        rule.Pause = new PauseOptions { Enabled = true, TimesPerDay = 1, Minutes = 10 };
        Assert.DoesNotContain(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void 额度会用一句话说清()
    {
        Assert.Equal("暂停：关", new PauseOptions { Enabled = false }.ToString());
        Assert.Equal("每天最多 3 次，每次 10 分钟", Allowance(timesPerDay: 3, minutes: 10).ToString());
    }
}
