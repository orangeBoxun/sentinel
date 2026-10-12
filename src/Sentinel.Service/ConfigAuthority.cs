using Sentinel.Core.Config;
using Sentinel.Core.Ipc;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;
using Sentinel.Core.Unlock;

namespace Sentinel.Service;

/// <summary>
/// Owns the config file. The Service runs as SYSTEM, which is what lets the file live somewhere a
/// standard user cannot write — the Agents only read it, and ask here to change it.
///
/// <para>
/// Because every route to the config comes through this class, the unlock check only has to exist
/// here: the named pipe, the tray UI and a hand-edited file all end up in <see cref="TrySave"/> or
/// <see cref="ConfigAuthority"/>'s constructor. Putting the check anywhere else would mean three
/// places to keep in agreement.
/// </para>
/// </summary>
public sealed class ConfigAuthority
{
    private readonly object _gate = new();
    private UnlockAuthority? _unlock;
    private string? _unlockSource;
    private AppConfig _config;
    private int _version = 1;

    public ConfigAuthority()
    {
        Path = ConfigPaths.Resolve(Environment.GetCommandLineArgs());

        _unlock = UnlockAuthority.TryLoad(Path, out var status);
        UnlockStatus = status;
        _unlockSource = ReadPublicKey(Path);

        _config = LoadHonouringSeal(Path, _unlock, out var refusal);
        StartupRefusal = refusal;

        // What we are now running with is, by definition, the last accepted state.
        UnlockSeal.Write(Path, _config);
    }

    public string Path { get; }

    /// <summary>Why unlock protection is off, or null when it is on. Shown in the tray UI.</summary>
    public string? UnlockStatus { get; private set; }

    public bool UnlockConfigured
    {
        get
        {
            lock (_gate)
                return _unlock is not null;
        }
    }

    /// <summary>
    /// Picks up an unlock public key that appeared after the Service started.
    ///
    /// <para>
    /// This exists because of an awkward ordering. Turning the protection on means putting a file in
    /// the install directory, which needs administrator rights — and restarting the Service to make
    /// it read that file is exactly what the protection is built to make expensive. Waiting for a
    /// restart would have made setting it up the one step the tool fights you on.
    /// </para>
    /// <para>
    /// A key that disappears is not a key that stops mattering: the loaded one is kept. Otherwise
    /// deleting the file would be a quieter way of switching the protection off than the route this
    /// whole mechanism exists to make noisy.
    /// </para>
    /// </summary>
    public void RefreshUnlockAuthority()
    {
        var pem = ReadPublicKey(Path);
        if (pem is null)
            return;   // keep whatever is already loaded

        lock (_gate)
        {
            if (pem == _unlockSource)
                return;
        }

        var loaded = UnlockAuthority.TryLoad(Path, out _);
        if (loaded is null)
            return;

        lock (_gate)
        {
            _unlock = loaded;
            _unlockSource = pem;
            UnlockStatus = null;

            // Bumping this is what makes a running Agent notice. Its heartbeat compares versions and
            // a change is its cue to re-read — without it the status line would go on saying "not
            // configured" while the Service was already refusing saves, which is the worst of both:
            // a lie on screen and a wall behind it.
            _version++;
        }
    }

    private static string? ReadPublicKey(string configPath)
    {
        try
        {
            var path = UnlockAuthority.PublicKeyPathFor(configPath);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Set when the config on disk was refused at startup for being a relaxation. Logged once.</summary>
    public string? StartupRefusal { get; }

    public IReadOnlyList<ConfigIssue> Validate(AppConfig config) => ConfigValidator.Validate(config);

    /// <summary>The config plus its version, ready to send to an Agent.</summary>
    public string Snapshot()
    {
        lock (_gate)
            return Ipc.Serialize(new ConfigEnvelope(_version, _config, UnlockConfigured, UnlockStatus));
    }

    public string SnapshotVersion()
    {
        lock (_gate)
            return _version.ToString();
    }

    /// <summary>
    /// Replaces the config. Refuses anything the validator calls an error: a config that only fails
    /// at three in the morning is worse than a refused save.
    ///
    /// <para>
    /// <b>但只拦"这次新引入的"错误。</b>拦全部的话会锁死：存下来的那份只要有一处不对，之后
    /// **每一次保存都会被拒** —— 包括想把那处改掉的那一次。真实发生过：一个没填完的服务商让
    /// 用户加不了规则、也删不掉它自己（删除同样要保存，而保存时它还在）。
    /// 校验的用处是"别把配置改坏"，不是"把已经坏了的配置焊死"。
    /// </para>
    /// <para>
    /// <paramref name="unlockRequest"/> comes back non-null on exactly one failure: the edit weakens
    /// enforcement and there was no usable token. It is the string the user carries to the other
    /// machine, built from the config they asked for rather than the one being refused, so signing it
    /// authorizes the edit they actually made.
    /// </para>
    /// </summary>
    public bool TrySave(string json, string? token, out string? error, out string? unlockRequest)
    {
        error = null;
        unlockRequest = null;

        var incoming = Ipc.Deserialize<AppConfig>(json);
        if (incoming is null)
        {
            error = "配置内容无法解析";
            return false;
        }

        // Snapshot both under the lock: the authority can be swapped in while this runs, by the
        // same loop that puts a newly-placed public key into service.
        UnlockAuthority? unlock;
        AppConfig previous;

        lock (_gate)
        {
            unlock = _unlock;
            previous = _config;
        }

        // 存下来的那份**本来就有**的错误不算这次的问题 —— 否则人没法修它。
        var alreadyBroken = ConfigValidator.Validate(previous)
            .Where(i => i.Severity == IssueSeverity.Error)
            .Select(i => i.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var introduced = ConfigValidator.Validate(incoming)
            .Where(i => i.Severity == IssueSeverity.Error)
            .Where(i => !alreadyBroken.Contains(i.ToString()))
            .ToList();

        if (introduced.Count > 0)
        {
            error = string.Join("\n", introduced.Select(e => e.ToString()));
            return false;
        }

        UnlockLedger? ledger = null;
        string? spend = null;

        if (unlock is not null)
        {
            var verdict = unlock.Authorize(previous, incoming, token, out var reason, out var nonce);

            if (verdict == UnlockVerdict.Refused)
            {
                error = reason;
                unlockRequest = unlock.CreateRequest(incoming);
                return false;
            }

            if (verdict == UnlockVerdict.Granted && nonce is not null)
            {
                ledger = UnlockLedger.Load(Path);

                // The same change can be asked for again later — undoing it is free, because
                // tightening always is — and without this the one token would cover every repeat.
                if (ledger.Has(nonce))
                {
                    error = "这个令牌已经用过了 —— 一个令牌只生效一次。请重新获取解锁码。";
                    unlockRequest = unlock.CreateRequest(incoming);
                    return false;
                }

                spend = nonce;
            }
        }

        try
        {
            ConfigStore.Save(incoming, Path);
        }
        catch (Exception ex)
        {
            error = $"无法写入 {Path}：{ex.Message}";
            return false;
        }

        // Spent only once the change is actually stored: a token burned by a failed write is a token
        // the user would have to go and fetch again for no reason at all.
        if (ledger is not null && spend is not null)
        {
            ledger.Mark(spend, DateTimeOffset.Now);
            ledger.Save(Path);
        }

        UnlockSeal.Write(Path, incoming);

        lock (_gate)
        {
            _config = incoming;
            _version++;
        }

        return true;
    }

    /// <summary>
    /// Spends one pause on a rule.
    ///
    /// <para>
    /// The Service owns this rather than the Agent, and for the plainest reason: the Agent runs as
    /// the same user who wants the pause, so counting there would be the interested party counting
    /// its own spending. The budget is in the config, where raising it needs a token like any other
    /// loosening.
    /// </para>
    /// <para>
    /// Keyed by rule, not by target. A target is an application; its rules are the individual things
    /// being kept away. Pausing the app would set all of them aside at once, and spend one click doing
    /// it — which is the opposite of a bounded exception.
    /// </para>
    /// </summary>
    public bool TryPause(string ruleId, out string? reply, out string? error)
    {
        reply = null;
        error = null;

        AppConfig config;
        lock (_gate)
            config = _config;

        Rule? rule = null;
        foreach (var target in config.Targets)
        {
            rule = target.Rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule is not null)
                break;
        }

        if (rule is null)
        {
            error = "没有这条规则。";
            return false;
        }

        var options = rule.Pause;

        if (!options.Enabled)
        {
            error = $"「{rule.Name}」没有开启暂停。";
            return false;
        }

        var now = DateTimeOffset.Now;
        var state = PauseState.Load(Path);

        if (!state.CanPause(ruleId, options, now))
        {
            var next = state.AvailableAt(ruleId, options)!.Value;
            error = $"「{rule.Name}」下一次暂停要等到 {next:MM-dd HH:mm} —— 每 {options.EveryDays} 天一次。";
            return false;
        }

        state.Record(ruleId, now);
        state.Save(Path);

        reply = Ipc.Serialize(new PauseReply(
            now.AddMinutes(options.Minutes).ToString("O"),
            now.AddDays(options.EveryDays).ToString("O"),
            options.Minutes));

        return true;
    }

    /// <summary>
    /// Turns unlock protection off, with a token.
    ///
    /// <para>
    /// This exists because every other way of loosening is reached by making an edit first, and so
    /// there was no way to say "I mean to stop, deliberately" — only the accident of an edit being
    /// refused. Taking the rule itself down is a different intention and deserves its own door.
    /// </para>
    /// <para>
    /// The key file is deleted rather than merely forgotten: left in place, the Service would pick it
    /// up again on its next look and the protection would come back by itself. Putting a key back is
    /// how it is turned on again — the same file, from the same place.
    /// </para>
    /// </summary>
    public bool TryReleaseProtection(string? token, out string? error, out string? request, out string? detail)
    {
        error = null;
        request = null;
        detail = null;

        UnlockAuthority? unlock;
        lock (_gate)
            unlock = _unlock;

        if (unlock is null)
        {
            error = "解锁保护本来就没有开启。";
            return false;
        }

        if (!unlock.ValidateDigest(UnlockCrypto.ReleaseDigest, token, out var reason, out var nonce, what: "关闭解锁保护"))
        {
            error = reason;
            request = unlock.CreateRequestFor(UnlockCrypto.ReleaseDigest);
            return false;
        }

        var ledger = UnlockLedger.Load(Path);

        if (nonce is not null && ledger.Has(nonce))
        {
            error = "这个令牌已经用过了 —— 一个令牌只生效一次。";
            request = unlock.CreateRequestFor(UnlockCrypto.ReleaseDigest);
            return false;
        }

        try
        {
            File.Delete(UnlockAuthority.PublicKeyPathFor(Path));
        }
        catch (Exception ex)
        {
            // Failing here would leave the protection on while the user believes they turned it off.
            error = $"删不掉公钥文件，保护没有关掉：{ex.Message}";
            return false;
        }

        if (nonce is not null)
        {
            ledger.Mark(nonce, DateTimeOffset.Now);
            ledger.Save(Path);
        }

        lock (_gate)
        {
            _unlock = null;
            _unlockSource = null;
            UnlockStatus = "已用令牌关闭 —— 想重新开启，把公钥放回配置目录即可。";
            _version++;
        }

        detail = "解锁保护已关闭。现在放宽限制不再需要令牌。";
        return true;
    }

    /// <summary>
    /// Turns unlock protection on, with a public key the user brought from the other device.
    ///
    /// <para>
    /// No token, because this is a tightening: it takes freedom away rather than handing any out.
    /// The check that matters here is the opposite one — <b>a key that is already loaded may not be
    /// replaced through this door</b>. Otherwise "enable" would be a way to swap in a key whose
    /// private half you hold, which undoes the whole mechanism in one step. Replacing means releasing
    /// first, and releasing needs a token.
    /// </para>
    /// <para>
    /// The Service does the write because the install directory is SYSTEM-only. The Agent runs as the
    /// user and cannot put a file there — which is exactly what makes the key's presence meaningful.
    /// </para>
    /// </summary>
    public bool TryEnableProtection(string? pastedPublicKey, out string? error, out string? detail)
    {
        error = null;
        detail = null;

        lock (_gate)
        {
            if (!UnlockAuthority.MayEnable(_unlock is not null, out error))
                return false;
        }

        if (!UnlockAuthority.TryPreparePublicKey(pastedPublicKey, out var pem, out error))
            return false;

        var path = UnlockAuthority.PublicKeyPathFor(Path);

        try
        {
            File.WriteAllText(path, pem);
        }
        catch (Exception ex)
        {
            error = $"写不进 {path}：{ex.Message}";
            return false;
        }

        var loaded = UnlockAuthority.TryLoad(Path, out var reason);
        if (loaded is null)
        {
            // Should not happen — we just wrote a key we validated. Undo it rather than leave a file
            // that switches the protection on in a way the Service cannot itself honour.
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort; the message below is the actionable part.
            }

            error = $"公钥写进去了却读不出来：{reason}";
            return false;
        }

        lock (_gate)
        {
            _unlock = loaded;
            _unlockSource = pem;
            UnlockStatus = null;

            // Bumping the version is what makes a running Agent redraw its status line.
            _version++;
        }

        detail = "解锁保护已开启。从现在起，放宽限制需要机器外签发的令牌；收紧永远不需要。";
        return true;
    }

    /// <summary>
    /// Loads the file, but keeps the sealed config instead when the file has been relaxed without a
    /// token — the case of the config being replaced while the service was down.
    ///
    /// Refusing and carrying on with the stricter rules is the point: coming up with enforcement
    /// silently switched off would be the one failure this whole mechanism exists to prevent. The
    /// file is left alone rather than rewritten; saying nothing more than "this is not in effect" is
    /// enough, and the next accepted save overwrites it anyway.
    /// </summary>
    private static AppConfig LoadHonouringSeal(string path, UnlockAuthority? unlock, out string? refusal)
    {
        refusal = null;
        var loaded = ConfigStore.LoadOrDefault(path);

        if (unlock is null)
            return loaded;

        var sealedConfig = UnlockSeal.Read(path);
        if (sealedConfig is null)
            return loaded;   // first run, or the seal was removed: nothing to compare against

        if (ConfigRelaxation.Classify(sealedConfig, loaded) != ChangeDirection.Relaxing)
            return loaded;

        refusal =
            $"{path} 里的配置比上次认可的更宽松，且没有解锁令牌 —— 已忽略，继续按上次认可的配置运行。" +
            "如果这是你自己改的，请从界面里修改以取得解锁码。";

        return sealedConfig;
    }
}
