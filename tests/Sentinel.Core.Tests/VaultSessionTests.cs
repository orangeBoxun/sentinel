using System.IO;
using System.Text;
using Sentinel.Core.Vault;
using Xunit;

namespace Sentinel.Core.Tests;

public class VaultSessionTests : IDisposable
{
    private readonly string _config =
        Path.Combine(Path.GetTempPath(), "sentinel-session-" + Guid.NewGuid().ToString("N"), "config.json");

    private readonly string _scratch;

    public VaultSessionTests()
    {
        _scratch = Path.Combine(Path.GetDirectoryName(_config)!, "scratch");
        Directory.CreateDirectory(_scratch);

    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_config)!, true);
        }
        catch
        {
            // Leftover temp directory is not worth failing over.
        }
    }

    /// <summary>测试里的"这台机器"。绑定的门要用它 —— 会话由它去问"本机秘密"。</summary>
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private VaultSession Session() => new(_config, null, () => Secret);

    private string ScratchFile(string name, string content)
    {
        var path = Path.Combine(_scratch, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ------------------------------------------------------------------ 建立

    [Fact]
    public void A_machine_with_no_vault_says_so()
    {
        using var session = Session();

        Assert.Equal(VaultPhase.NotSetUp, session.Phase);
        Assert.False(session.IsSealed);
    }

    [Fact]
    public void Creating_one_opens_it()
    {
        using var session = Session();
        var result = session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(VaultPhase.Unlocked, session.Phase);
        Assert.True(VaultGuard.VaultExists(_config));
    }

    [Fact]
    public void Without_a_guardian_code_there_is_no_vault()
    {
        using var session = Session();

        var result = session.Create("一个足够长的密码", "不是码");

        Assert.False(result.Ok);
        Assert.Equal(VaultPhase.NotSetUp, session.Phase);
        Assert.False(VaultGuard.VaultExists(_config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("短")]
    public void A_short_password_is_refused(string password)
    {
        using var session = Session();

        Assert.False(session.Create(password, VaultCrypto.NewGuardianCode()).Ok);
        Assert.Equal(VaultPhase.NotSetUp, session.Phase);
    }

    // ------------------------------------------------------------------ 开关

    [Fact]
    public void Closing_and_reopening_with_the_password_works()
    {
        var code = VaultCrypto.NewGuardianCode();

        using var session = Session();
        Assert.True(session.Create("一个足够长的密码", code).Ok);

        session.Import(ScratchFile("a.txt", "内容"), "a.txt");
        session.Lock();
        Assert.Equal(VaultPhase.Locked, session.Phase);

        Assert.True(session.UnlockWithPassword("一个足够长的密码").Ok);
        Assert.Equal(VaultPhase.Unlocked, session.Phase);
        Assert.Single(session.Entries);
    }

    [Fact]
    public void Reopening_after_a_restart_works()
    {
        var code = VaultCrypto.NewGuardianCode();

        using (var session = Session())
        {
            Assert.True(session.Create("一个足够长的密码", code).Ok);
            session.Import(ScratchFile("a.txt", "内容"), "a.txt");
        }

        using var reopened = Session();
        Assert.Equal(VaultPhase.Locked, reopened.Phase);
        Assert.True(reopened.UnlockWithPassword("一个足够长的密码").Ok);
        Assert.Single(reopened.Entries);
    }

    [Fact]
    public void The_wrong_password_leaves_it_shut()
    {
        using var session = Session();
        Assert.True(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);
        session.Lock();

        var result = session.UnlockWithPassword("猜的");

        Assert.False(result.Ok);
        Assert.Equal(VaultPhase.Locked, session.Phase);
    }

    // ------------------------------------------------------------------ 用

    [Fact]
    public void Files_go_in_and_come_back_out()
    {
        using var session = Session();
        Assert.True(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);

        Assert.True(session.Import(ScratchFile("doc.txt", "要保管的内容"), "文件/doc.txt").Ok);
        Assert.Single(session.Entries);
        Assert.Equal("文件/doc.txt", session.Entries[0].Path);

        var output = Path.Combine(_scratch, "out");
        Assert.True(session.Extract("文件/doc.txt", output).Ok);
        Assert.Equal("要保管的内容", File.ReadAllText(Path.Combine(output, "doc.txt")));
    }

    [Fact]
    public void Removing_takes_it_out_of_the_list()
    {
        using var session = Session();
        Assert.True(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);
        session.Import(ScratchFile("a.txt", "x"), "a.txt");

        Assert.Equal(1, session.Remove("a.txt"));
        Assert.Empty(session.Entries);
    }

    [Fact]
    public void Nothing_works_while_it_is_closed()
    {
        var code = VaultCrypto.NewGuardianCode();

        using var session = Session();
        Assert.True(session.Create("一个足够长的密码", code).Ok);
        session.Import(ScratchFile("a.txt", "x"), "a.txt");
        session.Lock();

        Assert.False(session.Import(ScratchFile("b.txt", "y"), "b.txt").Ok);
        Assert.False(session.Extract("a.txt", _scratch).Ok);
        Assert.Equal(0, session.Remove("a.txt"));
        Assert.Empty(session.Entries);
    }

    [Fact]
    public void Folders_are_derived_from_the_file_paths()
    {
        using var session = Session();
        Assert.True(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);

        session.Import(ScratchFile("a.txt", "x"), "甲/乙/c.txt");
        session.Import(ScratchFile("b.txt", "y"), "甲/d.txt");

        Assert.Equal(new[] { "甲", "甲/乙" }, session.Folders());
    }

    // ------------------------------------------------------------------ 改钥匙

    [Fact]
    public void Changing_the_password_leaves_the_guardian_code_working()
    {
        var code = VaultCrypto.NewGuardianCode();

        using var session = Session();
        Assert.True(session.Create("这是一个足够长的密码", code).Ok);
        session.Import(ScratchFile("doc.txt", "内容"), "doc.txt");

        Assert.True(session.ChangePassword("换了一个足够长的新密码").Ok);

        session.Lock();
        Assert.False(session.UnlockWithPassword("这是一个足够长的密码").Ok);
        Assert.True(session.UnlockWithPassword("换了一个足够长的新密码").Ok);

        // The guardian's door is untouched. It has to be: the code is with somebody else, and a
        // password change that needed it would mean a trip to them every time.
        Assert.Equal(session.UnlockWithPassword("换了一个足够长的新密码"), session.UnlockWithGuardianCode(code));
    }

    [Fact]
    public void Changing_the_password_keeps_the_files()
    {
        using var session = Session();
        Assert.True(session.Create("这是一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);
        session.Import(ScratchFile("doc.txt", "还在"), "doc.txt");

        Assert.True(session.ChangePassword("换了一个足够长的新密码").Ok);
        session.Lock();
        Assert.True(session.UnlockWithPassword("换了一个足够长的新密码").Ok);

        Assert.Single(session.Entries);
    }

    [Fact]
    public void A_sealed_vault_will_not_take_a_new_password()
    {
        var code = VaultCrypto.NewGuardianCode();

        using (var session = Session())
            Assert.True(session.Create("这是一个足够长的密码", code).Ok);

        VaultGuard.SealVault(_config, "测试");

        using var sealedSession = Session();
        Assert.True(sealedSession.UnlockWithGuardianCode(code).Ok);   // open, but sealed

        // Refused: setting a password here would be a way of putting back the door that sealing
        // took away.
        Assert.False(sealedSession.ChangePassword("换了一个足够长的新密码").Ok);
    }

    [Fact]
    public void Changing_the_guardian_code_needs_the_current_one()
    {
        var code = VaultCrypto.NewGuardianCode();
        var fresh = VaultCrypto.NewGuardianCode();

        using var session = Session();
        Assert.True(session.Create("这是一个足够长的密码", code).Ok);

        // Without this, someone at an open vault could set the guardian's secret to one of their
        // own — and the door that exists so they cannot open this alone would be theirs.
        var refused = session.ChangeGuardianCode(VaultCrypto.NewGuardianCode(), fresh);
        Assert.False(refused.Ok);
        Assert.Contains("监护码", refused.Error);

        Assert.True(session.ChangeGuardianCode(code, fresh).Ok);

        session.Lock();
        Assert.True(session.UnlockWithGuardianCode(fresh).Ok);
        Assert.False(session.UnlockWithGuardianCode(code).Ok);   // the old one is retired
        Assert.True(session.UnlockWithPassword("这是一个足够长的密码").Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("短")]
    public void A_short_new_password_is_refused(string password)
    {
        using var session = Session();
        Assert.True(session.Create("这是一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);

        Assert.False(session.ChangePassword(password).Ok);
        session.Lock();
        Assert.True(session.UnlockWithPassword("这是一个足够长的密码").Ok);   // unchanged
    }

    // ------------------------------------------------------------------ 清空

    [Fact]
    public void Clearing_leaves_the_machine_ready_for_a_new_vault()
    {
        using var session = Session();
        Assert.True(session.Create("这是一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);
        session.Import(ScratchFile("doc.txt", "内容"), "doc.txt");

        Assert.True(session.Reset().Ok);
        Assert.Equal(VaultPhase.NotSetUp, session.Phase);
        Assert.False(VaultGuard.VaultExists(_config));

        // And a new one can be made straight away.
        Assert.True(session.Create("这是一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);
        Assert.Empty(session.Entries);
    }

    [Fact]
    public void Clearing_keeps_the_vault_directory_itself()
    {
        using var session = Session();
        Assert.True(session.Create("这是一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);

        Assert.True(session.Reset().Ok);

        // Its permissions are set by the installer and cannot be recreated from here: a folder made
        // by this program inherits the read-only parent's, and after that no vault could ever be
        // created on this machine again — with nothing on screen to say why.
        Assert.True(Directory.Exists(VaultGuard.VaultRootFor(_config)));
    }

    [Fact]
    public void Clearing_is_the_way_out_of_a_seal_with_no_guardian_code()
    {
        var code = VaultCrypto.NewGuardianCode();

        using (var session = Session())
            Assert.True(session.Create("这是一个足够长的密码", code).Ok);

        VaultGuard.SealVault(_config, "测试：监护码也不见了");

        // Sealed, and the guardian code is gone: without this the vault can never be opened and a
        // new one can never be made.
        using var stuck = Session();
        Assert.True(stuck.IsSealed);
        Assert.False(stuck.UnlockWithPassword("这是一个足够长的密码").Ok);

        Assert.True(stuck.Reset().Ok);
        Assert.False(stuck.IsSealed);
        Assert.Null(VaultGuard.ReadSeal(_config));

        // A seal outliving the vault would mean the next one is born sealed.
        Assert.True(stuck.Create("这是一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);
    }

    [Fact]
    public void Clearing_never_touches_anything_outside_the_vault()
    {
        using var session = Session();
        Assert.True(session.Create("这是一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);

        var parent = Path.GetDirectoryName(VaultGuard.VaultRootFor(_config))!;
        var before = Directory.GetFileSystemEntries(parent).OrderBy(x => x).ToArray();

        Assert.True(session.Reset().Ok);

        var after = Directory.GetFileSystemEntries(parent).OrderBy(x => x).ToArray();
        Assert.Equal(before, after);
    }

    // ------------------------------------------------------------------ 封印

    [Fact]
    public void A_seal_shuts_the_password_door_and_leaves_the_guardians()
    {
        var code = VaultCrypto.NewGuardianCode();

        using var session = Session();
        Assert.True(session.Create("一个足够长的密码", code).Ok);
        session.Import(ScratchFile("doc.txt", "要保管的内容"), "doc.txt");
        session.Lock();

        VaultGuard.Seal(_config, "测试：服务被删了");

        using var afterSeal = Session();
        Assert.True(afterSeal.IsSealed);
        Assert.Contains("服务被删了", afterSeal.SealReason);

        var refused = afterSeal.UnlockWithPassword("一个足够长的密码");
        Assert.False(refused.Ok);
        Assert.Contains("服务被删了", refused.Error);

        // The whole design rests on this: the guardian code still opens it, and the contents are
        // exactly as they were.
        Assert.True(afterSeal.UnlockWithGuardianCode(code).Ok);
        Assert.Single(afterSeal.Entries);
    }

    [Fact]
    public void An_unlock_asks_the_second_question_and_seals_on_the_answer()
    {
        using (var session = Session())
            Assert.True(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);

        // The second opinion is the Agent's: it is the thing that can see whether the Service is
        // still registered and still answering. It is asked at the unlock, not at the constructor.
        using var session2 = new VaultSession(_config,
            () => GuardStatus.SealedWith("Sentinel 服务没有在运行。"));

        var result = session2.UnlockWithPassword("一个足够长的密码");

        Assert.False(result.Ok);
        Assert.True(session2.IsSealed);
        Assert.Contains("服务", session2.SealReason);
        Assert.True(File.Exists(VaultGuard.SealPathFor(_config)));
    }

    [Fact]
    public void Merely_opening_the_window_does_not_ask_the_second_question()
    {
        using (var session = Session())
            Assert.True(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);

        // Constructing a session reads the seal marker and nothing else. Judging the Service here
        // would mean judging it at the moment the window opens, with no room to wait for one that
        // is merely slow to start after a reboot.
        using var session2 = new VaultSession(_config,
            () => GuardStatus.SealedWith("Sentinel 服务没有在运行。"));

        Assert.False(session2.IsSealed);
        Assert.Equal(VaultPhase.Locked, session2.Phase);
    }

    [Fact]
    public void A_second_opinion_can_seal_but_never_unseal()
    {
        using (var session = Session())
            Assert.True(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);

        VaultGuard.Seal(_config, "别处发现的");

        // A check that says "fine" must not reopen a door the files already closed.
        using var session2 = new VaultSession(_config, () => GuardStatus.Intact, () => Secret);
        Assert.True(session2.IsSealed);
    }

    [Fact]
    public void Recovering_with_the_guardian_code_needs_only_a_new_password()
    {
        var code = VaultCrypto.NewGuardianCode();

        using (var session = Session())
        {
            Assert.True(session.Create("一个足够长的密码", code).Ok);
            session.Import(ScratchFile("doc.txt", "还在"), "doc.txt");
        }

        VaultGuard.Seal(_config, "测试");

        using var recovered = Session();
        Assert.True(recovered.UnlockWithGuardianCode(code).Ok);

        // 只给一个密码。**监护码保持现在这个** —— 封印多半不是泄露引起的（误封、装机、断电），
        // 逼着人专程去换一次监护码是纯摩擦；想换，界面上本来就有换监护码的按钮。
        var unsealed = recovered.UnsealWithPassword("换一个足够长的新密码");

        Assert.True(unsealed.Ok, unsealed.Error);
        Assert.False(recovered.IsSealed);
        Assert.Null(VaultGuard.ReadSeal(_config));

        recovered.Lock();

        Assert.True(recovered.UnlockWithPassword("换一个足够长的新密码").Ok);   // 新密码能用
        Assert.False(recovered.UnlockWithPassword("一个足够长的密码").Ok);     // 旧密码不行（门被删过，重包的是新的）

        Assert.True(recovered.UnlockWithGuardianCode(code).Ok);              // ★ 同一个监护码照样能开

        recovered.Lock();
        Assert.True(recovered.UnlockWithPassword("换一个足够长的新密码").Ok);
        Assert.Single(recovered.Entries);
    }

    [Fact]
    public void No_vault_means_nothing_to_seal()
    {
        // A marker written before a vault existed would tell someone who never had one that theirs
        // was taken away — and would block the first setup.
        VaultGuard.SealVault(_config, "测试");

        Assert.False(File.Exists(VaultGuard.SealPathFor(_config)));

        using var session = Session();
        Assert.Equal(VaultPhase.NotSetUp, session.Phase);
        Assert.True(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);
    }

    [Fact]
    public void A_machine_with_no_protection_will_not_hold_a_vault()
    {
        var bare = Path.Combine(Path.GetDirectoryName(_config)!, "bare", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);

        // Creating one asks the second question, so "nothing is guarding this machine" is a refusal
        // rather than a vault that would refuse to open the moment it was needed.
        using var session = new VaultSession(bare, () => GuardStatus.SealedWith("Sentinel 服务没有在运行。"), () => Secret);

        Assert.False(session.Create("一个足够长的密码", VaultCrypto.NewGuardianCode()).Ok);
        Assert.False(VaultGuard.VaultExists(bare));
    }

    [Fact]
    public void The_password_is_not_stored_anywhere_in_the_clear()
    {
        const string password = "SENTINEL-PASSWORD-MARKER-9f3a";

        using (var session = Session())
            Assert.True(session.Create(password, VaultCrypto.NewGuardianCode()).Ok);

        var needle = Encoding.UTF8.GetBytes(password);
        var keys = File.ReadAllBytes(VaultGuard.KeysPathFor(_config));

        Assert.False(KeysContain(keys, needle));
    }

    private static bool KeysContain(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j])
                    continue;

                match = false;
                break;
            }

            if (match)
                return true;
        }

        return false;
    }
}
