using System.Text.RegularExpressions;
using Sentinel.Core.Models;

namespace Sentinel.Core.Engine;

/// <summary>Evaluates a <see cref="MatchSpec"/> against a snapshot's fields.</summary>
public static class MatchEvaluator
{
    public static bool Matches(MatchSpec spec, ContentSnapshot snapshot)
    {
        var fields = spec.Fields.Count > 0
            ? spec.Fields
            : new List<MatchField> { MatchField.Url, MatchField.Title };

        foreach (var field in fields)
        {
            var text = field switch
            {
                MatchField.Url => snapshot.Url,
                MatchField.Title => snapshot.Title,
                MatchField.Body => snapshot.BodyText,
                _ => null,
            };

            if (string.IsNullOrEmpty(text))
                continue;

            foreach (var pattern in spec.Patterns)
            {
                if (!string.IsNullOrEmpty(pattern) && MatchesPattern(spec.Type, pattern, text, spec.CaseSensitive))
                    return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(MatchKind type, string pattern, string text, bool caseSensitive)
    {
        switch (type)
        {
            case MatchKind.Keyword:
            case MatchKind.Whitelist:
            {
                var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                return text.Contains(pattern, comparison);
            }
            case MatchKind.Regex:
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                // Bound regex evaluation so a bad user pattern can't hang the monitor loop.
                return Regex.IsMatch(text, pattern, options, TimeSpan.FromSeconds(1));
            }
            default:
                return false;
        }
    }
}

/// <summary>Evaluates whether a schedule is currently active.</summary>
public static class ScheduleEvaluator
{
    public static bool IsActive(Schedule schedule, DateTimeOffset now)
    {
        if (schedule.DaysOfWeek.Count > 0 && !schedule.DaysOfWeek.Contains(now.DayOfWeek))
            return false;

        if (schedule.TimeWindows.Count == 0)
            return true; // day-only restriction, no time bound

        foreach (var window in schedule.TimeWindows)
        {
            if (IsWithin(now, window))
                return true;
        }

        return false;
    }

    private static bool IsWithin(DateTimeOffset now, TimeWindow window)
    {
        var start = ParseTime(window.Start);
        var end = ParseTime(window.End);
        var t = now.TimeOfDay;

        if (start <= end)
            return t >= start && t <= end;

        // Overnight window, e.g. 22:00 - 06:00.
        return t >= start || t <= end;
    }

    private static TimeSpan ParseTime(string hhmm)
    {
        if (TimeSpan.TryParse(hhmm, out var ts))
            return ts;

        var parts = hhmm.Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
            return new TimeSpan(h, m, 0);

        return TimeSpan.Zero;
    }
}
