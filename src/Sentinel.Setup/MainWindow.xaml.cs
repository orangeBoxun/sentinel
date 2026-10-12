using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Sentinel.Setup;

/// <summary>
/// 图形安装程序。
///
/// <para>
/// <b>它不重写安装逻辑，它驱动 <c>install.ps1</c> / <c>uninstall.ps1</c>。</b>
/// 那两个脚本里有几件不能出错的事：给安装目录、配置文件、日志目录、保险箱分别设 ACL；
/// 给服务设 DACL（否则普通用户能停掉它）；设置失败恢复策略；以及那个标记文件。
/// 把这些用 C# 重写一遍，等于把一份已经跑通过的东西换成一份没跑过的 —— 而错的代价是
/// "装上了但保护是假的"，那种错还看不出来。所以这里只补界面、进度和错误提示。
/// </para>
/// <para>
/// 目录约定（<c>make-setup.ps1</c> 就是照这个拼的）：
/// <c>Sentinel.Setup.exe</c>、<c>install.ps1</c>、<c>uninstall.ps1</c>、<c>payload\</c> 放在同一层。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>安装标记文件，由 install.ps1 写下。用它认"这个地方装过东西"。</summary>
    private const string Marker = ".sentinel-install";

    private bool _running;

    /// <summary>装完了：主按钮变成「完成」，再点一下就关窗口。装上之后还挂着「安装 / 更新」是说不通的。</summary>
    private bool _finished;

    public MainWindow()
    {
        InitializeComponent();
        ApplyMode();
        Detect();
    }

    /// <summary>
    /// 是不是"卸载模式"。
    ///
    /// <para>
    /// 装完之后安装目录里会有一个 <c>Uninstall.exe</c>，它**就是这个程序** —— 同一个二进制，
    /// 靠自己的文件名决定走哪条路。这是常见做法（不必维护第二份窗口、第二份提权、第二份跑脚本的
    /// 代码），而且它在「应用和功能」里是一条正常的卸载项，双击就能卸。
    /// </para>
    /// </summary>
    private static bool IsUninstallMode =>
        Environment.ProcessPath is { } exe
        && Path.GetFileNameWithoutExtension(exe).StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase);

    /// <summary>装到哪 / 从哪卸。卸载模式下永远是"我自己所在的目录"。</summary>
    private static string ExeDir => AppContext.BaseDirectory;

    private static string InstallScript => Path.Combine(ExeDir, "install.ps1");
    private static string UninstallScript => Path.Combine(ExeDir, "uninstall.ps1");
    private static string PayloadDir => Path.Combine(ExeDir, "payload");

    private void ApplyMode()
    {
        if (!IsUninstallMode)
            return;

        ModeTitle.Text = "卸载 Sentinel";
        IntroText.Text = "把 Sentinel 从这台机器上卸掉：停掉并删除后台服务，结束正在跑的界面进程。";

        // 卸载不需要选位置 —— 它就在这个程序所在的目录。
        DirCard.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Collapsed;

        UninstallButton.Style = (Style)FindResource("PrimaryIconButton");
        UninstallButton.Content = "卸载";
        UninstallButton.Margin = new Thickness(0);

        PurgeCheck.Content = "连配置和日志一起删掉";
    }

    /// <summary>这一次要动的是哪个目录。</summary>
    private string InstallDir => IsUninstallMode ? ExeDir : DirBox.Text.Trim();

    // ------------------------------------------------------------------ 检测

    /// <summary>
    /// 猜一下这台机器上已经装在哪。
    ///
    /// <para>
    /// 猜的依据是标记文件，不是"目录存不存在" —— 一个空的 E:\Sentinel 什么都说明不了。
    /// 猜错了代价也不大：上面那个框是可以改的。
    /// </para>
    /// </summary>
    private void Detect()
    {
        if (IsUninstallMode)
        {
            // 卸载模式没什么可猜的：就是我自己待着的这个目录。
            DirBox.Text = ExeDir;
            RefreshState();
            return;
        }

        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sentinel");

        var candidates = new[] { programData, @"E:\Sentinel", @"D:\Sentinel" };

        var installed = candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, Marker)));

        DirBox.Text = installed ?? programData;

        RefreshState();
    }

    private void RefreshState()
    {
        var dir = InstallDir;
        var there = !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, Marker));

        if (_finished)
        {
            // 装完了就别再说"再点一次就是更新" —— 那时候该说的是"可以关掉了"。
            StatusText.Text = "装好了。托盘里会出现 Sentinel 的图标（后台服务负责把它拉起来）。";
            DirStateText.Text = "状态：已安装";
            UninstallButton.IsEnabled = !_running;
            return;
        }

        StatusText.Text = IsUninstallMode
            ? $"将从这个目录卸载：{dir}"
            : there
                ? "这台机器上已经装在这里。再点「安装 / 更新」就是更新 —— 配置会留着。"
                : "这个地方看起来还没装过。";

        DirStateText.Text = there ? "状态：已安装" : "状态：未安装";
        UninstallButton.IsEnabled = !_running && (there || IsUninstallMode);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = InstallDir;

        if (!Directory.Exists(dir))
        {
            Complain($"找不到 {dir}。");
            return;
        }

        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void Detect_Click(object sender, RoutedEventArgs e) => Detect();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择安装位置",
            InitialDirectory = Directory.Exists(DirBox.Text) ? DirBox.Text : null,
        };

        if (dialog.ShowDialog(this) == true)
        {
            DirBox.Text = dialog.FolderName;
            RefreshState();
        }
    }

    // ------------------------------------------------------------------ 安装 / 卸载

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        // 装完之后这个按钮就是「完成」—— 再点一下就关窗口。
        if (_finished)
        {
            Close();
            return;
        }

        var dir = InstallDir;

        if (!Ready(dir, out var problem))
        {
            Complain(problem);
            return;
        }

        var updating = File.Exists(Path.Combine(dir, Marker));

        SetRunning(true, updating ? $"正在更新 {dir} …" : $"正在安装到 {dir} …");
        Log($"=== {(updating ? "更新" : "安装")} {dir} ===");

        var code = await RunScriptAsync(InstallScript,
            ("-Source", PayloadDir),
            ("-InstallDir", dir),

            // 让脚本把我自己复制成安装目录下的 Uninstall.exe，并登记到「应用和功能」。
            // 卸载项是指向那个文件的，所以这一步不能少 —— 少了它，用户就只能翻出当初这个安装包
            // 才卸得掉。
            ("-SetupExe", Environment.ProcessPath ?? ""));

        SetRunning(false);

        if (code == 0)
        {
            _finished = true;

            InstallButton.Content = "完成";
            InstallButton.Tag = FindResource("IconCheck");
            UninstallButton.IsEnabled = false;
            DirBox.IsEnabled = false;
            BrowseButton.IsEnabled = false;
            DetectButton.IsEnabled = false;

            RefreshState();
            StepText.Text = "装好了。";

            Log("");
            Log("完成。托盘里会出现 Sentinel 的图标（后台服务负责把它拉起来）——");
            Log("如果没看到，注销一次再登录，或者看一眼「事件查看器 → Windows 日志 → 应用程序」。");
            Log("");
            Log("以后要卸载：Windows 的「设置 → 应用 → 已安装的应用」里找 Sentinel，");
            Log($"或者直接跑 {Path.Combine(dir, "Uninstall.exe")}。");
        }
        else
        {
            StepText.Text = "没有装完，看下面的过程。";
            Log("");
            Log($"退出码 {code} —— 上面最后几行通常就写着原因。");
            Complain($"安装没有完成（退出码 {code}）。\n\n下面「过程」里最后几行写着原因。", warning: true);
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var dir = InstallDir;

        if (!Directory.Exists(dir))
        {
            Complain($"找不到 {dir}。");
            return;
        }

        var purge = PurgeCheck.IsChecked == true;

        var what = purge
            ? "停止并删除 Sentinel 服务，然后**把配置和日志一起删掉**。"
            : "停止并删除 Sentinel 服务。配置和日志会留着。";

        var answer = MessageBox.Show(this,
            $"{what}\n\n位置：{dir}\n\n继续吗？",
            "卸载 Sentinel", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
            return;

        SetRunning(true, $"正在卸载 {dir} …");
        Log($"=== 卸载 {dir}{(purge ? "（连配置和日志）" : "")} ===");

        var code = purge
            ? await RunScriptAsync(UninstallScript, ("-InstallDir", dir), ("-Purge", null))
            : await RunScriptAsync(UninstallScript, ("-InstallDir", dir));

        SetRunning(false);
        RefreshState();

        StepText.Text = code == 0 ? "卸载完了。" : "没有卸干净，看下面的过程。";

        if (code != 0)
        {
            Complain($"卸载没有完成（退出码 {code}）。\n\n下面「过程」里最后几行写着原因。", warning: true);
            return;
        }

        // 从「应用和功能」点进来的（也就是这个程序本身就叫 Uninstall.exe）：卸完就关掉，
        // 别停在那一页让人以为还得再点一下。
        if (IsUninstallMode)
            Close();
    }

    /// <summary>
    /// 动手之前先自己看一眼那几个明显的错。
    ///
    /// <para>
    /// 脚本里也有校验（<c>Assert-SafeDirectory</c>），但让脚本去报错的话，错误会落在日志框里，
    /// 而这类错是"填错了框"，该在按下去之前就说。
    /// </para>
    /// </summary>
    private bool Ready(string dir, out string problem)
    {
        problem = "";

        if (string.IsNullOrWhiteSpace(dir))
        {
            problem = "先填一个安装位置。";
            return false;
        }

        if (!Path.IsPathFullyQualified(dir))
        {
            problem = "安装位置要是一个完整的路径，比如 E:\\Sentinel 。";
            return false;
        }

        if (string.Equals(Path.GetPathRoot(dir)?.TrimEnd('\\'), dir.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            problem = "别装到盘根上。给 Sentinel 一个自己的目录，比如 E:\\Sentinel 。";
            return false;
        }

        if (!File.Exists(InstallScript))
        {
            problem = $"找不到 {Path.GetFileName(InstallScript)} —— 它要和这个安装程序放在一起。"
                      + "只把 exe 单独拷出来的话是装不了的。";
            return false;
        }

        if (!Directory.Exists(PayloadDir))
        {
            problem = $"找不到 payload 目录 —— 程序和依赖在里面，它要和这个安装程序放在一起。";
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ 跑脚本

    private Task<int> RunScriptAsync(string script, params (string Name, string? Value)[] arguments) =>
        ScriptRunner.RunAsync(script, arguments, Log);

    private void SetRunning(bool running, string step = "")
    {
        _running = running;

        // 跑脚本的时候把输入都关掉：中途改路径会让日志和结果对不上，而那时候人最容易乱点。
        // 「完成」状态下除了主按钮（那时它是"关闭"）之外，别的也不该再能点。
        var open = !running && !_finished;

        InstallButton.IsEnabled = !running;
        UninstallButton.IsEnabled = open && (IsUninstallMode || File.Exists(Path.Combine(InstallDir, Marker)));
        BrowseButton.IsEnabled = open;
        DetectButton.IsEnabled = open;
        DirBox.IsEnabled = open;
        PurgeCheck.IsEnabled = !running;
        OpenFolderButton.IsEnabled = open;

        Bar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        StepText.Text = running ? step : StepText.Text;
        StatusBarText.Text = running ? "正在跑脚本…" : "就绪。";
    }

    private void Log(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void Complain(string message, bool warning = false) =>
        MessageBox.Show(this, message, "Sentinel 安装程序", MessageBoxButton.OK,
            warning ? MessageBoxImage.Warning : MessageBoxImage.Information);
}
