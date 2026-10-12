using Sentinel.Core.Abstractions;
using Sentinel.Core.Engine;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

public class NotifyMessageTests
{
    private static ActionContext Context(ActionStage stage) => new(
        new WindowRef(1, "Codex", "ChatGPT", "", 0x1),
        new Target
        {
            Id = "t",
            DisplayName = "Codex",
            Rules = { new Rule { Id = "r1", Name = "拦 bilibili", Reminder = "别刷了，你设这条是为了考研" } },
        },
        new ContentSnapshot("https://bilibili.com", "哔哩哔哩", null, DateTimeOffset.Now),
        new Policy { Id = "p", Name = "p", Actions = { ActionType.Notify, ActionType.Minimize }, GraceSeconds = 5 },
        Verdict.Block("r1", "matched rule 'x'", "p"),
        DateTimeOffset.Now,
        stage);

    [Fact]
    public void Grace_notice_and_enforcement_notice_are_told_apart()
    {
        // They fire seconds apart for the same hit. If they read the same, one event looks like two.
        var warning = NotifyMessage.Build(Context(ActionStage.Notify));
        var acted = NotifyMessage.Build(Context(ActionStage.Enforce));

        Assert.NotEqual(warning.Title, acted.Title);
        Assert.NotEqual(warning.Body, acted.Body);
        Assert.False(warning.Acted);
        Assert.True(acted.Acted);
    }

    [Fact]
    public void Grace_notice_says_what_will_happen_and_when()
    {
        var warning = NotifyMessage.Build(Context(ActionStage.Notify));

        Assert.Contains("即将拦截", warning.Title);
        Assert.Contains("5 秒后执行", warning.Body);
        Assert.Contains("最小化窗口", warning.Body);
    }

    [Fact]
    public void Enforcement_notice_says_what_happened()
    {
        var acted = NotifyMessage.Build(Context(ActionStage.Enforce));

        Assert.Contains("已拦截", acted.Title);
        Assert.Contains("已执行", acted.Body);
        Assert.DoesNotContain("秒后执行", acted.Body);
    }

    [Fact]
    public void Body_is_collapsed_to_a_single_short_line()
    {
        // The body field is page text, which can be thousands of characters across many lines.
        var context = Context(ActionStage.Enforce) with
        {
            Snapshot = new ContentSnapshot(null, "第一行\n第二行\n第三行", null, DateTimeOffset.Now),
        };

        var message = NotifyMessage.Build(context);

        Assert.Contains("第一行", message.Body);
        Assert.DoesNotContain("第二行", message.Body);
    }

    [Fact]
    public void Notice_carries_a_timestamp_so_two_notices_are_tellable_apart()
    {
        var message = NotifyMessage.Build(Context(ActionStage.Enforce));

        Assert.Contains(DateTimeOffset.Now.ToString("HH:mm"), message.Body);
    }

    [Fact]
    public void The_ordinal_appears_only_from_the_second_hit()
    {
        var first = NotifyMessage.Build(Context(ActionStage.Enforce) with { HitNumber = 1 });
        var third = NotifyMessage.Build(Context(ActionStage.Enforce) with { HitNumber = 3 });

        Assert.DoesNotContain("第 1 次", first.Body);
        Assert.Contains("本次运行第 3 次", third.Body);
    }

    [Fact]
    public void Both_stages_of_one_hit_show_the_same_ordinal()
    {
        var warning = NotifyMessage.Build(Context(ActionStage.Notify) with { HitNumber = 2 });
        var acted = NotifyMessage.Build(Context(ActionStage.Enforce) with { HitNumber = 2 });

        Assert.Contains("本次运行第 2 次", warning.Body);
        Assert.Contains("本次运行第 2 次", acted.Body);
    }

    [Fact]
    public void The_notice_names_the_rule_that_fired()
    {
        // "Which app, which rule" is the question the reader actually has.
        Assert.Contains("规则：拦 bilibili", NotifyMessage.Build(Context(ActionStage.Enforce)).Body);
    }

    [Fact]
    public void A_rule_reminder_is_shown_when_it_fires()
    {
        Assert.Contains("别刷了，你设这条是为了考研", NotifyMessage.Build(Context(ActionStage.Enforce)).Body);
        Assert.Contains("别刷了，你设这条是为了考研", NotifyMessage.Build(Context(ActionStage.Notify)).Body);
    }

    private static Policy Reminding() => new()
    {
        Id = "p",
        Name = "只提醒",
        Actions = { ActionType.Notify },
        RepeatSeconds = 300,
    };

    [Fact]
    public void A_reminding_policy_does_not_claim_to_have_blocked_anything()
    {
        // "已拦截" tells the reader the offending thing was stopped. Nothing was.
        var message = NotifyMessage.Build(Context(ActionStage.Enforce) with { Policy = Reminding() });

        Assert.Contains("提醒", message.Title);
        Assert.DoesNotContain("拦截", message.Title);
        Assert.DoesNotContain("已执行", message.Body);
    }

    [Fact]
    public void A_reminding_policy_reads_the_same_at_both_stages()
    {
        // There is nothing to defer, so there is no "about to" phase to word differently.
        var early = NotifyMessage.Build(Context(ActionStage.Notify) with { Policy = Reminding() });
        var late = NotifyMessage.Build(Context(ActionStage.Enforce) with { Policy = Reminding() });

        Assert.Equal(early.Title, late.Title);
    }

    [Fact]
    public void A_reminding_policy_still_carries_the_rule_and_the_reminder()
    {
        var body = NotifyMessage.Build(Context(ActionStage.Enforce) with { Policy = Reminding() }).Body;

        Assert.Contains("规则：拦 bilibili", body);
        Assert.Contains("别刷了，你设这条是为了考研", body);
    }

    [Fact]
    public void A_rule_without_a_reminder_adds_nothing()
    {
        var context = Context(ActionStage.Enforce) with
        {
            Target = new Target { Id = "t", DisplayName = "Codex", Rules = { new Rule { Id = "r1", Name = "r" } } },
        };

        Assert.DoesNotContain("别刷了", NotifyMessage.Build(context).Body);
    }

    [Fact]
    public void A_rule_deleted_since_the_hit_is_described_rather_than_named_by_id()
    {
        var context = Context(ActionStage.Enforce) with
        {
            Verdict = Verdict.Block("gone", "matched rule 'x'", "p"),
        };

        var body = NotifyMessage.Build(context).Body;
        Assert.Contains("已不存在", body);
        Assert.DoesNotContain("规则：gone", body);
    }
}
