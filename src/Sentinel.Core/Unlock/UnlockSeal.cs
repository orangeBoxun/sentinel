using System.Text.Json;
using System.Text.Json.Serialization;
using Sentinel.Core.Models;

namespace Sentinel.Core.Unlock;

/// <summary>
/// The last configuration the service agreed to, kept on disk so the check survives a restart.
///
/// <para>
/// The comparison that matters lives in memory (<c>Classify(what we had, what we are being asked
/// for)</c>), and that is enough for a save — the service already knows what it is running with.
/// It is not enough at startup: the running config came from the file, so the file cannot also be
/// the only thing saying what it used to be. Without a separate record, replacing the file while
/// the service is down would look exactly like the file always having said that.
/// </para>
/// <para>
/// This is friction, not a wall, and it is worth being clear about which: whoever can write the
/// config can write this too. It stops the file being swapped by anything that cannot also reach
/// this, and it turns the rest into a two-file edit that has to be got right.
/// </para>
/// </summary>
public sealed class UnlockSeal
{
    public const string FileName = "unlock-seal.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Version { get; set; } = 1;

    public string SealedAt { get; set; } = "";

    /// <summary>The whole config, not a hash of it — the startup check needs something to compare against.</summary>
    public AppConfig? Config { get; set; }

    public static string PathFor(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        return string.IsNullOrEmpty(directory) ? FileName : Path.Combine(directory, FileName);
    }

    /// <summary>Null when there is no seal yet, or it cannot be read — both mean "nothing to compare against".</summary>
    public static AppConfig? Read(string configPath)
    {
        var path = PathFor(configPath);

        try
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<UnlockSeal>(File.ReadAllText(path), Options)?.Config;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string configPath, AppConfig config, string? sealedAt = null)
    {
        var path = PathFor(configPath);

        try
        {
            var seal = new UnlockSeal
            {
                SealedAt = sealedAt ?? DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Config = config,
            };

            File.WriteAllText(path, JsonSerializer.Serialize(seal, Options));
        }
        catch
        {
            // A seal we could not write costs a false "relaxation" on the next start, which asks for a
            // token that a lookup would have granted. Annoying, never dangerous, and never a reason to
            // fail the save that has already been written to the real file.
        }
    }
}
