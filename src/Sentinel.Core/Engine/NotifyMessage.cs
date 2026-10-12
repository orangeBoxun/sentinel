using Sentinel.Core.Abstractions;
using Sentinel.Core.Models;

namespace Sentinel.Core.Engine;

/// <summary>
/// Text for a rule-hit notification.
///
/// A policy with both a grace period and a Notify action produces TWO balloons for the same hit —
/// one when the grace starts, one when the actions run. Identical-looking balloons a few seconds
/// apart read as the same event firing repeatedly, so the two are deliberately worded, and the
/// callers icon them, differently.
/// </summary>
public static class NotifyMessage
{
    public static Notification Build(ActionContext context)
    {
        var what = FirstLine(context.Snapshot.Title ?? context.Window.MainWindowTitle);
        var actions = string.Join(" / ", context.Policy.Actions.Select(Describe));

        // Name the rule that fired. "Which app, which rule" is the question the reader actually has;
        // the internal reason string ("matched rule 'x'") is not written for them.
        var fired = context.Target.Rules.FirstOrDefault(r => r.Id == context.Verdict.RuleId);
        var rule = fired?.Name
                   ?? (string.IsNullOrWhiteSpace(context.Verdict.RuleId)
                       ? "（未知规则）"
                       : $"（规则 {context.Verdict.RuleId} 已不存在）");

        // A line the user wrote for themselves. Whatever we would have picked sounds like the
        // program nagging; their own sentence does not.
        var reminder = string.IsNullOrWhiteSpace(fired?.Reminder) ? "" : $"\n\n{fired!.Reminder!.Trim()}";

        var when = context.Now.ToString("HH:mm:ss");
        var which = context.HitNumber > 1 ? $"（本次运行第 {context.HitNumber} 次）" : "";
        var header = $"{when}{which}";

        // A policy that only reminds has not intercepted anything, and saying it did is a plain lie
        // to the reader. It also has no "about to" phase: there is nothing to defer.
        if (PolicyKind.IsReminderOnly(context.Policy))
        {
            return new Notification(
                "Sentinel · 提醒",
                $"{context.Target.DisplayName}\n规则：{rule}\n{what}\n{header}{reminder}",
                Acted: false);
        }

        if (context.Stage == ActionStage.Notify)
        {
            return new Notification(
                "Sentinel · 即将拦截",
                $"{context.Target.DisplayName}\n规则：{rule}\n{what}\n{header}\n\n{context.Policy.GraceSeconds} 秒后执行：{actions}{reminder}",
                Acted: false);
        }

        // What actually ran, not what the policy asked for. Posting WM_CLOSE reports success whether
        // or not the app closes, so reciting the configured list would claim more than happened.
        // The caller supplies this fully phrased, so it reads naturally either way.
        var did = context.ExecutedSummary ?? $"已执行：{actions}";

        return new Notification(
            "Sentinel · 已拦截",
            $"{context.Target.DisplayName}\n规则：{rule}\n{what}\n{header}\n\n{did}{reminder}",
            Acted: true);
    }

    public static string Describe(ActionType action) => action switch
    {
        ActionType.Notify => "通知",
        ActionType.Minimize => "最小化窗口",
        ActionType.Close => "关闭窗口",
        ActionType.Kill => "杀掉进程",
        ActionType.Log => "记录日志",
        _ => action.ToString(),
    };

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var breakAt = trimmed.IndexOfAny(new[] { '\r', '\n' });
        if (breakAt < 0)
            return trimmed.Length > 80 ? trimmed[..80] + "…" : trimmed;

        var line = trimmed[..breakAt];
        return line.Length > 80 ? line[..80] + "…" : line;
    }
}

/// <summary><paramref name="Acted"/> false = warning that something is about to happen; true = it happened.</summary>
public sealed record Notification(string Title, string Body, bool Acted);
