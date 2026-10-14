using System.Text.Json;

namespace Sentinel.Core.Vault;

/// <summary>Whether the vault is still allowed to be opened with the user's password.</summary>
public sealed record GuardStatus(bool Sealed, string? Reason)
{
    public static readonly GuardStatus Intact = new(false, null);

    public static GuardStatus SealedWith(string reason) => new(true, reason);
}

/// <summary>
/// The evidence that the protection is still standing, and the seal that says it was taken down.
///
/// <para>
/// Two files, both in the install directory — the one place a standard user cannot write:
/// </para>
/// <list type="bullet">
/// <item>A <b>seal</b>, written when something observed the protection being dismantled. Its
/// presence is final: it is what makes sealing a state rather than an event, so it does not matter
/// whether the vault is opened a minute later or a year later.</item>
/// <item>A <b>liveness record</b>, rewritten every few seconds by the Service while it runs. It is
/// read at exactly one moment: when the Service starts again, to see how long it was gone.</item>
/// </list>
/// <para>
/// <b>Why the second one is read only at startup.</b> An earlier version checked at every unlock
/// whether that record was still fresh, and sealed the vault when it was not. That reads a fact
/// about the present ("nobody is running") as evidence about the past ("someone stopped it"), and
/// the gap between the two is where ordinary life happens: a laptop on battery, a machine asleep
/// for the night, a scheduled task the system declined to run. All of those look identical to being
/// dismantled, and all of them would have sealed a vault that nobody had touched.
/// </para>
/// <para>
/// Read at startup instead, the same record answers a question it is actually able to answer: "I
/// was last alive at T, the machine has been up since before T, and it is now much later than T —
/// so I was stopped while the machine was running." A reboot cannot produce that, because the
/// machine was off in between and the record predates the boot. Sleep cannot produce it, because
/// nothing restarted and this check never runs. Only a stoppage can.
/// </para>
/// </summary>
public static class VaultGuard
{
    public const string SealFileName = ".vault-seal";

    public const string AliveFileName = ".vault-guard";

    /// <summary>看门狗每跑一次写一遍它。和上面那个刻意分成两个文件，理由见 <see cref="WatchdogAlivePathFor"/>。</summary>
    public const string WatchdogAliveFileName = ".watchdog-alive";

    /// <summary>Written by the installer and uninstaller while they are working.</summary>
    public const string MaintenanceFileName = ".maintenance";

    /// <summary>
    /// 本机秘密所在的文件。**放在 vault 目录之外** —— 所以"备份/拷贝 vault 目录"带不走它。
    /// </summary>
    public const string MachineSecretFileName = ".vault-machine";

    /// <summary>
    /// How long the Service has to be gone before its absence counts as a stoppage.
    ///
    /// A crash is recovered by the Service's own failure actions in about five seconds, and that
    /// must not seal anything. Ten minutes is long enough that nothing incidental reaches it, and
    /// short enough that stopping the Service and coming back to the vault does.
    /// </summary>
    public static readonly TimeSpan StoppageThreshold = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // These files carry reasons in words, and that reason is the only thing on this machine
        // that explains why the password stopped working. Escaped to \uXXXX it is still valid JSON
        // and completely unreadable to the person who opens it.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DirectoryFor(string configPath) =>
        Path.GetDirectoryName(configPath) ?? ".";

    public static string SealPathFor(string configPath) => Path.Combine(DirectoryFor(configPath), SealFileName);

    public static string AlivePathFor(string configPath) => Path.Combine(DirectoryFor(configPath), AliveFileName);

    public static string MachineSecretPathFor(string configPath) =>
        Path.Combine(DirectoryFor(configPath), MachineSecretFileName);

    /// <summary>
    /// 本机秘密：密码那道门绑着它（见 <see cref="VaultCrypto.DeriveBoundKey"/>）。
    ///
    /// <para>
    /// 它存在的唯一理由是"封印之后，旧的 vault 副本也打不开"：封印会把它一起销毁
    /// （见 <see cref="SealVault"/>），卸载同样。存的是 32 字节随机数，文件只给 SYSTEM 和管理员
    /// 读 —— 所以"顺手拷一份 vault 目录"不再够用，还得专门把它弄到手。
    /// </para>
    /// </summary>
    public static byte[]? LoadOrCreateMachineSecret(string configPath)
    {
        var path = MachineSecretPathFor(configPath);

        if (File.Exists(path))
        {
            try
            {
                var buffer = new byte[64];

                if (Convert.TryFromBase64String(File.ReadAllText(path).Trim(), buffer, out var written)
                    && written == VaultCrypto.KeySize)
                {
                    return buffer[..VaultCrypto.KeySize];
                }
            }
            catch
            {
                // 落到下面那句 return null。
            }

            // **读不出来就返回"没有"，绝不覆盖。** 这份秘密绑着用户那道门：悄悄换一份新的，
            // 等于把保险箱的密码门废掉 —— 而表现只是"密码不对"，没人会想到是这里。
            // 读不出来通常是权限问题（比如服务不是以 SYSTEM 在跑）。
            return null;
        }

        var secret = VaultCrypto.NewKey();
        File.WriteAllText(path, Convert.ToBase64String(secret));

        return secret;
    }

    /// <summary>销毁本机秘密。封印和卸载都会调它 —— 这一步之后，旧的 vault 副本就只认监护码了。</summary>
    public static bool DestroyMachineSecret(string configPath)
    {
        try
        {
            var path = MachineSecretPathFor(configPath);

            if (File.Exists(path))
                File.Delete(path);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string MaintenancePathFor(string configPath) => Path.Combine(DirectoryFor(configPath), MaintenanceFileName);

    /// <summary>
    /// 看门狗的存活记录：它每跑一次就写一遍。
    ///
    /// <para>
    /// <b>和上面那个 <c>.vault-guard</c> 是两回事，不能合并。</b>那一个是服务写的，专门用来发现
    /// "服务被停了"；如果让看门狗也去写它，服务停着的时候记录照样新鲜，那个检测就废了。
    /// 这个是反过来的：服务靠它发现**看门狗**被停了 —— 而看门狗自己发现不了自己不存在，
    /// 只有服务还在跑的时候才有人能注意到。
    /// </para>
    /// </summary>
    public static string WatchdogAlivePathFor(string configPath) =>
        Path.Combine(DirectoryFor(configPath), WatchdogAliveFileName);

    /// <summary>Where the encrypted files live. Under the install directory, but writable by the user — see <see cref="SealPathFor"/> for why the seal is not.</summary>
    public static string VaultRootFor(string configPath) => Path.Combine(DirectoryFor(configPath), "vault");

    public static string KeysPathFor(string configPath) => Path.Combine(VaultRootFor(configPath), "keys.json");

    /// <summary>
    /// False until a vault has actually been set up. Sealing one that does not exist would leave a
    /// marker that reads as "your vault was taken away" to someone who never had one.
    /// </summary>
    public static bool VaultExists(string configPath) => File.Exists(KeysPathFor(configPath));

    public static bool InMaintenance(string configPath) => File.Exists(MaintenancePathFor(configPath));

    /// <summary>
    /// How much is in the vault, from the outside. Used to say honestly what clearing it would
    /// destroy — the plaintext sizes leak either way, and it is the one place where being vague
    /// would be worse than being precise.
    /// </summary>
    public static (int Files, long Bytes) VaultFootprint(string configPath)
    {
        try
        {
            var blobs = Path.Combine(VaultRootFor(configPath), "blobs");
            if (!Directory.Exists(blobs))
                return (0, 0);

            var files = Directory.GetFiles(blobs);
            return (files.Length, files.Sum(file => new FileInfo(file).Length));
        }
        catch
        {
            return (0, 0);
        }
    }

    // ------------------------------------------------------------------ 封印

    /// <summary>Null when the vault is not sealed.</summary>
    public static string? ReadSeal(string configPath)
    {
        try
        {
            var path = SealPathFor(configPath);
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<SealRecord>(File.ReadAllText(path), Options)?.Reason ?? "防护被拆除";
        }
        catch
        {
            // An unreadable seal still means sealed. Failing open here would make corrupting this one
            // file a way to un-seal the vault.
            return "封印状态无法读取";
        }
    }

    /// <summary>
    /// Writes the seal marker. Idempotent, and only records the first reason: the one that actually
    /// happened is more useful than the dozen that noticed.
    /// </summary>
    public static bool Seal(string configPath, string reason, DateTimeOffset? at = null)
    {
        try
        {
            var path = SealPathFor(configPath);
            if (File.Exists(path))
                return false;

            var record = new SealRecord
            {
                Reason = reason,
                At = (at ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss"),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(record, Options));
            return true;
        }
        catch
        {
            // If the seal cannot be written, say so by returning false; the caller (SYSTEM) logs it.
            return false;
        }
    }

    /// <summary>
    /// Seals the vault: the marker beside the config, and the user's own key-wrapping inside the
    /// vault.
    ///
    /// <para>
    /// Both, not just the marker. The marker is the authority and is what makes the seal survive
    /// someone putting back an old copy of the vault — but a backup taken while sealed would
    /// otherwise carry the password door along with it, and land on whatever machine it is restored
    /// to. Doing both means "sealed" travels with the data as well as staying behind with the
    /// machine.
    /// </para>
    /// <para>
    /// Does nothing when there is no vault: a marker written before one existed would tell someone
    /// who never had a vault that theirs was taken away, and would block the first setup.
    /// </para>
    /// </summary>
    public static bool SealVault(string configPath, string reason, DateTimeOffset? at = null)
    {
        if (!VaultExists(configPath))
            return false;

        var sealedNow = Seal(configPath, reason, at);

        // **本机秘密跟着封印一起销毁。** 这才是"旧备份也打不开"的道理所在：
        // 光把用户那道门从钥匙文件里删掉不够 —— 旧的钥匙文件副本里那道门还在，
        // 而它解开需要的那份秘密现在没了。
        DestroyMachineSecret(configPath);

        try
        {
            var keys = VaultKeys.Load(KeysPathFor(configPath));
            if (keys is not null && keys.SealToGuardian(reason, at ?? DateTimeOffset.Now))
                keys.Save(KeysPathFor(configPath));
        }
        catch
        {
            // The marker is already down and is what decides; failing to also strip the wrapping
            // costs defence in depth, not the seal.
        }

        return sealedNow;
    }

    /// <summary>
    /// Removes the seal. Only legitimate after recovering with the guardian code, and only by
    /// someone who can write the install directory — which is a narrower group than the people who
    /// can write the vault.
    /// </summary>
    public static void ClearSeal(string configPath)
    {
        try
        {
            File.Delete(SealPathFor(configPath));
        }
        catch
        {
            // Nothing useful to do; a seal that will not come off is visible on the next read.
        }
    }

    // ------------------------------------------------------------------ 存活记录

    /// <summary>
    /// Written by the Service while it runs, and by nothing else. A second writer — the watchdog,
    /// say — would keep the record fresh while the Service was stopped, which is the one thing it
    /// exists to notice.
    /// </summary>
    public static void WriteAlive(string configPath, string detail, DateTimeOffset? at = null)
    {
        try
        {
            var record = new AliveRecord
            {
                At = (at ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O"),
                Detail = detail,
            };

            File.WriteAllText(AlivePathFor(configPath), JsonSerializer.Serialize(record, Options));
        }
        catch
        {
            // A missed write shows up as a longer gap on the next start, which errs toward sealing.
        }
    }

    public static DateTimeOffset? ReadAlive(string configPath)
    {
        try
        {
            var path = AlivePathFor(configPath);
            if (!File.Exists(path))
                return null;

            var record = JsonSerializer.Deserialize<AliveRecord>(File.ReadAllText(path), Options);
            return record is not null && DateTimeOffset.TryParse(record.At, out var at)
                ? at.ToUniversalTime()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The Service's own check, run once when it starts. Returns a reason to seal, or null when the
    /// gap — if there was one — is explained by the machine having been off.
    ///
    /// <para>
    /// <paramref name="uptime"/> is what makes a reboot innocent. A record written during the
    /// current boot session means the machine was up the whole time and the only thing missing was
    /// the Service; the same record predating the boot means the machine was off, which is not a
    /// dismantling.
    /// </para>
    /// </summary>
    public static string? DetectStoppage(string configPath, DateTimeOffset now, TimeSpan uptime)
    {
        var lastAlive = ReadAlive(configPath);
        if (lastAlive is null)
            return null;   // first run after installing: nothing to compare against

        var utcNow = now.ToUniversalTime();
        var bootedAt = utcNow - uptime;
        var gap = utcNow - lastAlive.Value;

        if (gap < StoppageThreshold)
            return null;

        if (lastAlive.Value < bootedAt)
            return null;   // the machine was off; that is a shutdown, not a stoppage

        return $"Sentinel 服务中断了 {gap.TotalMinutes:F0} 分钟，而这段时间里机器一直没有关过。";
    }

    private sealed record SealRecord
    {
        public string Reason { get; set; } = "";

        public string At { get; set; } = "";
    }

    private sealed record AliveRecord
    {
        public string At { get; set; } = "";

        public string Detail { get; set; } = "";
    }
}
