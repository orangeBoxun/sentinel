using System.Text.RegularExpressions;
using Sentinel.Core.Models;

namespace Sentinel.Core.Engine;

/// <summary>Decides whether a discovered window belongs to a configured target.</summary>
public static class TargetMatcher
{
    public static bool Matches(Target target, WindowRef window)
    {
        if (!target.Enabled)
            return false;

        if (target.ProcessNames.Count > 0)
        {
            var processName = StripExtension(window.ProcessName);
            var hit = target.ProcessNames.Any(p =>
                string.Equals(StripExtension(p), processName, StringComparison.OrdinalIgnoreCase));

            if (!hit)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(target.WindowTitlePattern))
        {
            var title = window.MainWindowTitle ?? "";
            if (!Regex.IsMatch(title, target.WindowTitlePattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                return false;
        }

        return true;
    }

    private static string StripExtension(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}
