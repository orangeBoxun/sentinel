using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

public class ConfigDiffTests
{
    private static AppConfig Build(Action<AppConfig>? tweak = null)
    {
        var config = new AppConfig();
        config.Policies.Add(new Policy { Id = "p1", Name = "p1", Actions = { ActionType.Close } });
        config.Policies.Add(new Policy { Id = "p2", Name = "p2", Actions = { ActionType.Notify } });
        config.Schedules.Add(new Schedule { Id = "s1", TimeWindows = { new TimeWindow("09:00", "17:00") } });

        config.Targets.Add(Target("a", "Alpha", "p1", scheduleId: null));
        config.Targets.Add(Target("b", "Beta", "p2", scheduleId: "s1"));

        tweak?.Invoke(config);
        return config;

        static Target Target(string id, string process, string policyId, string? scheduleId) => new()
        {
            Id = id,
            DisplayName = id,
            ProcessNames = { process },
            Rules =
            {
                new Rule
                {
                    Id = "r",
                    Name = "r",
                    PolicyId = policyId,
                    ScheduleId = scheduleId,
                    Match = new MatchSpec { Patterns = { "x" } },
                },
            },
        };
    }

    [Fact]
    public void Identical_configs_report_no_change()
    {
        Assert.Empty(ConfigDiff.ChangedTargetIds(Build(), Build()));
    }

    [Fact]
    public void Editing_one_target_reports_only_that_target()
    {
        var after = Build(c => c.Targets[0].Rules[0].Match.Patterns.Add("y"));

        Assert.Equal(new[] { "a" }, ConfigDiff.ChangedTargetIds(Build(), after));
    }

    [Fact]
    public void Editing_a_shared_policy_reports_the_targets_that_use_it()
    {
        var after = Build(c => c.Policies.First(p => p.Id == "p1").GraceSeconds = 30);

        Assert.Equal(new[] { "a" }, ConfigDiff.ChangedTargetIds(Build(), after));
    }

    [Fact]
    public void Editing_a_policy_nobody_uses_reports_nothing()
    {
        var after = Build(c =>
        {
            c.Policies.Add(new Policy { Id = "unused", Name = "unused", Actions = { ActionType.Notify } });
        });

        // Adding an unused policy must not disturb anything that is running.
        Assert.Empty(ConfigDiff.ChangedTargetIds(Build(), after));
    }

    [Fact]
    public void Editing_a_schedule_reports_the_targets_that_use_it()
    {
        var after = Build(c => c.Schedules.First(s => s.Id == "s1").TimeWindows.Add(new TimeWindow("18:00", "20:00")));

        Assert.Equal(new[] { "b" }, ConfigDiff.ChangedTargetIds(Build(), after));
    }

    [Fact]
    public void Adding_and_removing_targets_is_reported()
    {
        var added = Build(c => c.Targets.Add(new Target { Id = "c", ProcessNames = { "Gamma" } }));
        Assert.Equal(new[] { "c" }, ConfigDiff.ChangedTargetIds(Build(), added));

        var removed = Build(c => c.Targets.RemoveAt(0));
        Assert.Equal(new[] { "a" }, ConfigDiff.ChangedTargetIds(Build(), removed));
    }

    [Fact]
    public void Toggling_safe_mode_reports_every_target()
    {
        // Arming the monitor has to take effect on what is already on screen.
        var after = Build(c => c.Ui.DryRun = !c.Ui.DryRun);

        Assert.Equal(2, ConfigDiff.ChangedTargetIds(Build(), after).Count);
    }
}
