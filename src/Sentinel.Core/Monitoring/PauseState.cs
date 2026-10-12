using System.Text.Json;
using Sentinel.Core.Models;

namespace Sentinel.Core.Monitoring;

/// <summary>What the Service sends back when a pause is granted.</summary>
public sealed record PauseReply(string Until, string NextAvailableAt, int Minutes);

/// <summary>
/// When each target was last paused.
///
/// <para>
/// Deliberately not part of the config file. The pause is the one weakening that does not need a
/// token, so if its bookkeeping lived in the config, editing the config would be a way of granting
/// yourself an unlimited supply of them — the record would sit next to the thing it is supposed to
/// be recording. It lives in the install directory instead: readable by the user, writable only by
/// the Service.
/// </para>
/// <para>
/// One field per target, because everything else follows from it: the pause runs until that moment
/// plus the configured length, and the next one is allowed that moment plus the configured gap.
/// Keeping a second field for the end would only create a way for the two to disagree.
/// </para>
/// </summary>
public sealed class PauseState
{
    public const string FileName = ".paused.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Target id to the moment its last pause began, ISO 8601.</summary>
    public Dictionary<string, string> Last { get; set; } = new();

    public static string PathFor(string configPath) =>
        Path.Combine(Path.GetDirectoryName(configPath) ?? ".", FileName);

    public static PauseState Load(string configPath)
    {
        try
        {
            var path = PathFor(configPath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PauseState>(File.ReadAllText(path), Options) ?? new PauseState()
                : new PauseState();
        }
        catch
        {
            // Unreadable reads as "never paused", which fails toward enforcing.
            return new PauseState();
        }
    }

    public void Save(string configPath)
    {
        var path = PathFor(configPath);

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // A pause that could not be written down is not one. Failing loudly here would let a
            // full disk turn into an unlocked monitor.
            Last.Clear();
        }
    }

    public bool IsPaused(string targetId, PauseOptions options, DateTimeOffset now) =>
        PausedUntil(targetId, options, now) is not null;

    /// <summary>When the pause in progress ends, or null when there is not one.</summary>
    public DateTimeOffset? PausedUntil(string targetId, PauseOptions options, DateTimeOffset now) =>
        LastPaused(targetId) is { } last && now < last.AddMinutes(options.Minutes)
            ? last.AddMinutes(options.Minutes)
            : null;

    /// <summary>When this target may next be paused, or null when it never has been.</summary>
    public DateTimeOffset? AvailableAt(string targetId, PauseOptions options) =>
        LastPaused(targetId)?.AddDays(options.EveryDays);

    public bool CanPause(string targetId, PauseOptions options, DateTimeOffset now) =>
        AvailableAt(targetId, options) is not { } available || now >= available;

    public void Record(string targetId, DateTimeOffset now) => Last[targetId] = now.ToString("O");

    private DateTimeOffset? LastPaused(string targetId) =>
        Last.TryGetValue(targetId, out var value) && DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;
}
