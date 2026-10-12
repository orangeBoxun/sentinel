using Sentinel.Core.Models;

namespace Sentinel.Core.Engine;

/// <summary>
/// <paramref name="Episode"/> is the ordinal of the hit within this run (1 = first). Both stages of
/// one hit carry the same number, so a notification can say whether it is the second phase of what
/// just happened or a genuinely new occurrence.
/// </summary>
public sealed record ActionDecision(ActionStage Stage, string? Reason, int Episode = 0)
{
    public static readonly ActionDecision None = new(ActionStage.None, null);

    public static ActionDecision Notify(string? reason = null, int episode = 0) =>
        new(ActionStage.Notify, reason, episode);

    public static ActionDecision Enforce(string? reason = null, int episode = 0) =>
        new(ActionStage.Enforce, reason, episode);
}

/// <summary>
/// Pure debounce / grace / cooldown state machine. Call <see cref="Decide"/> once per poll for a
/// given target/rule key; pass the policy to get the stage to execute this tick.
/// </summary>
public sealed class ActionController
{
    private sealed class State
    {
        public int ConsecutiveHits;
        public DateTimeOffset? EpisodeStart;
        public DateTimeOffset? LastEnforcedAt;
        public bool Notified;

        /// <summary>Hits handled for this key since the run started. Survives episode resets.</summary>
        public int Episodes;
    }

    private readonly Dictionary<string, State> _states = new();

    /// <summary>
    /// True while a grace window is running for this key. The monitor loop uses this to keep
    /// advancing the timer on ticks where the page content has not changed.
    /// </summary>
    public bool HasPendingEpisode(string key) =>
        _states.TryGetValue(key, out var state) && state.EpisodeStart is not null;

    /// <summary>
    /// True when a policy with a repeat interval is due to run again for content that is still
    /// offending. The caller clears the key so the next cycle starts without a stale cooldown.
    /// </summary>
    public bool IsRepeatDue(string key, Policy policy, DateTimeOffset now)
    {
        if (policy.RepeatSeconds <= 0)
            return false;

        if (!_states.TryGetValue(key, out var state) || state.LastEnforcedAt is null)
            return false;

        return now - state.LastEnforcedAt >= TimeSpan.FromSeconds(policy.RepeatSeconds);
    }

    /// <summary>
    /// Forgets everything about a key, cooldown included. Used when the user reopens a window we had
    /// already dealt with: that is a new violation, not a repeat of the old one, so it should not
    /// have to wait out the previous action's cooldown.
    /// </summary>
    public void Clear(string key) => _states.Remove(key);

    /// <summary>
    /// Forgets every key, cooldowns included. Used when the operating mode changes: a safe-mode run
    /// records simulated enforcements, and their cooldowns must not carry over and delay the first
    /// real action after the monitor is armed.
    /// </summary>
    public void ClearAll() => _states.Clear();

    /// <summary>Tracked keys. Diagnostic; lets a test prove the episode map stays bounded.</summary>
    public int PendingEpisodeCount => _states.Count;

    /// <summary>
    /// Drops state for keys that no longer exist (window closed, target removed). Without this the
    /// map grows for the process lifetime, since a key is only removed when its content stops
    /// matching — which never happens for a window that simply disappeared.
    /// </summary>
    public void RetainOnly(IReadOnlySet<string> liveKeys)
    {
        // Deliberately no count-based shortcut: equal counts with different keys is precisely the
        // case that needs pruning (a window replaced by another with a new handle).
        foreach (var key in _states.Keys.Where(k => !liveKeys.Contains(k)).ToList())
            _states.Remove(key);
    }

    public ActionDecision Decide(string key, bool isBlocked, Policy policy, DateTimeOffset now)
    {
        var debounce = Math.Max(1, policy.DebounceCount);

        // A policy that only reminds has nothing to defer — the notification IS the action. Honouring
        // a grace period there would just send the same reminder twice.
        var grace = PolicyKind.IsReminderOnly(policy)
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(0, policy.GraceSeconds));

        var cooldown = TimeSpan.FromSeconds(Math.Max(0, policy.CooldownSeconds));

        // Content is fine again: end the episode so a later hit starts fresh. The hit count is kept —
        // it answers "how many times has this been stopped", and resetting it every time the user
        // navigates away and back would make the number meaningless.
        if (!isBlocked)
        {
            if (_states.TryGetValue(key, out var existing))
                ResetEpisode(existing);

            return ActionDecision.None;
        }

        if (!_states.TryGetValue(key, out var state))
        {
            state = new State();
            _states[key] = state;
        }

        state.ConsecutiveHits++;

        // Debounce: require N consecutive blocked polls before reacting.
        if (state.ConsecutiveHits < debounce)
            return ActionDecision.None;

        // Cooldown gates the whole episode, not just the enforcement step. Checking it only when
        // enforcing would let a page whose content keeps changing re-notify and re-enforce on every
        // change, straight through the cooldown — exactly the action storm it exists to prevent.
        if (state.LastEnforcedAt is not null && now - state.LastEnforcedAt < cooldown)
            return ActionDecision.None;

        // No grace: enforce immediately.
        if (grace == TimeSpan.Zero)
        {
            state.Episodes++;
            state.LastEnforcedAt = now;
            var ordinal = state.Episodes;
            ResetEpisode(state);
            return ActionDecision.Enforce("no grace", ordinal);
        }

        // Grace: notify once at episode start, then wait out the grace window.
        if (state.EpisodeStart is null)
        {
            state.Episodes++;
            state.EpisodeStart = now;
            state.Notified = true;
            return ActionDecision.Notify("grace started", state.Episodes);
        }

        if (now - state.EpisodeStart < grace)
            return ActionDecision.None;

        state.LastEnforcedAt = now;
        var episode = state.Episodes;
        ResetEpisode(state);
        return ActionDecision.Enforce("grace elapsed", episode);
    }

    private static void ResetEpisode(State state)
    {
        state.EpisodeStart = null;
        state.Notified = false;
        state.ConsecutiveHits = 0;
    }
}
