using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

public class ConfigValidatorTests
{
    private static AppConfig Valid()
    {
        var config = new AppConfig();
        config.Policies.Add(new Policy { Id = "p1", Name = "p", Actions = { ActionType.Close } });
        config.Targets.Add(new Target
        {
            Id = "t1",
            ProcessNames = { "msedge" },
            Rules =
            {
                new Rule
                {
                    Id = "r1",
                    PolicyId = "p1",
                    Match = new MatchSpec { Type = MatchKind.Keyword, Patterns = { "bilibili" } },
                },
            },
        });
        return config;
    }

    private static bool HasError(IReadOnlyList<ConfigIssue> issues, string fragment) =>
        issues.Any(i => i.Severity == IssueSeverity.Error && i.ToString().Contains(fragment, StringComparison.Ordinal));

    private static bool HasWarning(IReadOnlyList<ConfigIssue> issues, string fragment) =>
        issues.Any(i => i.Severity == IssueSeverity.Warning && i.ToString().Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public void Valid_config_has_no_issues()
    {
        Assert.Empty(ConfigValidator.Validate(Valid()));
    }

    [Fact]
    public void Rule_pointing_at_missing_policy_is_an_error()
    {
        // The failure this catches: the monitor logs "no policy bound" and never acts, silently.
        var config = Valid();
        config.Targets[0].Rules[0].PolicyId = "does-not-exist";

        Assert.True(HasError(ConfigValidator.Validate(config), "does not exist"));
    }

    [Fact]
    public void Rule_with_no_policy_is_a_warning()
    {
        // 以前是 Error，而这正是"加不了规则"的根因之一：新建的规则出生时既没有策略、也没有
        // 匹配内容，两样都算 Error 的话，**按「新建」的那一下必定被拒**。
        // 没绑策略是"还没填"；绑到一个不存在的策略才是"填错了"（另有一条测试管那个）。
        var config = Valid();
        config.Targets[0].Rules[0].PolicyId = "";

        var issues = ConfigValidator.Validate(config);

        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("还没有绑策略"));
    }

    [Fact]
    public void Rule_with_no_patterns_is_a_warning_too()
    {
        // 同一条道理：刚建出来的规则一定还没有匹配内容 —— 那是用户正要接着填的。
        // 以前这里也是 Error，于是"新建规则"这条最普通的操作一开始就走不通。
        var config = Valid();
        config.Targets[0].Rules[0].Match.Patterns.Clear();

        var issues = ConfigValidator.Validate(config);

        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("匹配内容"));
    }

    [Fact]
    public void Whitelist_rule_needs_no_policy()
    {
        var config = Valid();
        config.Targets[0].Rules.Add(new Rule
        {
            Id = "allow",
            PolicyId = "",
            Match = new MatchSpec { Type = MatchKind.Whitelist, Patterns = { "example.com" } },
        });

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void Matching_body_without_scanning_it_is_a_warning()
    {
        // The other failure that bit this project: a body rule on a target that never reads the body.
        var config = Valid();
        config.Targets[0].Rules[0].Match.Fields = new List<MatchField> { MatchField.Body };
        config.Targets[0].Reader.ScanBody = false;

        var issues = ConfigValidator.Validate(config);
        Assert.True(HasWarning(issues, "ScanBody=false"));
        Assert.False(HasError(issues, "ScanBody"));
    }

    [Fact]
    public void Matching_body_with_scanning_on_is_fine()
    {
        var config = Valid();
        config.Targets[0].Rules[0].Match.Fields = new List<MatchField> { MatchField.Body };
        config.Targets[0].Reader.ScanBody = true;

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void A_half_filled_provider_is_a_warning_not_an_error()
    {
        // 这条是给一个真实事故善后：没填完的服务商以前算 Error，而保存时只要有一个 Error
        // 就整次拒绝 —— 于是用户加不了规则、也删不掉那个服务商（删除同样要保存，而它还在）。
        // 一个"还没填完"的中间状态把整个配置锁死了。
        //
        // 它本来也没有破坏性：没填完的服务商只是用不了，模型规则查不到服务商时会放行 ——
        // 那正是"监督没配好"该有的样子。
        var config = new AppConfig();
        config.Models.Add(new ModelProvider { Id = "half", Name = "半成品" });

        var issues = ConfigValidator.Validate(config);

        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);

        // 但仍然要说出来 —— 不说的话它会一直是个"配了却什么也不做"的服务商。
        Assert.Contains(issues, i => i.Message.Contains("请求地址"));
        Assert.Contains(issues, i => i.Message.Contains("模型名"));
    }

    [Fact]
    public void A_malformed_address_is_still_an_error()
    {
        // 和上面那条划清界限：「没填」是中间状态，「填错了」是真错误。
        var config = new AppConfig();
        config.Models.Add(new ModelProvider
        {
            Id = "bad",
            Name = "填错了",
            Endpoint = "api.deepseek.com/anthropic",
            Model = "some-model",
        });

        var issues = ConfigValidator.Validate(config);

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Duplicate_target_ids_are_an_error()
    {
        var config = Valid();
        config.Targets.Add(new Target { Id = "t1", ProcessNames = { "chrome" } });

        Assert.True(HasError(ConfigValidator.Validate(config), "duplicate target Id"));
    }

    [Fact]
    public void Invalid_regex_pattern_is_an_error()
    {
        var config = Valid();
        config.Targets[0].Rules[0].Match.Type = MatchKind.Regex;
        config.Targets[0].Rules[0].Match.Patterns = new List<string> { "([unclosed" };

        Assert.True(HasError(ConfigValidator.Validate(config), "not a valid regex"));
    }

    [Fact]
    public void Invalid_time_window_is_an_error()
    {
        var config = Valid();
        config.Schedules.Add(new Schedule { Id = "s1", TimeWindows = { new TimeWindow("25:00", "99:99") } });

        Assert.True(HasError(ConfigValidator.Validate(config), "invalid time window"));
    }

    [Fact]
    public void Target_matching_every_window_is_an_error()
    {
        var config = Valid();
        config.Targets[0].ProcessNames.Clear();
        config.Targets[0].WindowTitlePattern = null;

        Assert.True(HasError(ConfigValidator.Validate(config), "matches every window"));
    }

    [Fact]
    public void Missing_schedule_reference_is_an_error()
    {
        var config = Valid();
        config.Targets[0].Rules[0].ScheduleId = "nope";

        Assert.True(HasError(ConfigValidator.Validate(config), "does not exist"));
    }

    [Fact]
    public void Policy_with_no_actions_is_a_warning()
    {
        var config = Valid();
        config.Policies[0].Actions.Clear();

        Assert.True(HasWarning(ConfigValidator.Validate(config), "no actions"));
    }
}

public class ConfigStoreStrictLoadTests
{
    private static string TempPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"sentinel-strict-{tag}-{Guid.NewGuid():N}.json");

    [Fact]
    public void Try_load_reports_corrupt_json_instead_of_defaulting()
    {
        // LoadOrDefault falls back to an empty config; import must NOT, or it would overwrite the
        // destination with nothing.
        var path = TempPath("corrupt");
        try
        {
            File.WriteAllText(path, "{ not json ");

            Assert.False(ConfigStore.TryLoad(path, out _, out var error));
            Assert.NotNull(error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Try_load_reports_a_missing_file()
    {
        Assert.False(ConfigStore.TryLoad(TempPath("missing"), out _, out var error));
        Assert.Contains("not found", error);
    }

    [Fact]
    public void Try_load_round_trips()
    {
        var path = TempPath("ok");
        try
        {
            var config = new AppConfig();
            config.Targets.Add(new Target { Id = "t1", ProcessNames = { "msedge" } });
            ConfigStore.Save(config, path);

            Assert.True(ConfigStore.TryLoad(path, out var loaded, out _));
            Assert.Equal("t1", Assert.Single(loaded.Targets).Id);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
