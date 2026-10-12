using System.Text.Json;
using Sentinel.Core.Models;

namespace Sentinel.Core.Config;

/// <summary>
/// Works out which targets a config edit actually affects.
///
/// The monitor caches each target's last-read content so an unchanged page is not re-judged. When
/// the config changes, throwing that cache away wholesale means editing one rule makes every OTHER
/// rule fire again — visible as a burst of notifications for pages nobody touched. Comparing
/// fingerprints narrows the invalidation to what really changed.
/// </summary>
public static class ConfigDiff
{
    public static IReadOnlySet<string> ChangedTargetIds(AppConfig previous, AppConfig current)
    {
        var before = previous.Targets.ToDictionary(t => t.Id, t => Fingerprint(previous, t));
        var after = current.Targets.ToDictionary(t => t.Id, t => Fingerprint(current, t));

        var changed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (id, fingerprint) in after)
        {
            if (!before.TryGetValue(id, out var old) || old != fingerprint)
                changed.Add(id);
        }

        foreach (var id in before.Keys)
        {
            if (!after.ContainsKey(id))
                changed.Add(id);
        }

        return changed;
    }

    /// <summary>
    /// A target's behaviour depends on its own settings, the policies its rules point at, and the
    /// schedules those rules run under — so all three go into the fingerprint. Safe mode is included
    /// on purpose: arming the monitor should take effect on what is already on screen.
    /// </summary>
    private static string Fingerprint(AppConfig config, Target target)
    {
        var relevant = new
        {
            Target = target,
            Policies = config.Policies
                .Where(p => target.Rules.Any(r => r.PolicyId == p.Id))
                .OrderBy(p => p.Id, StringComparer.Ordinal),
            Schedules = config.Schedules
                .Where(s => target.Rules.Any(r => r.ScheduleId == s.Id))
                .OrderBy(s => s.Id, StringComparer.Ordinal),
            config.Ui.DryRun,
        };

        return JsonSerializer.Serialize(relevant);
    }
}
