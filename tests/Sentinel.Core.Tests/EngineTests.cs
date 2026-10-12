using Sentinel.Core.Engine;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

public class MatchEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Keyword_url_matches_case_insensitive()
    {
        var spec = new MatchSpec { Type = MatchKind.Keyword, Fields = { MatchField.Url }, Patterns = { "bilibili" } };
        var snap = new ContentSnapshot("https://www.BILIBILI.com/video", null, null, Now);
        Assert.True(MatchEvaluator.Matches(spec, snap));
    }

    [Fact]
    public void Keyword_is_case_sensitive_when_requested()
    {
        var spec = new MatchSpec { Type = MatchKind.Keyword, CaseSensitive = true, Fields = { MatchField.Title }, Patterns = { "Game" } };
        var snap = new ContentSnapshot(null, "game over", null, Now);
        Assert.False(MatchEvaluator.Matches(spec, snap));
    }

    [Fact]
    public void Regex_matches_title()
    {
        var spec = new MatchSpec { Type = MatchKind.Regex, Fields = { MatchField.Title }, Patterns = { @"(?i)(抖音|快手|微博)" } };
        var snap = new ContentSnapshot(null, "刷抖音", null, Now);
        Assert.True(MatchEvaluator.Matches(spec, snap));
    }

    [Fact]
    public void Body_matches_when_field_selected()
    {
        var spec = new MatchSpec { Type = MatchKind.Keyword, Fields = { MatchField.Body }, Patterns = { "游戏" } };
        var snap = new ContentSnapshot(null, null, "这是一段关于游戏的正文", Now);
        Assert.True(MatchEvaluator.Matches(spec, snap));
    }

    [Fact]
    public void Empty_fields_default_to_url_and_title()
    {
        var spec = new MatchSpec { Type = MatchKind.Keyword, Patterns = { "hello" } };
        Assert.True(MatchEvaluator.Matches(spec, new ContentSnapshot(null, "hello world", null, Now)));
        Assert.False(MatchEvaluator.Matches(spec, new ContentSnapshot(null, null, "hello in body", Now)));
    }
}

public class RuleEngineTests
{
    [Fact]
    public void Highest_priority_block_wins()
    {
        var target = new Target
        {
            Id = "t1",
            Rules =
            {
                new Rule { Id = "r-low", Priority = 1, Match = new MatchSpec { Type = MatchKind.Keyword, Fields = { MatchField.Title }, Patterns = { "low" } }, PolicyId = "p1" },
                new Rule { Id = "r-high", Priority = 10, Match = new MatchSpec { Type = MatchKind.Keyword, Fields = { MatchField.Title }, Patterns = { "high" } }, PolicyId = "p2" },
            },
        };
        var engine = new RuleEngine(Array.Empty<Schedule>());
        var snap = new ContentSnapshot(null, "low high", null, DateTimeOffset.UtcNow);

        var verdict = engine.Evaluate(target, snap, DateTimeOffset.UtcNow);

        Assert.True(verdict.IsBlocked);
        Assert.Equal("r-high", verdict.RuleId);
        Assert.Equal("p2", verdict.PolicyId);
    }

    [Fact]
    public void Whitelist_overrides_block_by_priority()
    {
        var target = new Target
        {
            Rules =
            {
                new Rule { Id = "block", Priority = 10, Match = new MatchSpec { Type = MatchKind.Keyword, Fields = { MatchField.Url }, Patterns = { "youtube" } }, PolicyId = "p1" },
                new Rule { Id = "allow", Priority = 100, Match = new MatchSpec { Type = MatchKind.Whitelist, Fields = { MatchField.Url }, Patterns = { "youtube.com/edu" } }, PolicyId = "p0" },
            },
        };
        var engine = new RuleEngine(Array.Empty<Schedule>());
        var snap = new ContentSnapshot("https://youtube.com/edu/course", null, null, DateTimeOffset.UtcNow);

        Assert.False(engine.Evaluate(target, snap, DateTimeOffset.UtcNow).IsBlocked);
    }

    [Fact]
    public void No_match_yields_allow()
    {
        var target = new Target
        {
            Rules =
            {
                new Rule { Id = "r1", Match = new MatchSpec { Type = MatchKind.Keyword, Fields = { MatchField.Url }, Patterns = { "bilibili" } }, PolicyId = "p1" },
            },
        };
        var engine = new RuleEngine(Array.Empty<Schedule>());
        var snap = new ContentSnapshot("https://example.com", null, null, DateTimeOffset.UtcNow);

        Assert.False(engine.Evaluate(target, snap, DateTimeOffset.UtcNow).IsBlocked);
    }

    [Fact]
    public void Schedule_gates_rule()
    {
        var schedule = new Schedule { Id = "work", DaysOfWeek = { DayOfWeek.Monday }, TimeWindows = { new TimeWindow("09:00", "17:00") } };
        var target = new Target
        {
            Rules =
            {
                new Rule { Id = "r1", ScheduleId = "work", Priority = 1, Match = new MatchSpec { Type = MatchKind.Keyword, Fields = { MatchField.Url }, Patterns = { "x" } }, PolicyId = "p1" },
            },
        };
        var engine = new RuleEngine(new[] { schedule });
        var snap = new ContentSnapshot("https://x.com", null, null, DateTimeOffset.UtcNow);

        var mondayMorning = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero); // Monday
        var mondayNight = new DateTimeOffset(2026, 1, 5, 20, 0, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.Zero); // Saturday

        Assert.True(engine.Evaluate(target, snap, mondayMorning).IsBlocked);
        Assert.False(engine.Evaluate(target, snap, mondayNight).IsBlocked);
        Assert.False(engine.Evaluate(target, snap, saturday).IsBlocked);
    }
}

public class ScheduleEvaluatorTests
{
    [Fact]
    public void Overnight_window_spans_midnight()
    {
        var schedule = new Schedule { DaysOfWeek = { DayOfWeek.Monday }, TimeWindows = { new TimeWindow("22:00", "06:00") } };
        var monday23 = new DateTimeOffset(2026, 1, 5, 23, 0, 0, TimeSpan.Zero);
        var monday02 = new DateTimeOffset(2026, 1, 5, 2, 0, 0, TimeSpan.Zero);
        var monday12 = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ScheduleEvaluator.IsActive(schedule, monday23));
        Assert.True(ScheduleEvaluator.IsActive(schedule, monday02));
        Assert.False(ScheduleEvaluator.IsActive(schedule, monday12));
    }

    [Fact]
    public void Empty_days_means_every_day()
    {
        var schedule = new Schedule { TimeWindows = { new TimeWindow("00:00", "23:59") } };
        var anyDay = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.True(ScheduleEvaluator.IsActive(schedule, anyDay));
    }
}

public class ActionControllerTests
{
    private static readonly Policy Immediate = new() { GraceSeconds = 0, CooldownSeconds = 0, DebounceCount = 1 };

    [Fact]
    public void Grace_notifies_then_enforces()
    {
        var ctl = new ActionController();
        var policy = new Policy { GraceSeconds = 10, CooldownSeconds = 0, DebounceCount = 1 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(ActionStage.Notify, ctl.Decide("k", true, policy, t0).Stage);
        Assert.Equal(ActionStage.None, ctl.Decide("k", true, policy, t0.AddSeconds(5)).Stage);
        Assert.Equal(ActionStage.Enforce, ctl.Decide("k", true, policy, t0.AddSeconds(10)).Stage);
    }

    [Fact]
    public void Zero_grace_enforces_immediately()
    {
        var ctl = new ActionController();
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(ActionStage.Enforce, ctl.Decide("k", true, Immediate, t0).Stage);
    }

    [Fact]
    public void Debounce_requires_n_consecutive_hits()
    {
        var ctl = new ActionController();
        var policy = new Policy { DebounceCount = 3, GraceSeconds = 0, CooldownSeconds = 0 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(ActionStage.None, ctl.Decide("k", true, policy, t0).Stage);
        Assert.Equal(ActionStage.None, ctl.Decide("k", true, policy, t0.AddSeconds(1)).Stage);
        Assert.Equal(ActionStage.Enforce, ctl.Decide("k", true, policy, t0.AddSeconds(2)).Stage);
    }

    [Fact]
    public void Cooldown_suppresses_reenforce()
    {
        var ctl = new ActionController();
        var policy = new Policy { DebounceCount = 1, GraceSeconds = 0, CooldownSeconds = 30 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(ActionStage.Enforce, ctl.Decide("k", true, policy, t0).Stage);
        Assert.Equal(ActionStage.None, ctl.Decide("k", true, policy, t0.AddSeconds(10)).Stage);
        Assert.Equal(ActionStage.Enforce, ctl.Decide("k", true, policy, t0.AddSeconds(31)).Stage);
    }

    [Fact]
    public void Allow_resets_episode()
    {
        var ctl = new ActionController();
        var policy = new Policy { DebounceCount = 1, GraceSeconds = 10, CooldownSeconds = 0 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(ActionStage.Notify, ctl.Decide("k", true, policy, t0).Stage);
        Assert.Equal(ActionStage.None, ctl.Decide("k", false, policy, t0.AddSeconds(3)).Stage);
        Assert.Equal(ActionStage.Notify, ctl.Decide("k", true, policy, t0.AddSeconds(4)).Stage);
    }

    [Fact]
    public void A_reminding_policy_ignores_its_grace_period()
    {
        // Grace exists to give the user a moment before something is done to their window. A policy
        // that only reminds does nothing to it, so waiting would just say the same thing twice.
        var ctl = new ActionController();
        var policy = new Policy { Id = "p", Actions = { ActionType.Notify }, GraceSeconds = 30 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        var decision = ctl.Decide("k", true, policy, t0);

        Assert.Equal(ActionStage.Enforce, decision.Stage);
        Assert.Equal(1, decision.Episode);
    }

    [Fact]
    public void An_acting_policy_still_honours_its_grace_period()
    {
        var ctl = new ActionController();
        var policy = new Policy { Id = "p", Actions = { ActionType.Minimize }, GraceSeconds = 30 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(ActionStage.Notify, ctl.Decide("k", true, policy, t0).Stage);
    }

    [Fact]
    public void Cooldown_blocks_a_whole_new_episode_not_just_the_enforce_step()
    {
        // A page whose content keeps changing must not be able to re-notify + re-enforce its way
        // straight through the cooldown.
        var ctl = new ActionController();
        var policy = new Policy { DebounceCount = 1, GraceSeconds = 3, CooldownSeconds = 60 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(ActionStage.Notify, ctl.Decide("k", true, policy, t0).Stage);
        Assert.Equal(ActionStage.Enforce, ctl.Decide("k", true, policy, t0.AddSeconds(3)).Stage);

        // New blocked page, well inside the cooldown: silently ignored, no second notification.
        Assert.Equal(ActionStage.None, ctl.Decide("k", true, policy, t0.AddSeconds(10)).Stage);
        Assert.Equal(ActionStage.None, ctl.Decide("k", true, policy, t0.AddSeconds(59)).Stage);

        // Cooldown expired: a fresh episode may start.
        Assert.Equal(ActionStage.Notify, ctl.Decide("k", true, policy, t0.AddSeconds(64)).Stage);
    }

    [Fact]
    public void Episode_ordinal_is_shared_by_both_stages_and_counts_each_hit()
    {
        var ctl = new ActionController();
        var policy = new Policy { GraceSeconds = 5, CooldownSeconds = 0 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        // One hit: the grace notice and the enforcement must carry the same number, or they read as
        // two separate occurrences.
        var warning = ctl.Decide("k", true, policy, t0);
        Assert.Equal(ActionStage.Notify, warning.Stage);
        Assert.Equal(1, warning.Episode);

        var enforced = ctl.Decide("k", true, policy, t0.AddSeconds(5));
        Assert.Equal(ActionStage.Enforce, enforced.Stage);
        Assert.Equal(1, enforced.Episode);

        // Navigated away and back: a genuinely new hit, numbered as such.
        ctl.Decide("k", false, policy, t0.AddSeconds(6));
        Assert.Equal(2, ctl.Decide("k", true, policy, t0.AddSeconds(7)).Episode);
    }

    [Fact]
    public void Hit_count_survives_content_becoming_allowed_again()
    {
        var ctl = new ActionController();
        var policy = new Policy { GraceSeconds = 0, CooldownSeconds = 0 };
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i + 1, ctl.Decide("k", true, policy, t0.AddSeconds(i * 2)).Episode);
            ctl.Decide("k", false, policy, t0.AddSeconds(i * 2 + 1));
        }
    }
}
