using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sentinel.Core.Models;

namespace Sentinel.Core.Config;

/// <summary>Loads/saves <see cref="AppConfig"/> with a notify-only fallback on missing/corrupt input.</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Without this, non-ASCII is written as \uXXXX escapes — unreadable in a file people edit by hand.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig LoadOrDefault(string path)
    {
        // A monitor must keep running on a corrupt file, so failure here degrades to a safe default.
        return TryLoad(path, out var config, out _) ? config : Default();
    }

    /// <summary>
    /// Strict load: reports why it failed instead of silently substituting a default. Import and
    /// validation use this — degrading to an empty config there would quietly discard the user's file.
    /// </summary>
    /// <summary>
    /// 老配置升级：以前只有一个 <see cref="AppConfig.ActiveModelId"/>，现在判断链才是权威。
    ///
    /// <para>
    /// 把它搬进链里、并标成"稳定提供服务" —— 那正是它升级前的事实：那一个连不上时，原来的
    /// 行为就是反复提醒。不搬的话，升级会**悄悄**停掉模型监督（链变空了），而界面上看不出区别。
    /// </para>
    /// <para>
    /// 只搬一次：之后链空 = 用户自己清空的（那件事要令牌，而且会把 ActiveModelId 一起清掉），
    /// 不会再把旧值扶回来。
    /// </para>
    /// </summary>
    private static void Migrate(AppConfig config)
    {
        if (config.ModelChain is { Count: > 0 } || string.IsNullOrWhiteSpace(config.ActiveModelId))
            return;

        var provider = config.Models.FirstOrDefault(m => m.Id == config.ActiveModelId);

        if (provider is null)
            return;

        config.ModelChain = new List<string> { provider.Id };
        provider.Stable = true;
    }

    public static bool TryLoad(string path, out AppConfig config, out string? error)
    {
        config = new AppConfig();
        error = null;

        try
        {
            if (!File.Exists(path))
            {
                error = $"file not found: {path}";
                return false;
            }

            var parsed = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
            if (parsed is null)
            {
                error = "parsed to null";
                return false;
            }

            config = parsed;
            Migrate(config);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The config as stable bytes, for hashing.
    ///
    /// "Stable" is the whole requirement: the unlock token signs a digest of this, and the same
    /// config has to produce the same bytes today and after a restart. Serializing a typed model
    /// with fixed options does that — property order is declaration order, and the enum converter
    /// never depends on the current culture.
    /// </summary>
    public static byte[] CanonicalBytes(AppConfig config) =>
        JsonSerializer.SerializeToUtf8Bytes(config, Options);

    public static void Save(AppConfig config, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, Options);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// An independent copy.
    ///
    /// The monitor keeps one so that edits the UI makes to the live config object do not change the
    /// monitor's baseline. With a shared instance, "what has changed since last time?" compares an
    /// object against itself and always answers "nothing" — so no edit ever takes effect.
    /// </summary>
    public static AppConfig Clone(AppConfig config) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, Options), Options) ?? new AppConfig();

    /// <summary>A safe default that only notifies and never blocks, until the user configures rules.</summary>
    public static AppConfig Default()
    {
        var config = new AppConfig();
        config.Policies.Add(new Policy
        {
            Id = "notify-only",
            Name = "仅通知",
            GraceSeconds = 0,
            CooldownSeconds = 30,
            DebounceCount = 1,
        });
        config.Policies[0].Actions.Add(ActionType.Notify);
        return config;
    }
}
