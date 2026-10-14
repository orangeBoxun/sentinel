using Sentinel.Core.Config;

namespace Sentinel.Core.Vault;

public enum VaultPhase
{
    /// <summary>No vault yet — the setup flow is what comes next.</summary>
    NotSetUp,

    /// <summary>There is a vault and it is closed.</summary>
    Locked,

    /// <summary>Open: the file list is live and import/extract work.</summary>
    Unlocked,
}

public sealed record VaultResult(bool Ok, string? Error = null)
{
    public static readonly VaultResult Success = new(true);

    public static VaultResult Fail(string error) => new(false, error);
}

/// <summary>
/// The vault as the tray app drives it: set up, open, use, close.
///
/// <para>
/// Deliberately free of any UI reference, so the rules below are testable without a window. It is
/// also the only place that decides when the user's password is allowed, which keeps that decision
/// in one readable sequence rather than spread across event handlers.
/// </para>
/// </summary>
public sealed class VaultSession : IDisposable
{
    /// <summary>Short enough to be easy to live with, long enough that the KDF is not the weak part.</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>
    /// 这台机器上那份秘密的来源（由服务持有）。密码那道门绑着它 —— 见 VaultCrypto.DeriveBoundKey。
    /// 拿不到它就只剩监护码那条路。
    /// </summary>
    private readonly Func<byte[]?>? _machineSecret;

    /// <summary>这次打开用的监护码；<see cref="Lock"/> 会把它清掉。</summary>
    private string? _guardianCode;

    private readonly string _configPath;
    private readonly Func<GuardStatus>? _extraCheck;

    private VaultKeys? _keys;
    private VaultStore? _store;
    private byte[]? _dataKey;

    /// <param name="extraCheck">
    /// A second opinion on whether the protection is standing, for things the files cannot show —
    /// in the Agent this asks whether the Service is still there and still answering. It can add a
    /// seal but never remove one.
    /// </param>
    public VaultSession(
        string configPath,
        Func<GuardStatus>? extraCheck = null,
        Func<byte[]?>? machineSecret = null)
    {
        _configPath = configPath;
        _extraCheck = extraCheck;
        _machineSecret = machineSecret;

        _keys = VaultKeys.Load(VaultGuard.KeysPathFor(configPath));
        Phase = _keys is null ? VaultPhase.NotSetUp : VaultPhase.Locked;

        // The marker only. Asking the caller's second question here would mean judging the Service
        // at the moment the window opens, when there is no room to wait for one that is merely
        // late — and a Service that is late is the ordinary case after a reboot.
        RefreshCore(null);
    }

    public string ConfigPath => _configPath;

    public VaultPhase Phase { get; private set; }

    /// <summary>Non-null once the vault has been sealed. Explains why the password stopped working.</summary>
    public string? SealReason { get; private set; }

    public bool IsSealed => SealReason is not null;

    public GuardStatus Guard { get; private set; } = GuardStatus.Intact;

    public IReadOnlyList<VaultEntry> Entries => _store?.Entries ?? [];

    public string VaultRoot => VaultGuard.VaultRootFor(_configPath);

    /// <summary>
    /// Re-reads the evidence and seals the vault if it says the protection is gone.
    ///
    /// <para>
    /// Only two things can say so: the seal marker, and whatever <c>extraCheck</c> observed just
    /// now. There is deliberately no third. Reading a liveness timestamp here — deciding "nobody
    /// has written this recently, therefore someone stopped them" — is what made an unplugged
    /// laptop and a night's sleep look exactly like a dismantled install.
    /// </para>
    /// </summary>
    public GuardStatus Refresh() => RefreshCore(_extraCheck);

    private GuardStatus RefreshCore(Func<GuardStatus>? extraCheck)
    {
        var seal = VaultGuard.ReadSeal(_configPath);
        var status = seal is not null ? GuardStatus.SealedWith(seal) : GuardStatus.Intact;

        if (!status.Sealed && extraCheck is not null)
            status = extraCheck();

        Guard = status;

        if (status.Sealed && VaultGuard.SealVault(_configPath, status.Reason ?? "防护已被拆除"))
            SealReason = status.Reason ?? "防护已被拆除";

        if (status.Sealed)
        {
            SealReason ??= status.Reason;
            Lock();
        }

        return status;
    }

    /// <summary>本机秘密；拿不到返回 null（服务没在跑，或者还没建过）。</summary>
    private byte[]? Secret()
    {
        try
        {
            return _machineSecret?.Invoke();
        }
        catch
        {
            return null;
        }
    }

    private static VaultResult NoSecret(string doing) =>
        VaultResult.Fail(
            $"{doing}需要这台机器上那份秘密，而现在拿不到它（Sentinel 服务没在运行？）。"
            + "密码那道门跟机器绑在一起：要么把服务起回来，要么用监护码打开。");

    // ------------------------------------------------------------------ 建立

    /// <summary>Creates the vault and opens it. There is no way to do this without a guardian code.</summary>
    public VaultResult Create(string password, string guardianCode)
    {
        Refresh();

        if (IsSealed)
            return VaultResult.Fail(SealReason ?? "防护已被拆除");

        if (Phase != VaultPhase.NotSetUp)
            return VaultResult.Fail("已经有一个保险箱了。");

        if (password.Length < MinimumPasswordLength)
            return VaultResult.Fail($"密码至少 {MinimumPasswordLength} 位。");

        if (VaultCrypto.NormalizeGuardianCode(guardianCode) is null)
            return VaultResult.Fail("监护码格式不对。");

        var secret = Secret();
        if (secret is null)
            return NoSecret("建立保险箱");

        try
        {
            var keys = VaultKeys.Create(password, guardianCode, secret);
            var key = keys.UnlockWithUserPassword(password, secret);
            if (key is null)
                return VaultResult.Fail("刚写好的钥匙打不开 —— 保险箱没有被创建。");

            // Container first, then the keys file. The keys file is what makes a vault "exist", so
            // it goes last: the other order would leave behind a vault that claims to exist and
            // cannot be opened.
            _store = VaultStore.Create(VaultRoot, key);
            keys.Save(VaultGuard.KeysPathFor(_configPath));

            _keys = keys;
            _dataKey = key;
            Phase = VaultPhase.Unlocked;

            return VaultResult.Success;
        }
        catch (Exception ex)
        {
            return VaultResult.Fail($"创建保险箱失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 开锁

    public VaultResult UnlockWithPassword(string password)
    {
        Refresh();

        if (IsSealed)
            return VaultResult.Fail(SealReason ?? "防护已被拆除");

        if (_keys is null)
            return VaultResult.Fail("还没有保险箱。");

        if (!_keys.UserDoorAvailable)
            return VaultResult.Fail("这个保险箱已经用不了了，只能拿监护码打开。");

        var secret = Secret();

        if (_keys.MachineBound && secret is null)
            return NoSecret("用密码打开");

        var key = _keys.UnlockWithUserPassword(password, secret);

        if (key is null)
            return VaultResult.Fail("密码不对。");

        // 老文件（只用密码派生的那道门）就地升级成绑定机器的 —— 内容一个字节都不动。
        // 升级不了（拿不到本机秘密）也照样开，只是下次还得走这一趟。
        if (!_keys.MachineBound && secret is not null)
        {
            try
            {
                _keys.BindToMachine(key, password, secret);
                _keys.Save(VaultGuard.KeysPathFor(_configPath));
            }
            catch
            {
                // 升级失败不该让人打不开自己的东西。
            }
        }

        return Open(key);
    }

    /// <summary>
    /// The way back in after a seal. The data key comes out of the guardian's wrapping, so the files
    /// are exactly as they were — the seal took away a door, never anything inside.
    /// </summary>
    public VaultResult UnlockWithGuardianCode(string code)
    {
        if (_keys is null)
            return VaultResult.Fail("还没有保险箱。");

        if (VaultCrypto.NormalizeGuardianCode(code) is null)
            return VaultResult.Fail("监护码格式不对 —— 检查一下是不是抄错了。");

        var key = _keys.UnlockWithGuardianCode(code);
        if (key is null)
            return VaultResult.Fail("监护码不对。");

        // 记下这次用的监护码：解除封印时要用它取回"寄存的那把用户派生钥匙"，免得人再输一遍。
        // **锁上就忘**（见 Lock）。保险箱的钥匙本来就已在内存里，这一份并不比它更敏感。
        //
        // 存**原样**，不存归一化之后的结果：归一化把校验位去掉、只剩 32 个字符，再归一化一次
        // 会被判成"格式不对"（它要求 34 位）。归一是拿到码的人（用户）做的事，这里不要抢着做。
        _guardianCode = code;

        return Open(key);
    }

    /// <summary>
    /// 用监护码进来之后：设一个新密码，**封印就此解除**，监护码保持现在这个。
    ///
    /// <para>
    /// 以前这里必须同时换掉密码和监护码，理由是"监护码在这台机器上被敲过"。可封印绝大多数
    /// 不是泄露引起的（误封、装机、断电都算），而监护码在别人手里 —— 强制换一次就是让人专程
    /// 再跑一趟，换来的安全收益近乎为零。真想换，界面上本来就有换监护码的按钮。
    /// </para>
    /// <para>
    /// 密码则<b>必须</b>重新设一个：封印把它那道门（包装）删掉了 —— 那不是策略，是信息没了，
    /// 旧密码无从"恢复"，只能重新包一遍。你可以填回原来那个。
    /// </para>
    /// </summary>
    public VaultResult UnsealWithPassword(string newPassword)
    {
        if (Phase != VaultPhase.Unlocked || _dataKey is null || _keys is null)
            return VaultResult.Fail("先用监护码打开保险箱。");

        if (newPassword.Length < MinimumPasswordLength)
            return VaultResult.Fail($"密码至少 {MinimumPasswordLength} 位。");

        try
        {
            var secret = Secret();
            if (secret is null)
                return NoSecret("设密码");

            _keys.SetUserPassword(_dataKey, newPassword, secret);

            return FinishUnseal();
        }
        catch (Exception ex)
        {
            return VaultResult.Fail($"解除封印失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 用监护码打开之后：**原来的密码继续管用，什么都不用填**。
    ///
    /// <para>
    /// 原理：建保险箱时把"由密码派生出来的那把钥匙"另存了一份、**用监护码加密**（见
    /// <see cref="VaultKeys.GuardianSealedUserKey"/>）。封印会把用户那道门连包装一起删掉 ——
    /// 所以正常情形下"下次还用原来那个密码"是做不到的；但解除封印时人已经拿着监护码，
    /// 于是把那份派生钥匙取出来、配上新的本机秘密，门就原样包回去了。
    /// </para>
    /// <para>
    /// 两种情况走不通：从来没寄存过（老保险箱），或者**改过密码**（改密码会作废那份寄存 ——
    /// 否则旧密码会在解封之后复活）。那时界面会退回"请设一个新密码"。
    /// </para>
    /// </summary>
    public VaultResult UnsealKeepingPassword()
    {
        if (Phase != VaultPhase.Unlocked || _dataKey is null || _keys is null)
            return VaultResult.Fail("先用监护码打开保险箱。");

        if (!CanRestorePasswordWithoutTyping)
            return VaultResult.Fail("这个保险箱没有寄存原来那把钥匙 —— 请设一个新密码。");

        var secret = Secret();
        if (secret is null)
            return NoSecret("解除封印");

        try
        {
            if (!_keys.TryRestoreUserDoor(_dataKey, _guardianCode!, secret))
                return VaultResult.Fail("取回原来那把钥匙失败了 —— 请设一个新密码。");

            return FinishUnseal();
        }
        catch (Exception ex)
        {
            return VaultResult.Fail($"解除封印失败：{ex.Message}");
        }
    }

    /// <summary>能不能"什么都不填"地解除封印。界面靠它决定显示哪个按钮。</summary>
    public bool CanRestorePasswordWithoutTyping =>
        _guardianCode is not null && _keys?.GuardianSealedUserKey is not null;

    /// <summary>解除封印的收尾：状态回正常、抹掉标记。两条路（填密码 / 不填密码）共用。</summary>
    private VaultResult FinishUnseal()
    {
        _keys!.ResetToNormal();
        _keys.Save(VaultGuard.KeysPathFor(_configPath));

        VaultGuard.ClearSeal(_configPath);
        SealReason = null;
        Guard = GuardStatus.Intact;

        return VaultResult.Success;
    }

    private VaultResult Open(byte[] key)
    {
        var store = VaultStore.Open(VaultRoot, key);
        if (store is null)
        {
            CryptographicZero(key);
            return VaultResult.Fail("保险箱的数据打不开 —— 索引损坏，或者这份钥匙对不上。");
        }

        _store?.Dispose();
        _store = store;
        _dataKey = key;
        Phase = VaultPhase.Unlocked;

        return VaultResult.Success;
    }

    public void Lock()
    {
        _store?.Dispose();
        _store = null;

        if (_dataKey is not null)
            CryptographicZero(_dataKey);

        _dataKey = null;
        _guardianCode = null;

        if (Phase == VaultPhase.Unlocked)
            Phase = VaultPhase.Locked;
    }

    // ------------------------------------------------------------------ 使用

    public VaultResult Import(string sourcePath, string vaultPath)
    {
        if (Phase != VaultPhase.Unlocked || _store is null)
            return VaultResult.Fail("保险箱没打开。");

        try
        {
            _store.Import(sourcePath, vaultPath);
            return VaultResult.Success;
        }
        catch (Exception ex)
        {
            return VaultResult.Fail($"放不进去：{ex.Message}");
        }
    }

    public VaultResult Extract(string vaultPath, string destinationFolder)
    {
        if (Phase != VaultPhase.Unlocked || _store is null)
            return VaultResult.Fail("保险箱没打开。");

        var entry = _store.Find(vaultPath);
        if (entry is null)
            return VaultResult.Fail($"保险箱里没有：{vaultPath}");

        try
        {
            _store.Extract(vaultPath, Path.Combine(destinationFolder, entry.Name));
            return VaultResult.Success;
        }
        catch (Exception ex)
        {
            return VaultResult.Fail($"取不出来：{ex.Message}");
        }
    }

    public int Remove(string vaultPath)
    {
        if (Phase != VaultPhase.Unlocked || _store is null)
            return 0;

        return _store.Remove(vaultPath);
    }

    // ------------------------------------------------------------------ 改钥匙

    /// <summary>
    /// Changes the user's password. The vault is open, which is the proof that whoever is asking
    /// already holds a door, so no second confirmation is asked for.
    /// </summary>
    public VaultResult ChangePassword(string newPassword)
    {
        if (Phase != VaultPhase.Unlocked || _dataKey is null || _keys is null)
            return VaultResult.Fail("先打开保险箱。");

        if (IsSealed)
            return VaultResult.Fail(
                "保险箱处于封印状态，改不了 —— 先用监护码打开，"
                + "打开之后设一个新密码，封印就解除了（监护码保持现在这个）。");

        if (newPassword.Length < MinimumPasswordLength)
            return VaultResult.Fail($"密码至少 {MinimumPasswordLength} 位。");

        try
        {
            var secret = Secret();
            if (secret is null)
                return NoSecret("改密码");

            _keys.SetUserPassword(_dataKey, newPassword, secret);
            _keys.Save(VaultGuard.KeysPathFor(_configPath));
            return VaultResult.Success;
        }
        catch (Exception ex)
        {
            return VaultResult.Fail($"改密码失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Changes the guardian code, and requires the current one.
    ///
    /// <para>
    /// That requirement is the whole safeguard. Without it, someone sitting at an open vault could
    /// set the guardian's secret to one of their own choosing — and the second door, the one that
    /// exists precisely so that they cannot open this alone, would be theirs.
    /// </para>
    /// </summary>
    public VaultResult ChangeGuardianCode(string currentCode, string newCode)
    {
        if (Phase != VaultPhase.Unlocked || _dataKey is null || _keys is null)
            return VaultResult.Fail("先打开保险箱。");

        if (IsSealed)
            return VaultResult.Fail(
                "保险箱处于封印状态，改不了 —— 先用监护码打开，"
                + "打开之后设一个新密码，封印就解除了（监护码保持现在这个）。");

        if (_keys.UnlockWithGuardianCode(currentCode) is null)
            return VaultResult.Fail("当前的监护码不对。");

        if (VaultCrypto.NormalizeGuardianCode(newCode) is null)
            return VaultResult.Fail("新的监护码格式不对。");

        try
        {
            _keys.SetGuardianCode(_dataKey, newCode);
            _keys.Save(VaultGuard.KeysPathFor(_configPath));
            return VaultResult.Success;
        }
        catch (Exception ex)
        {
            return VaultResult.Fail($"改监护码失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 清空

    /// <summary>
    /// Deletes the vault and everything in it, leaving the machine ready to set up a new one.
    ///
    /// <para>
    /// This exists because of two dead ends that otherwise have no way out at all: the password and
    /// the guardian code both lost, and a seal whose guardian code is gone. In both, the vault
    /// refuses to open and <see cref="Create"/> refuses to make another one — the machine is stuck
    /// holding data nobody can reach. What this costs is the certainty of never getting that data
    /// back, which in those two cases is already true.
    /// </para>
    /// <para>
    /// No confirmation here. The caller owns that, because only the caller can say what is in the
    /// vault.
    /// </para>
    /// </summary>
    public VaultResult Reset()
    {
        Lock();

        try
        {
            if (Directory.Exists(VaultRoot))
            {
                foreach (var entry in Directory.GetFileSystemEntries(VaultRoot))
                {
                    if (Directory.Exists(entry))
                        IoRetry.Try(() => Directory.Delete(entry, recursive: true));
                    else
                        IoRetry.Try(() => File.Delete(entry));
                }
            }

            // The directory itself is left alone on purpose: the installer gives it permissions the
            // program cannot recreate — a folder made here would inherit the read-only parent's, and
            // after that no vault could ever be created on this machine again.

            // A seal outlives the vault it belonged to. Leaving it would mean the next vault is
            // born sealed.
            VaultGuard.ClearSeal(_configPath);

            _keys = null;
            Phase = VaultPhase.NotSetUp;
            SealReason = null;
            Guard = GuardStatus.Intact;

            return VaultResult.Success;
        }
        catch (Exception ex)
        {
            return VaultResult.Fail($"清空保险箱失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 备份

    /// <summary>When a backup was last written, or null if never.</summary>
    public DateTimeOffset? LastExport => VaultBackup.LastExport(_configPath);

    public VaultResult Export(string destination) => VaultBackup.Export(_configPath, destination);

    /// <summary>
    /// Puts a backup back and shuts the session, because whatever was open belonged to the vault
    /// that was just replaced. The user unlocks the restored one themselves — a restore is not a way
    /// to end up holding an open vault nobody asked for.
    /// </summary>
    public VaultResult RestoreFromBackup(string source)
    {
        Lock();

        var result = VaultBackup.Restore(_configPath, source);
        if (!result.Ok)
            return result;

        _keys = VaultKeys.Load(VaultGuard.KeysPathFor(_configPath));
        Phase = _keys is null ? VaultPhase.NotSetUp : VaultPhase.Locked;
        SealReason = null;

        // Re-read the guard: a vault restored onto a machine whose protection is gone is still a
        // vault that will not open with the password.
        Refresh();

        return VaultResult.Success;
    }

    /// <summary>The folders implied by the file paths, for showing a tree rather than a flat list.</summary>
    public IEnumerable<string> Folders()
    {
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Entries)
        {
            var path = entry.Path;
            var slash = path.LastIndexOf('/');

            while (slash > 0)
            {
                seen.Add(path[..slash]);
                slash = path.LastIndexOf('/', slash - 1);
            }
        }

        return seen;
    }

    private static void CryptographicZero(byte[] key) =>
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);

    public void Dispose()
    {
        Lock();
        _keys = null;
    }
}
