using System.Diagnostics;
using System.IO;
using System.Windows;
using Sentinel.Core.Vault;
using WinForms = System.Windows.Forms;

namespace Sentinel.Agent;

/// <summary>
/// The vault: setting it up, opening it, and using it.
///
/// <para>
/// A window of its own rather than a seventh tab. Everything here is a sequence — set up, unlock,
/// browse, lock — and putting it behind a password entry that shares a screen with the rule editor
/// would make the one thing that has to feel deliberate feel like a settings page.
/// </para>
/// </summary>
public partial class VaultWindow : Window
{
    /// <summary>How long to wait for the Service before taking its absence at face value.</summary>
    private const int ServiceWaitSeconds = 60;

    private const int ServiceWaitStepSeconds = 5;

    private readonly AgentState _state;
    private readonly VaultSession _session;

    /// <summary>Generated but not yet confirmed by typing it back. Nothing is written until it matches.</summary>
    private string? _pendingGuardianCode;
    private string? _pendingRecoveryCode;
    private string? _pendingNewGuardianCode;

    /// <summary>Set when the vault was opened with the guardian code, which is what prompts a reset.</summary>
    private bool _recovering;

    public VaultWindow(AgentState state)
    {
        InitializeComponent();

        _state = state;
        _session = new VaultSession(state.ConfigPath, ExtraGuardCheck);

        Refresh();
    }

    // ------------------------------------------------------------------ 防护状态

    /// <summary>
    /// The second opinion on whether the protection is standing: is the Service registered, and is
    /// it answering?
    ///
    /// <para>
    /// A direct look, rather than an inference from a timestamp. "Nobody has written the liveness
    /// record lately" is true of a stopped Service and equally true of a machine that spent the
    /// night asleep, so it cannot be used to decide anything — that mistake is what made an
    /// unplugged laptop look like a dismantled install.
    /// </para>
    /// <para>
    /// A momentary failure is not evidence either. <see cref="WaitForServiceAsync"/> runs first and
    /// gives a Service that is merely late its chance.
    /// </para>
    /// </summary>
    private GuardStatus ExtraGuardCheck()
    {
        if (!ServiceRegistered())
            return GuardStatus.SealedWith("Sentinel 服务已经不在系统里了。");

        return _state.Service.IsAvailable
            ? GuardStatus.Intact
            : GuardStatus.SealedWith("Sentinel 服务没有在运行。");
    }

    /// <summary>
    /// Gives a Service that is merely late a chance to appear before an unlock is judged.
    ///
    /// The cases this exists for are ordinary rather than exotic: the machine just booted and the
    /// Service is still starting, or it crashed and its failure action has not fired yet. Both come
    /// back within seconds, and without this both would read as "the Service is gone".
    /// </summary>
    private async Task<bool> WaitForServiceAsync()
    {
        for (var waited = 0; ; waited += ServiceWaitStepSeconds)
        {
            // A live round trip, not the cached flag: the flag could be a minute old.
            _state.Service.Heartbeat(ServiceClient.CurrentSessionId, out _);

            if (_state.Service.IsAvailable)
                return true;

            if (waited >= ServiceWaitSeconds)
                return false;

            StatusMessage.Text = $"正在等 Sentinel 服务起来…（已等 {waited} 秒）";
            await Task.Delay(TimeSpan.FromSeconds(ServiceWaitStepSeconds));
            StatusMessage.Text = "";
        }
    }

    private static bool ServiceRegistered()
    {
        try
        {
            var info = new ProcessStartInfo("sc.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            info.ArgumentList.Add("query");
            info.ArgumentList.Add("Sentinel");

            using var process = Process.Start(info);
            if (process is null)
                return true;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            // Not being able to ask is not evidence of damage. Sealing on a guess is the one mistake
            // this whole mechanism cannot afford.
            return !process.WaitForExit(5000) || process.ExitCode == 0;
        }
        catch
        {
            return true;
        }
    }

    // ------------------------------------------------------------------ 界面

    private void Refresh()
    {
        // Unlocked wins over sealed. Those two are true at the same time after coming in with the
        // guardian code — the seal is still on disk, and the vault is open for the one visit that
        // rebuilds it — and checking the seal first put the recovery flow out of reach.
        if (_session.Phase == VaultPhase.Unlocked)
        {
            ShowOnly(UnlockedPanel);
            RefreshEntries();

            StatusText.Text = _recovering
                ? "已用监护码打开。密码A 已经作废 —— 请在下面重新设置，否则下次只能用监护码。"
                : "保险箱已打开。";
        }
        else if (_session.IsSealed)
        {
            ShowOnly(SealedPanel);
            SealedReasonText.Text = _session.SealReason;
            StatusText.Text = "防护已被拆除：密码A 已作废，只有监护码能打开。";
        }
        else
        {
            var notSetUp = _session.Phase == VaultPhase.NotSetUp;
            ShowOnly(notSetUp ? SetupPanel : LockedPanel);
            StatusText.Text = "防护完好 —— 看门狗在运行，服务在册。";
        }
    }

    private void ShowOnly(UIElement panel)
    {
        foreach (var candidate in new UIElement[] { SetupPanel, LockedPanel, SealedPanel, UnlockedPanel })
            candidate.Visibility = ReferenceEquals(candidate, panel) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshEntries()
    {
        EntryList.ItemsSource = _session.Entries
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Select(e => new Row(e))
            .ToList();

        RecoveryBanner.Visibility = _recovering ? Visibility.Visible : Visibility.Collapsed;
        RefreshBackupLine();
    }

    private sealed record Row(VaultEntry Entry)
    {
        /// <summary>
        /// Not called <c>Path</c>: <c>{Binding Path}</c> reads as the Binding's own <c>Path</c>
        /// property, so the column would silently come up blank.
        /// </summary>
        public string Location => Entry.Path;

        public string SizeText => FormatSize(Entry.Size);

        public string AddedAt => Entry.AddedAt;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    private void Fail(System.Windows.Controls.TextBlock label, string? message)
    {
        label.Text = message ?? "";
        label.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ 建立

    private void GenerateGuardian_Click(object sender, RoutedEventArgs e)
    {
        _pendingGuardianCode = VaultCrypto.NewGuardianCode();
        GuardianCodeText.Text = _pendingGuardianCode;
        GuardianConfirmBox.Text = "";
        GuardianConfirmPanel.Visibility = Visibility.Visible;
        Fail(SetupError, null);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingGuardianCode is null)
        {
            Fail(SetupError, "先生成一个监护码。");
            return;
        }

        if (SetupPassword.Password != SetupConfirmPassword.Password)
        {
            Fail(SetupError, "两次输入的密码不一样。");
            return;
        }

        // Typing it back is the only moment the paper copy can be checked. After this, a
        // mistranscription would not surface until the day it is needed.
        if (VaultCrypto.NormalizeGuardianCode(GuardianConfirmBox.Text) is null)
        {
            Fail(SetupError, "敲进来的监护码和上面那串对不上，照着抄一遍再试。");
            return;
        }

        var result = _session.Create(SetupPassword.Password, GuardianConfirmBox.Text);
        if (!result.Ok)
        {
            Fail(SetupError, result.Error);
            return;
        }

        _pendingGuardianCode = null;
        SetPasswordBoxesEmpty();
        Refresh();
    }

    private void SetPasswordBoxesEmpty()
    {
        SetupPassword.Clear();
        SetupConfirmPassword.Clear();
        GuardianConfirmBox.Clear();
        UnlockPassword.Clear();
        UnlockCode.Clear();
        RecoverPassword.Clear();
        NewPasswordBox.Clear();
        NewPasswordConfirmBox.Clear();
        CurrentGuardianBox.Clear();
        NewGuardianConfirmBox.Clear();
    }

    // ------------------------------------------------------------------ 开锁

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        // Before anything is judged: a Service that is merely slow to start is not a Service that
        // is gone, and the difference decides whether the vault seals.
        await WaitForServiceAsync();

        var result = _session.UnlockWithPassword(UnlockPassword.Password);
        UnlockPassword.Clear();

        if (!result.Ok)
        {
            // The attempt itself may have been what discovered the seal, in which case the window is
            // now showing the wrong panel. Re-read before reporting the failure.
            Refresh();
            Fail(_session.IsSealed ? SealedError : UnlockError, result.Error);
            return;
        }

        Fail(UnlockError, null);
        _recovering = false;
        Refresh();
    }

    private void UnlockWithCode_Click(object sender, RoutedEventArgs e)
    {
        var wasSealed = _session.IsSealed;
        var result = _session.UnlockWithGuardianCode(wasSealed ? SealedCode.Text : UnlockCode.Text);

        if (!result.Ok)
        {
            Fail(wasSealed ? SealedError : UnlockError, result.Error);
            return;
        }

        Fail(SealedError, null);
        Fail(UnlockError, null);
        UnlockCode.Clear();
        SealedCode.Clear();

        // Coming in this way means the password door is gone. Say so, and offer to rebuild it.
        _recovering = wasSealed;
        Refresh();
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _session.Lock();
        _recovering = false;

        // Anything half-typed gets wiped too: a password sitting in a box behind a locked vault is
        // the kind of thing that survives on a screen nobody is looking at.
        SetPasswordBoxesEmpty();
        NewGuardianPanel.Visibility = Visibility.Collapsed;
        _pendingNewGuardianCode = null;

        Note(ChangePasswordMessage, null, ok: false);
        Note(ChangeGuardianMessage, null, ok: false);

        Refresh();
    }

    // ------------------------------------------------------------------ 重设

    private void GenerateRecoveryCode_Click(object sender, RoutedEventArgs e)
    {
        _pendingRecoveryCode = VaultCrypto.NewGuardianCode();
        RecoveryCodeText.Text = _pendingRecoveryCode;
        RecoveryConfirmBox.Text = "";
        RecoveryConfirmPanel.Visibility = Visibility.Visible;
        Fail(RecoveryError, null);
    }

    private void ConfirmRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRecoveryCode is null)
        {
            Fail(RecoveryError, "先生成一个新的监护码。");
            return;
        }

        if (VaultCrypto.NormalizeGuardianCode(RecoveryConfirmBox.Text) is null)
        {
            Fail(RecoveryError, "敲进来的监护码和上面那串对不上。");
            return;
        }

        var result = _session.ResetAfterRecovery(RecoverPassword.Password, RecoveryConfirmBox.Text);
        if (!result.Ok)
        {
            Fail(RecoveryError, result.Error);
            return;
        }

        _pendingRecoveryCode = null;
        _recovering = false;
        RecoverPassword.Clear();
        RecoveryConfirmBox.Clear();
        RecoveryConfirmPanel.Visibility = Visibility.Collapsed;

        MessageBox.Show(this,
            "已经重新设好。新的监护码请现在就交给监护人 —— 你手上这份用完就不作数了。",
            "保险箱", MessageBoxButton.OK, MessageBoxImage.Information);

        Refresh();
    }

    // ------------------------------------------------------------------ 使用

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var files = PickOpenFiles("选择要放进保险箱的文件", "所有文件|*.*");
        if (files is null)
            return;

        var failures = new List<string>();

        foreach (var file in files)
        {
            var result = _session.Import(file, System.IO.Path.GetFileName(file));
            if (!result.Ok)
                failures.Add($"{System.IO.Path.GetFileName(file)}：{result.Error}");
        }

        Report(failures, $"已放入 {files.Length - failures.Count} 个文件。");
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = PickFolder("选择要放进保险箱的文件夹");
        if (folder is null)
            return;

        var name = new DirectoryInfo(folder).Name;
        var result = _session.Import(folder, name);

        Report(result.Ok ? [] : [$"{name}：{result.Error}"], $"已放入文件夹「{name}」。");
    }

    private void Extract_Click(object sender, RoutedEventArgs e)
    {
        var chosen = SelectedEntries();
        if (chosen.Count == 0)
        {
            VaultMessage.Text = "先在下面选一个。";
            return;
        }

        var folder = PickFolder("取出到哪个文件夹");
        if (folder is null)
            return;

        var failures = new List<string>();

        foreach (var entry in chosen)
        {
            var result = _session.Extract(entry.Path, folder);
            if (!result.Ok)
                failures.Add($"{entry.Path}：{result.Error}");
        }

        // Worth saying plainly: from here on the vault is not protecting anything.
        Report(failures, $"已取出 {chosen.Count - failures.Count} 个到 {folder}。文件在那里是明文的。");
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var chosen = SelectedEntries();
        if (chosen.Count == 0)
        {
            VaultMessage.Text = "先在下面选一个。";
            return;
        }

        var question = chosen.Count == 1
            ? $"从保险箱里删掉「{chosen[0].Path}」？"
            : $"从保险箱里删掉 {chosen.Count} 个？";

        if (MessageBox.Show(this, question + "\n\n删掉之后就找不回来了 —— 这个「删除」是真的删除。",
                "保险箱", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var removed = chosen.Sum(entry => _session.Remove(entry.Path));
        RefreshEntries();
        VaultMessage.Text = $"已删除 {removed} 个。";
    }

    private List<VaultEntry> SelectedEntries() =>
        EntryList.SelectedItems.Cast<Row>().Select(row => row.Entry).ToList();

    private void Report(List<string> failures, string success)
    {
        RefreshEntries();
        VaultMessage.Text = failures.Count == 0 ? success : string.Join("\n", failures);
    }

    // ------------------------------------------------------------------ 选文件

    /// <summary>
    /// The file and folder pickers are the only non-WPF part of this window, and a dialog that
    /// fails to appear looks exactly like a button that does nothing. Reporting the failure is the
    /// difference between "this is broken" and "this did nothing, I think".
    /// </summary>
    private string? PickOpenFile(string title, string filter)
    {
        try
        {
            using var dialog = new WinForms.OpenFileDialog { Title = title, Filter = filter };
            return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.FileName : null;
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"打不开文件选择窗口：{ex.Message}";
            return null;
        }
    }

    private string[]? PickOpenFiles(string title, string filter)
    {
        try
        {
            using var dialog = new WinForms.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                Multiselect = true,
            };

            return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.FileNames : null;
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"打不开文件选择窗口：{ex.Message}";
            return null;
        }
    }

    private string? PickFolder(string title)
    {
        try
        {
            using var dialog = new WinForms.FolderBrowserDialog { Description = title };
            return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"打不开文件夹选择窗口：{ex.Message}";
            return null;
        }
    }

    private string? PickSaveFile(string title, string filter, string suggested)
    {
        try
        {
            using var dialog = new WinForms.SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = suggested,
                DefaultExt = VaultBackup.Extension,
            };

            return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.FileName : null;
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"打不开保存窗口：{ex.Message}";
            return null;
        }
    }

    // ------------------------------------------------------------------ 改钥匙

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (NewPasswordBox.Password != NewPasswordConfirmBox.Password)
        {
            Note(ChangePasswordMessage, "两次输入的密码不一样。", ok: false);
            return;
        }

        var result = _session.ChangePassword(NewPasswordBox.Password);

        NewPasswordBox.Clear();
        NewPasswordConfirmBox.Clear();

        Note(ChangePasswordMessage,
            result.Ok ? "密码已经改好，立刻生效。监护码不受影响。" : result.Error,
            result.Ok);
    }

    private void GenerateNewGuardian_Click(object sender, RoutedEventArgs e)
    {
        _pendingNewGuardianCode = VaultCrypto.NewGuardianCode();
        NewGuardianText.Text = _pendingNewGuardianCode;
        NewGuardianConfirmBox.Text = "";
        NewGuardianPanel.Visibility = Visibility.Visible;
        Note(ChangeGuardianMessage, null, ok: false);
    }

    private void ChangeGuardian_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingNewGuardianCode is null)
        {
            Note(ChangeGuardianMessage, "先生成一个新的监护码。", ok: false);
            return;
        }

        // Same reason as at setup: this is the only moment the paper copy can be checked.
        if (VaultCrypto.NormalizeGuardianCode(NewGuardianConfirmBox.Text) is null)
        {
            Note(ChangeGuardianMessage, "敲进来的新监护码和上面那串对不上。", ok: false);
            return;
        }

        var result = _session.ChangeGuardianCode(CurrentGuardianBox.Text, NewGuardianConfirmBox.Text);

        if (!result.Ok)
        {
            Note(ChangeGuardianMessage, result.Error, ok: false);
            return;
        }

        _pendingNewGuardianCode = null;
        CurrentGuardianBox.Clear();
        NewGuardianConfirmBox.Clear();
        NewGuardianPanel.Visibility = Visibility.Collapsed;

        MessageBox.Show(this,
            "监护码已经换好。新的那份请现在就交给监护人 —— 旧的那份从这一刻起不作数了。",
            "保险箱", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Reports an outcome next to the thing that produced it, green or red.</summary>
    private void Note(System.Windows.Controls.TextBlock label, string? message, bool ok)
    {
        label.Text = message ?? "";
        label.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        label.Foreground = ok
            ? System.Windows.Media.Brushes.DarkGreen
            : System.Windows.Media.Brushes.Firebrick;
    }

    // ------------------------------------------------------------------ 备份

    /// <summary>
    /// After this long without an export, the line turns red. The user's own rhythm is roughly
    /// monthly — password A lives on their phone and they only reach for it a couple of times a
    /// month — so a month is when "there is a backup" stops being true in practice.
    /// </summary>
    private const int BackupNagDays = 30;

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var destination = PickSaveFile(
            "把保险箱导出到哪里",
            $"Sentinel 保险箱备份|*{VaultBackup.Extension}|所有文件|*.*",
            VaultBackup.DefaultName(DateTimeOffset.Now));

        if (destination is null)
            return;

        var result = _session.Export(destination);

        // Worth saying plainly: a backup on the same disk protects against neither the disk dying
        // nor the program being purged.
        StatusMessage.Text = result.Ok
            ? $"已导出到 {destination}。把它拷到别的设备上去 —— 留在同一块盘上不算备份。"
            : result.Error;

        RefreshBackupLine();
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var source = PickOpenFile(
            "选择要恢复的备份",
            $"Sentinel 保险箱备份|*{VaultBackup.Extension}|所有文件|*.*");

        if (source is null)
            return;

        // Offer to keep what is about to be replaced, before anything is touched. The vault being
        // restored over is often the broken one, but "often" is not "always", and the only copy of
        // it is the one on this disk right now.
        var (files, bytes) = VaultGuard.VaultFootprint(_session.ConfigPath);

        if (files > 0)
        {
            var ask =
                $"现在这个保险箱里有 {files} 个文件，共 {FormatSize(bytes)}。\n\n" +
                "恢复会把它们换成备份里的内容。要先把现在这些导出成一份备份吗？\n\n" +
                "选「否」就直接换掉 —— 现在这些之后找不回来了。";

            var answer = MessageBox.Show(this, ask, "从备份恢复",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Cancel)
                return;

            if (answer == MessageBoxResult.Yes)
            {
                var destination = PickSaveFile(
                    "把现在这个保险箱导出成一份备份",
                    $"Sentinel 保险箱备份|*{VaultBackup.Extension}|所有文件|*.*",
                    VaultBackup.DefaultName(DateTimeOffset.Now));

                if (destination is null)
                    return;   // they backed out of the export; abandon the restore with it

                var export = _session.Export(destination);

                if (!export.Ok)
                {
                    StatusMessage.Text = export.Error;
                    return;
                }
            }
        }

        var question =
            "恢复这个备份？\n\n" +
            "保险箱会被换成备份里的那一份。两点要注意：\n\n" +
            "· 打开它的密码和监护码，是【做那份备份的时候】的，不是你后来改过的\n" +
            "· 如果那份备份是在封印之后做的，恢复出来也是封印的\n\n" +
            "继续吗？";

        if (MessageBox.Show(this, question, "从备份恢复",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var result = _session.RestoreFromBackup(source);

        StatusMessage.Text = result.Ok
            ? "已恢复。密码和监护码都是做那份备份时的 —— 用它打开试试。"
            : result.Error;

        Refresh();
    }

    /// <summary>
    /// The way out of a vault nobody can open. The confirmation says what is inside, because the
    /// only thing this can cost that the user does not already know is the data itself.
    /// </summary>
    private void ResetVault_Click(object sender, RoutedEventArgs e)
    {
        var (files, bytes) = VaultGuard.VaultFootprint(_session.ConfigPath);

        var contents = files == 0
            ? "这个保险箱里现在没有文件。"
            : $"这个保险箱里有 {files} 个文件，共 {FormatSize(bytes)}。";

        var question =
            "清空保险箱？\n\n" + contents + "\n\n" +
            "清空之后里面的东西永久消失，无法恢复 —— 已经导出的备份文件不受影响。\n" +
            "如果这个保险箱本来就打不开了，那些文件本来也拿不出来。\n\n" +
            "确定清空吗？";

        if (MessageBox.Show(this, question, "清空保险箱",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var result = _session.Reset();

        StatusMessage.Text = result.Ok ? "保险箱已清空。可以重新建一个了。" : result.Error;

        Refresh();
    }

    private void RefreshBackupLine()
    {
        var last = _session.LastExport;

        if (last is null)
        {
            BackupText.Foreground = System.Windows.Media.Brushes.Firebrick;
            BackupText.Text = "还没导出过备份。这块盘坏了、或者卸载时加了 -Purge，里面的东西就没了 —— 备份是唯一的后路。";
            return;
        }

        var days = (DateTimeOffset.Now - last.Value).TotalDays;

        BackupText.Foreground = days >= BackupNagDays
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.DarkGoldenrod;

        BackupText.Text = days >= BackupNagDays
            ? $"上次导出：{last.Value:yyyy-MM-dd HH:mm} —— 已经 {days:F0} 天没导过了，该再导一次。"
            : $"上次导出：{last.Value:yyyy-MM-dd HH:mm}（{days:F0} 天前）。";
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // The unlocked key goes with the window. Leaving it alive in a hidden window would mean a
        // vault that stays open because nobody closed it.
        _session.Dispose();
    }
}
