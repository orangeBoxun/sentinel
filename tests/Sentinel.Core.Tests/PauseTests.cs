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

    private static PauseOptions Allowance(int everyDays = 1, int minutes = 10) =>
        new() { Enabled = true, EveryDays = everyDays, Minutes = minutes };

    // ------------------------------------------------------------------ 状态

    [Fact]
    public void A_target_that_has_never_been_paused_can_be_now()
    {
        var options = Allowance();
        var state = PauseState.Load(_config);
        var now = DateTimeOffset.Now;

        Assert.False(state.IsPaused("edge", options, now));
        Assert.True(state.CanPause("edge", options, now));
        Assert.Null(state.AvailableAt("edge", options));
    }

    [Fact]
    public void A_pause_runs_for_its_configured_length_and_no_longer()
    {
        var options = Allowance(minutes: 10);
        var now = DateTimeOffset.Now;

        var state = PauseState.Load(_config);
        state.Record("edge", now);

        Assert.True(state.IsPaused("edge", options, now));
        Assert.True(state.IsPaused("edge", options, now.AddMinutes(9)));
        Assert.False(state.IsPaused("edge", options, now.AddMinutes(11)));
    }

    [Fact]
    public void The_next_pause_waits_out_the_configured_gap()
    {
        var options = Allowance(everyDays: 3, minutes: 10);
        var now = DateTimeOffset.Now;

        var state = PauseState.Load(_config);
        state.Record("edge", now);

        // Measured from when the pause began, not from when it ended — "once every three days" is
        // about how often it happens, not how much of the time it covers.
        Assert.Equal(now.AddDays(3), state.AvailableAt("edge", options));
        Assert.False(state.CanPause("edge", options, now.AddDays(2)));
        Assert.True(state.CanPause("edge", options, now.AddDays(3)));
    }

    [Fact]
    public void The_record_survives_a_restart()
    {
        var options = Allowance(everyDays: 2);
        var now = DateTimeOffset.Now;

        var recorded = PauseState.Load(_config);
        recorded.Record("edge", now);
        recorded.Save(_config);

        var reloaded = PauseState.Load(_config);

        Assert.True(reloaded.IsPaused("edge", options, now));
        Assert.False(reloaded.CanPause("edge", options, now.AddDays(1)));
    }

    [Fact]
    public void A_corrupt_file_reads_as_never_paused()
    {
        File.WriteAllText(PauseState.PathFor(_config), "{ not json");

        // Failing this way errs toward enforcing, which is the right direction for a mistake to go.
        Assert.True(PauseState.Load(_config).CanPause("edge", Allowance(), DateTimeOffset.Now));
    }

    [Fact]
    public void One_targets_pause_does_not_touch_anothers()
    {
        var options = Allowance(everyDays: 1);
        var now = DateTimeOffset.Now;

        var recorded = PauseState.Load(_config);
        recorded.Record("edge", now);
        recorded.Save(_config);

        var state = PauseState.Load(_config);

        Assert.True(state.CanPause("codex", options, now));
        Assert.False(state.IsPaused("codex", options, now));
    }

    // ------------------------------------------------------------------ 放宽判定

    [Fact]
    public void Loosening_the_allowance_is_a_relaxation()
    {
        var before = ConfigWithPause(Allowance(everyDays: 3, minutes: 10));

        // Allowing one at all, allowing them more often, and allowing longer ones: three ways of
        // loosening, all three on the side of the rule that needs the token.
        Assert.Equal(ChangeDirection.Relaxing, ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(everyDays: 1, minutes: 10))));
        Assert.Equal(ChangeDirection.Relaxing, ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(everyDays: 3, minutes: 30))));

        var off = ConfigWithPause(new PauseOptions { Enabled = false, EveryDays = 3, Minutes = 10 });
        Assert.Equal(ChangeDirection.Relaxing, ConfigRelaxation.Classify(off, before));
    }

    [Fact]
    public void Tightening_the_allowance_is_free()
    {
        var before = ConfigWithPause(Allowance(everyDays: 3, minutes: 10));

        Assert.Equal(ChangeDirection.Tightening, ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(everyDays: 7, minutes: 10))));
        Assert.Equal(ChangeDirection.Tightening, ConfigRelaxation.Classify(before, ConfigWithPause(Allowance(everyDays: 3, minutes: 5))));
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
    public void A_nonsense_allowance_is_refused_by_the_validator()
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

        rule.Pause = new PauseOptions { Enabled = true, EveryDays = 1, Minutes = 0 };
        Assert.Contains(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);

        rule.Pause = new PauseOptions { Enabled = true, EveryDays = 0, Minutes = 10 };
        Assert.Contains(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);

        rule.Pause = new PauseOptions { Enabled = true, EveryDays = 1, Minutes = 10 };
        Assert.DoesNotContain(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void The_options_describe_themselves()
    {
        Assert.Equal("暂停：关", new PauseOptions { Enabled = false }.ToString());
        Assert.Contains("10", Allowance(everyDays: 1, minutes: 10).ToString());
    }
}
