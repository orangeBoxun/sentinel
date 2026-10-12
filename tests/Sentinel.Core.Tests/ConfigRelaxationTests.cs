using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

public class ConfigRelaxationTests
{
    private static AppConfig Baseline()
    {
        var config = new AppConfig();
        config.Ui.DryRun = false;
        config.Logging.LogUrlHash = true;

        config.Policies.Add(new Policy
        {
            Id = "p",
            Name = "p",
            Actions = { ActionType.Notify, ActionType.Minimize },
            GraceSeconds = 3,
            CooldownSeconds = 60,
            DebounceCount = 1,
        });

        config.Schedules.Add(new Schedule
        {
            Id = "s",
            DaysOfWeek = { DayOfWeek.Monday, DayOfWeek.Tuesday },
            TimeWindows = { new TimeWindow("09:00", "18:00") },
        });

        config.Targets.Add(new Target
        {
            Id = "t",
            DisplayName = "t",
            Enabled = true,
            ProcessNames = { "msedge" },
            Reader = new ReaderOptions { IntervalMs = 1000, MaxBodyChars = 4096 },
            Rules =
            {
                new Rule
                {
                    Id = "r",
                    Name = "r",
                    Enabled = true,
                    PolicyId = "p",
                    Match = new MatchSpec
                    {
                        Type = MatchKind.Keyword,
                        Fields = { MatchField.Url },
                        Patterns = { "bilibili" },
                    },
                },
            },
        });

        return config;
    }

    /// <summary>Applies an edit to a fresh baseline and reports the direction.</summary>
    private static ChangeDirection Direction(Action<AppConfig> edit) => Direction(_ => { }, edit);

    /// <summary>
    /// For edits whose starting value is not the baseline default: <paramref name="prepare"/> runs on
    /// both sides, so the comparison is between two configs that both start from that state.
    /// </summary>
    private static ChangeDirection Direction(Action<AppConfig> prepare, Action<AppConfig> edit)
    {
        var before = Baseline();
        prepare(before);

        var after = Baseline();
        prepare(after);
        edit(after);

        return ConfigRelaxation.Classify(before, after);
    }

    private static Rule FirstRule(AppConfig c) => c.Targets[0].Rules[0];
    private static Policy FirstPolicy(AppConfig c) => c.Policies[0];

    [Fact]
    public void No_change_is_neutral()
    {
        Assert.Equal(ChangeDirection.Neutral, Direction(_ => { }));
    }

    [Fact]
    public void Cosmetic_edits_are_neutral()
    {
        // Renaming, and the reminder text that is shown to the user, change no enforcement.
        Assert.Equal(ChangeDirection.Neutral, Direction(c => FirstPolicy(c).Name = "renamed"));
        Assert.Equal(ChangeDirection.Neutral, Direction(c => FirstRule(c).Name = "renamed"));
        Assert.Equal(ChangeDirection.Neutral, Direction(c => c.Targets[0].DisplayName = "renamed"));
        Assert.Equal(ChangeDirection.Neutral, Direction(c => FirstRule(c).Reminder = "别刷了"));

        // A policy nothing references changes nothing.
        Assert.Equal(ChangeDirection.Neutral,
            Direction(c => c.Policies.Add(new Policy { Id = "unused", Actions = { ActionType.Notify } })));
    }

    // ------------------------------------------------------------------ 上膛

    [Fact]
    public void Leaving_safe_mode_is_tightening_and_entering_it_is_relaxing()
    {
        static void Safe(AppConfig c) => c.Ui.DryRun = true;

        Assert.Equal(ChangeDirection.Tightening, Direction(Safe, c => c.Ui.DryRun = false));
        Assert.Equal(ChangeDirection.Relaxing, Direction(_ => { }, Safe));
    }

    [Fact]
    public void Turning_logging_off_is_relaxing()
    {
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Logging.LogUrlHash = false));
        Assert.Equal(ChangeDirection.Tightening, Direction(c => c.Logging.LogBodyText = true));
    }

    // ------------------------------------------------------------------ 目标与规则

    [Fact]
    public void Adding_a_target_is_tightening_and_removing_one_is_relaxing()
    {
        Assert.Equal(ChangeDirection.Tightening,
            Direction(c => c.Targets.Add(new Target { Id = "t2", ProcessNames = { "chrome" } })));

        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Targets.Clear()));
    }

    [Fact]
    public void Disabling_a_target_or_rule_is_relaxing()
    {
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Targets[0].Enabled = false));
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstRule(c).Enabled = false));
    }

    [Fact]
    public void Adding_a_blocking_rule_is_tightening_but_adding_a_whitelist_rule_is_relaxing()
    {
        static Rule NewRule(MatchKind type) => new()
        {
            Id = "r2",
            Name = "r2",
            PolicyId = "p",
            Match = new MatchSpec { Type = type, Fields = { MatchField.Url }, Patterns = { "x" } },
        };

        Assert.Equal(ChangeDirection.Tightening, Direction(c => c.Targets[0].Rules.Add(NewRule(MatchKind.Keyword))));

        // A whitelist rule exists to let things through — it can only weaken enforcement.
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Targets[0].Rules.Add(NewRule(MatchKind.Whitelist))));
    }

    [Fact]
    public void Growing_a_blocking_rule_is_tightening_but_growing_a_whitelist_is_relaxing()
    {
        Assert.Equal(ChangeDirection.Tightening, Direction(c => FirstRule(c).Match.Patterns.Add("youtube")));
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstRule(c).Match.Patterns.Remove("bilibili")));

        // The same two edits on a whitelist rule read backwards. (Changing the type is itself a
        // relaxation, so the whitelist is set up beforehand rather than alongside.)
        static void Whitelist(AppConfig c) => FirstRule(c).Match.Type = MatchKind.Whitelist;

        Assert.Equal(ChangeDirection.Relaxing, Direction(Whitelist, c => FirstRule(c).Match.Patterns.Add("youtube")));
        Assert.Equal(ChangeDirection.Tightening, Direction(Whitelist, c => FirstRule(c).Match.Patterns.Remove("bilibili")));

        // Disabling a whitelist rule stops it permitting things, which is stricter.
        Assert.Equal(ChangeDirection.Tightening, Direction(Whitelist, c => FirstRule(c).Enabled = false));
        Assert.Equal(ChangeDirection.Relaxing, Direction(Whitelist, c => FirstRule(c).Match.Type = MatchKind.Keyword));

        // Turning a blocking rule into a whitelist one is the clearest relaxation there is.
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstRule(c).Match.Type = MatchKind.Whitelist));
    }

    [Fact]
    public void Making_matching_case_sensitive_is_relaxing()
    {
        // It covers strictly less text: "Bilibili" stops matching "bilibili".
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstRule(c).Match.CaseSensitive = true));
    }

    [Fact]
    public void Gathering_a_schedule_is_relaxing_and_dropping_one_is_tightening()
    {
        static void Scheduled(AppConfig c) => FirstRule(c).ScheduleId = "s";

        // A schedule limits when the rule applies, so acquiring one narrows enforcement…
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstRule(c).ScheduleId = "s"));

        // …and dropping it puts the rule back on all the time.
        Assert.Equal(ChangeDirection.Tightening, Direction(Scheduled, c => FirstRule(c).ScheduleId = null));
    }

    [Fact]
    public void Repointing_a_rule_at_another_policy_is_relaxing()
    {
        // Even "the same" policy under a different id: which policy runs cannot be judged stricter.
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstRule(c).PolicyId = "other"));
    }

    // ------------------------------------------------------------------ 策略

    [Fact]
    public void Removing_a_policy_action_is_relaxing_and_adding_one_is_tightening()
    {
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstPolicy(c).Actions.Remove(ActionType.Minimize)));
        Assert.Equal(ChangeDirection.Tightening, Direction(c => FirstPolicy(c).Actions.Add(ActionType.Kill)));
    }

    [Fact]
    public void Shrinking_the_grace_period_is_tightening_and_growing_it_is_relaxing()
    {
        Assert.Equal(ChangeDirection.Tightening, Direction(c => FirstPolicy(c).GraceSeconds = 1));
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstPolicy(c).GraceSeconds = 30));
    }

    [Fact]
    public void Longer_cooldown_and_bigger_debounce_are_relaxing()
    {
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstPolicy(c).CooldownSeconds = 300));
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => FirstPolicy(c).DebounceCount = 5));
    }

    [Fact]
    public void Turning_off_the_repeat_reminder_is_relaxing()
    {
        static void Repeating(AppConfig c) => FirstPolicy(c).RepeatSeconds = 300;

        Assert.Equal(ChangeDirection.Relaxing, Direction(Repeating, c => FirstPolicy(c).RepeatSeconds = 0));
        Assert.Equal(ChangeDirection.Tightening, Direction(Repeating, c => FirstPolicy(c).RepeatSeconds = 60));
        Assert.Equal(ChangeDirection.Relaxing, Direction(Repeating, c => FirstPolicy(c).RepeatSeconds = 900));

        // Switching it on at all is stricter than never repeating.
        Assert.Equal(ChangeDirection.Tightening, Direction(c => FirstPolicy(c).RepeatSeconds = 300));
    }

    [Fact]
    public void Softening_a_fullscreen_alert_to_a_balloon_is_relaxing()
    {
        static void Fullscreen(AppConfig c) => FirstPolicy(c).AlertStyle = AlertStyle.Fullscreen;

        Assert.Equal(ChangeDirection.Relaxing, Direction(Fullscreen, c => FirstPolicy(c).AlertStyle = AlertStyle.Balloon));
        Assert.Equal(ChangeDirection.Tightening, Direction(c => FirstPolicy(c).AlertStyle = AlertStyle.Fullscreen));
    }

    [Fact]
    public void Removing_a_policy_is_relaxing()
    {
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Policies.Clear()));
    }

    // ------------------------------------------------------------------ 时间表

    [Fact]
    public void Narrowing_a_schedule_is_relaxing_and_widening_it_is_tightening()
    {
        Assert.Equal(ChangeDirection.Tightening,
            Direction(c => c.Schedules[0].TimeWindows.Add(new TimeWindow("19:00", "21:00"))));

        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Schedules[0].TimeWindows.Clear()));
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Schedules[0].DaysOfWeek.Remove(DayOfWeek.Tuesday)));
    }

    [Fact]
    public void Shifting_a_schedule_without_gaining_time_is_relaxing()
    {
        // The same number of hours, moved somewhere else: not classifiable as stricter, so it is not.
        Assert.Equal(ChangeDirection.Relaxing,
            Direction(c => c.Schedules[0].TimeWindows[0] = new TimeWindow("20:00", "05:00")));
    }

    // ------------------------------------------------------------------ 混装

    [Fact]
    public void One_relaxation_makes_the_whole_save_a_relaxation()
    {
        // A tightening change must not be able to smuggle a relaxation through beside it.
        Assert.Equal(ChangeDirection.Relaxing, Direction(c =>
        {
            c.Targets.Add(new Target { Id = "t2", ProcessNames = { "chrome" } });   // tightening
            FirstRule(c).Enabled = false;                                           // relaxing
        }));
    }

    [Fact]
    public void Protect_config_edits_are_never_called_stricter()
    {
        static void Protected(AppConfig c) => c.Protection.Enabled = true;

        Assert.Equal(ChangeDirection.Relaxing, Direction(Protected, c => c.Protection.Enabled = false));
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Protection.SecretHash = "different"));
        Assert.Equal(ChangeDirection.Relaxing, Direction(c => c.Protection.Mode = ProtectionMode.Password));
    }

    [Fact]
    public void RequiresToken_matches_the_classification()
    {
        var before = Baseline();
        var after = Baseline();
        after.Targets[0].Rules[0].Enabled = false;

        Assert.True(ConfigRelaxation.RequiresToken(before, after));

        var stricter = Baseline();
        stricter.Targets[0].Rules[0].Match.Patterns.Add("youtube");
        Assert.False(ConfigRelaxation.RequiresToken(before, stricter));
    }
}
