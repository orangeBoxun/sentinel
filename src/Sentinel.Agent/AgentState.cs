using System.IO;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;

namespace Sentinel.Agent;

/// <summary>
/// Everything the UI and the monitor share: the loaded config, where it lives, and the background
/// loop.
///
/// The config has two possible sources. When the Service is running it owns the file — that is what
/// makes it writable only by SYSTEM and Administrators — and the Agent goes through the pipe. When
/// no Service is running, the Agent reads and writes the file directly, so it still works started by
/// hand.
/// </summary>
public sealed class AgentState : IDisposable
{
    public AgentState(string[] args)
    {
        ConfigPath = ConfigPaths.Resolve(args);
        OpenSettingsOnStart = args.Contains("--open", StringComparer.OrdinalIgnoreCase);
        Service = new ServiceClient();

        var fromService = Service.TryGetConfig(out _);

        if (fromService is not null)
        {
            Config = fromService;
        }
        else if (ConfigStore.TryLoad(ConfigPath, out var loaded, out var error))
        {
            Config = loaded;
        }
        else if (!File.Exists(ConfigPath))
        {
            Config = ConfigStore.Default();   // first run: nothing to load yet
        }
        else
        {
            // Load strictly. LoadOrDefault would silently substitute an empty config, leaving the
            // Agent running but monitoring nothing — indistinguishable from "the feature is broken".
            ConfigLoadError = error;
            Config = ConfigStore.Default();
        }

        LogDirectory = Environment.ExpandEnvironmentVariables(Config.Logging.LogDirectory);
        Monitor = new MonitorHost(this);
    }

    public string ConfigPath { get; }

    public string LogDirectory { get; }

    public ServiceClient Service { get; }

    /// <summary>Non-null when the config file exists but could not be parsed. Shown prominently.</summary>
    public string? ConfigLoadError { get; }

    /// <summary>Non-null when the last save was refused. Shown prominently — a lost edit must not be silent.</summary>
    public string? SaveError { get; private set; }

    /// <summary>
    /// Set when a save was refused for being a relaxation. This is the string the user carries to the
    /// machine holding the private key.
    /// </summary>
    public string? PendingUnlockRequest { get; private set; }

    /// <summary>
    /// The refused edit itself, held aside rather than left in <see cref="Config"/>.
    ///
    /// <para>
    /// It has to be kept somewhere: the token is signed over this exact content, so the retry has to
    /// send this back. It is kept <em>here</em> rather than left in the live config because the live
    /// config is what the screen draws — and a screen showing a rejected edit says a target is off
    /// when it is still on.
    /// </para>
    /// </summary>
    public AppConfig? PendingConfig { get; private set; }

    /// <summary>True when relaxing the rules needs an off-machine signature.</summary>
    public bool UnlockConfigured => Service.UnlockConfigured;

    public string? UnlockStatus => Service.UnlockStatus;

    /// <summary>Set by --open: start with the settings window showing instead of only a tray icon.</summary>
    public bool OpenSettingsOnStart { get; }

    public AppConfig Config { get; set; }

    public MonitorHost Monitor { get; }

    public bool DryRun => Config.Ui.DryRun;

    public void Start() => Monitor.Start();

    /// <summary>
    /// Tells the Service this Agent is on its way out. Without it the Service waits out its
    /// heartbeat timeout before starting a replacement, and that timeout is the length of an
    /// unmonitored gap anyone can open with one click on the tray menu.
    /// </summary>
    public void SayGoodbye() => Service.SayGoodbye(ServiceClient.CurrentSessionId);

    public void ReloadConfig()
    {
        var fromService = Service.TryGetConfig(out _);
        Config = fromService ?? ConfigStore.LoadOrDefault(ConfigPath);
    }

    /// <summary>
    /// Saves the current edit. False means it was refused — the reason is in <see cref="SaveError"/>,
    /// and the config in memory has been put back to what is actually stored.
    /// </summary>
    public bool SaveConfig() => Save(Config, null);

    /// <summary>
    /// Re-sends the configuration that was refused, with a token.
    ///
    /// <para>
    /// The refused one rather than the current one, because the token was issued for exactly that
    /// content: the digest covers the whole config, so anything else comes back as "this token is
    /// for another config".
    /// </para>
    /// </summary>
    public bool RetryPending(string token, out string? message)
    {
        if (PendingConfig is null)
        {
            message = "没有等着解锁的改动。";
            return false;
        }

        var saved = Save(PendingConfig, token);
        message = saved ? "那次改动已经生效。" : SaveError;
        return saved;
    }

    /// <summary>Throws away a refused edit and the request that came with it.</summary>
    public void CancelPendingUnlock()
    {
        PendingConfig = null;
        PendingUnlockRequest = null;
        PendingReleaseRequest = null;
    }

    // ------------------------------------------------------------------ 开启解锁保护

    /// <summary>
    /// Turns unlock protection on with a public key brought from the other device.
    ///
    /// <para>
    /// Nothing is loosened, so no token is involved. There is also deliberately no fallback to writing
    /// the file directly when the Service cannot be reached: the install directory is SYSTEM-only, so
    /// that write would fail anyway — and if it ever succeeded, the key would be somewhere the Service
    /// was not watching.
    /// </para>
    /// </summary>
    public bool EnableProtection(string publicKeyPem, out string message)
    {
        if (!Service.TryEnableProtection(publicKeyPem, out var detail))
        {
            message = detail ?? "开启失败 —— 服务没有接受这把公钥。";
            return false;
        }

        // The status line is drawn from what the Service reports about itself, so re-read it rather
        // than assuming what the new state is.
        ReloadConfig();

        message = detail ?? "解锁保护已开启。";
        return true;
    }

    // ------------------------------------------------------------------ 关闭解锁保护

    /// <summary>
    /// The code for turning unlock protection off, once the Service has been asked for it.
    /// </summary>
    public string? PendingReleaseRequest { get; private set; }

    /// <summary>
    /// Asks what it would take to turn the protection off. Nothing is turned off here — the Service
    /// answers with a request, and the answer has to come back signed.
    /// </summary>
    public bool RequestRelease(out string message)
    {
        if (Service.TryRelease(null, out var answer, out var request))
        {
            message = answer ?? "解锁保护本来就没有开启。";
            return false;
        }

        PendingReleaseRequest = request;
        message = answer ?? "";
        return request is not null;
    }

    public bool ConfirmRelease(string token, out string message)
    {
        if (Service.TryRelease(token, out var answer, out var request))
        {
            PendingReleaseRequest = null;

            // The status line is drawn from what the Service reports about itself, so re-read it
            // rather than assuming the new state.
            ReloadConfig();

            message = answer ?? "解锁保护已关闭。";
            return true;
        }

        if (request is not null)
            PendingReleaseRequest = request;

        message = answer ?? "";
        return false;
    }

    /// <summary>
    /// <para>
    /// The Service is always asked first, even when the heartbeat says it is down. Its answer decides
    /// things now — a refusal means the unlock rule was applied, and writing the file here instead
    /// would go straight past it. Only "there was nobody to ask" falls through to a direct write.
    /// </para>
    /// <para>
    /// A refusal also puts the config in memory back to what is stored. A screen still showing the
    /// edit that was just rejected is a screen telling the user something untrue — they would go on
    /// believing a target was off.
    /// </para>
    /// </summary>
    private bool Save(AppConfig candidate, string? token)
    {
        // 新的一次编辑意味着**上一次被拒的改动翻篇了** —— 把它丢掉。
        //
        // 留着它的坏处是具体的：那份被拒的配置是**冻住的**，万一之后拿着令牌点「用令牌重试」，
        // 发出去的就是它，会把这之后做的每一处改动一起盖回去 —— 用户看到的是"后面的配置改不了"。
        // 而它本来要说的话只有一句"这次放宽了，值不值得去签一个令牌"，说过了就够了。
        //
        // 只丢"被拒的这次改动"，不动「关闭解锁保护」那个请求：那是从按钮上明确发起的另一个意图。
        if (token is null)
        {
            PendingConfig = null;
            PendingUnlockRequest = null;
        }

        var saved = Service.TrySave(candidate, token, out var error, out var unlockRequest, out var reached);

        if (saved)
        {
            SaveError = null;
            CancelPendingUnlock();

            // Assigning rather than reloading: on a retry the candidate is the refused config, and
            // this is the moment it becomes the real one.
            Config = candidate;
            Monitor.RequestConfigRefresh();
            return true;
        }

        if (reached)
        {
            SaveError = error ?? "服务拒绝了这次保存";

            if (unlockRequest is not null)
            {
                PendingConfig = candidate;
                PendingUnlockRequest = unlockRequest;
            }
            else
            {
                CancelPendingUnlock();
            }

            ReloadConfig();
            return false;
        }

        // No Service anywhere: the Agent is the only thing that could write the file, so it does.
        try
        {
            ConfigStore.Save(candidate, ConfigPath);
        }
        catch (Exception ex)
        {
            SaveError = $"无法写入 {ConfigPath}：{ex.Message}";
            return false;
        }

        SaveError = null;
        CancelPendingUnlock();
        Config = candidate;
        Monitor.RequestConfigRefresh();
        return true;
    }

    /// <summary>
    /// 保险箱要的那份"本机秘密"。包成一个委托，是因为窗口在别人手上构造 VaultSession ——
    /// 让它按需来问服务，而不是把秘密在内存里传来传去。
    ///
    /// <para>
    /// 拿不到不是错误：那意味着服务不在，而这时候密码那道门本来就打不开（只能拿监护码）。
    /// </para>
    /// </summary>
    public byte[]? VaultSecret() => Service.TryGetVaultSecret(out var secret) ? secret : null;

    /// <summary>请服务抹掉封印标记（只有它删得掉那个文件）。</summary>
    public string? ClearSeal(string guardianCode) =>
        Service.TryClearSeal(guardianCode, out var error) ? null : error ?? "服务拒绝了。";

    public IReadOnlyList<ConfigIssue> Validate() => ConfigValidator.Validate(Config);

    // ------------------------------------------------------------------ 暂停

    /// <summary>Today's pause bookkeeping, read straight off disk. The Agent only ever reads it.</summary>
    public PauseState Pauses => PauseState.Load(ConfigPath);

    /// <summary>
    /// Spends one pause on a rule. Returns false with the reason in <paramref name="message"/>
    /// when the Service refuses — no budget left, or pauses not enabled on that rule.
    /// </summary>
    public bool PauseRule(string ruleId, out string message)
    {
        if (!Service.TryPause(ruleId, out var reply, out var error))
        {
            message = error ?? "服务拒绝了这次暂停";
            return false;
        }

        // The break starts now. Dropping what the monitor remembers matters: an episode already in
        // flight would otherwise survive the pause and enforce the instant it ended.
        Monitor.ForgetRule(ruleId);

        var until = DateTimeOffset.Parse(reply!.Until);

        message = reply.Left > 0
            ? $"已暂停到 {until:HH:mm} —— 「{reply.Scope}」还能停 {reply.Left} 次。"
            : $"已暂停到 {until:HH:mm} —— 「{reply.Scope}」的次数用完了，"
              + $"下一次要等到 {DateTimeOffset.Parse(reply.NextAvailableAt):MM-dd HH:mm}。";

        return true;
    }

    public void Dispose() => Monitor.Dispose();
}
