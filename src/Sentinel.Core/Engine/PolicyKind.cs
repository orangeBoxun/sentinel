using Sentinel.Core.Models;

namespace Sentinel.Core.Engine;

public static class PolicyKind
{
    /// <summary>
    /// A policy that only reminds — it takes no action that takes the offending window away from the
    /// user.
    ///
    /// The distinction matters because such a policy changes nothing about the window, so nothing
    /// about the window will ever change on its own to bring it back to the monitor's attention.
    /// It therefore needs the foreground triggers (returning to the window, and the repeat interval)
    /// to keep saying anything. A policy that minimizes or closes needs neither: the window going
    /// away, and coming back, is itself the signal.
    /// </summary>
    public static bool IsReminderOnly(Policy policy) =>
        policy.Actions.Contains(ActionType.Notify)
        && !policy.Actions.Any(a => a is ActionType.Minimize or ActionType.Close or ActionType.Kill);
}
