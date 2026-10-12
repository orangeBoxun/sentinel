using System.Text.Json;

namespace Sentinel.Core.Ipc;

/// <summary>
/// The Agent/Service contract. The Service owns the config (it runs as SYSTEM and can write a file
/// standard users cannot), so the Agent reads and writes it through here rather than touching the
/// file itself.
/// </summary>
public static class Ipc
{
    /// <summary>
    /// Versioned: a stale Agent from an older build must not be served as if it matched.
    ///
    /// <para>
    /// Overridable through the environment so a test can run its own Service alongside the installed
    /// one. A pipe name is a single global slot: a second server fails to bind, and the test client
    /// then connects to the real Service without anything saying so — which is a quiet way to run a
    /// test against somebody's live configuration.
    /// </para>
    /// </summary>
    public static string PipeName =>
        Environment.GetEnvironmentVariable("SENTINEL_PIPE") is { Length: > 0 } overridden
            ? overridden
            : "Sentinel.Agent.v1";

    public const string Ping = "ping";
    public const string GetConfig = "get-config";
    public const string SaveConfig = "save-config";
    public const string RequestKill = "request-kill";

    /// <summary>
    /// 一条审计记录，交给服务去落盘。
    ///
    /// <para>
    /// 为什么不由 Agent 自己写：装完之后 <c>logs\</c> 对普通用户是可写的（Agent 以用户身份跑），
    /// 于是那份记录改得动 —— 而"约束自己"这件事里，凌晨一点会去改的正是这份记录。
    /// 交给服务写到它自己的目录里，那份普通用户只能读。
    /// </para>
    /// <para>
    /// 一边一份：本地那份照写（服务不在的时候不至于丢），服务那份是权威的。
    /// </para>
    /// </summary>
    public const string Audit = "audit";

    /// <summary>
    /// The Agent saying it is on its way out, so the Service can bring the next one up at once
    /// instead of waiting out its "has it died?" timeout. Without it, a clean exit leaves the
    /// session unmonitored for the length of that timeout.
    /// </summary>
    public const string AgentLeaving = "agent-leaving";

    /// <summary>
    /// Spends one pause. The Service owns the budget: it is the only party that can be trusted to
    /// count, since the Agent runs as the same user who would be doing the pausing.
    ///
    /// <para>
    /// The payload is a rule id. Pauses are per rule rather than per target: the urge is for one
    /// specific thing, and pausing a whole target would set every rule on that app aside at once.
    /// </para>
    /// </summary>
    public const string PauseRule = "pause-rule";

    /// <summary>
    /// Turns unlock protection on. The payload is the public key; no token, because this is the
    /// tightening direction.
    ///
    /// <para>
    /// This exists so that switching the protection on is something you do in the window rather than
    /// by dropping a file into the install directory by hand — which needed administrator rights and a
    /// command line, and was the one step of setting the tool up that the tool itself did not help
    /// with.
    /// </para>
    /// </summary>
    public const string EnableProtection = "enable-protection";

    /// <summary>
    /// Turns unlock protection off. Needs a token like anything else that loosens — the difference
    /// is that this one loosens everything at once, which is why it is its own deliberate act rather
    /// than something that falls out of an edit.
    /// </summary>
    public const string ReleaseProtection = "release-protection";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,

        // The config file writes enums as names — it is meant to be read and edited by hand — while
        // a config travelling over the pipe used to write them as numbers, so the same config had
        // two shapes depending on which way it went. Taking one that came off disk and sending it
        // over the pipe failed with "配置内容无法解析" and nothing to say why. Accepting both costs
        // nothing and removes the trap; the reader takes numbers either way.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch
        {
            return default;
        }
    }
}

/// <summary>
/// <paramref name="Token"/> rides along with a save rather than being its own command: the token is
/// meaningless on its own, and pairing them means a save and its authorization cannot arrive out of
/// step or be applied to different configs.
/// </summary>
public sealed record IpcRequest(string Command, string? Payload = null, string? Token = null);

/// <summary>
/// <paramref name="Reached"/> separates "the Service said no" from "there was no Service to ask".
///
/// The difference decides whether the Agent may fall back to writing the config file itself. Treating
/// a refusal as unreachability would hand a caller a way to disable the unlock check: cause one
/// refused save, and the next one goes to the file and past every rule the Service enforces.
/// </summary>
public sealed record IpcResponse(
    bool Ok,
    string? Payload = null,
    string? Error = null,
    string? UnlockRequest = null,
    bool Reached = true)
{
    public static IpcResponse Success(string? payload = null) => new(true, payload);

    public static IpcResponse Failure(string error, string? unlockRequest = null) =>
        new(false, null, error, unlockRequest);

    public static IpcResponse Unreachable(string error) => new(false, null, error, null, false);
}

/// <summary>
/// The config plus a version that increments on every save. Agents compare versions instead of the
/// config itself: reloading on a version change is cheap, and it avoids a deep comparison of a graph.
///
/// <para>
/// The unlock fields ride along because the Agent has to explain the rule before it is hit: a user
/// who only learns that relaxing needs an off-machine signature at the moment a save is refused has
/// been told too late to plan for it.
/// </para>
/// </summary>
public sealed record ConfigEnvelope(
    int Version,
    Sentinel.Core.Models.AppConfig Config,
    bool UnlockConfigured = false,
    string? UnlockStatus = null);
