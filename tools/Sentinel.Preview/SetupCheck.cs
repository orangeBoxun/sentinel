using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sentinel.Preview;

/// <summary>
/// 把**安装程序**的窗口在进程里渲染一次。
///
/// <para>
/// 为什么不能只是"编译过了"：XAML 里引用一个不存在的资源键（StaticResource）是在**运行时**
/// 才炸的，编译期一声不吭 —— 这轮已经栽过两次。而安装程序没法直接启动来验：
/// 它的清单要求管理员，一启动就是 UAC 弹窗。在进程里构造它的窗口不提权，所以用这个办法。
/// </para>
/// <para>
/// 顺带检查那几件"没装过也该对"的事：默认目录填上了、按钮可用、日志框在。
/// </para>
/// </summary>
internal static class SetupCheck
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // 先看一眼应用级资源在不在 —— XAML 报的是"某个标记扩展炸了"，不打印的话分不清是
        // 样式没找到还是图标路径不对。
        var app = Application.Current;
        Console.WriteLine($"应用资源里有 AppWindow 吗: {app?.Resources.Contains("AppWindow")}");
        Console.WriteLine($"应用资源里有 KnotBand 吗 : {app?.Resources.Contains("KnotBand")}");
        Console.WriteLine();

        var window = new Sentinel.Setup.MainWindow();

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Width = 760;
        window.Height = 640;

        window.Show();
        Pump();

        var path = Path.Combine(outDir, "setup.png");

        if (window.Content is not FrameworkElement root)
        {
            Console.WriteLine("安装程序的窗口里没有内容 —— 它的 XAML 没建起来。");
            return 1;
        }

        if (root is Panel panel && panel.Background is null)
            panel.Background = window.Background ?? Brushes.White;

        root.UpdateLayout();

        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var file = File.Create(path))
            encoder.Save(file);

        Console.WriteLine($"渲染成功：{path}（{width}×{height}）");
        Console.WriteLine();

        var ok = true;

        void Report(string what, bool good, string detail)
        {
            Console.WriteLine($"{(good ? "✓" : "✗")} {what}：{detail}");
            ok &= good;
        }

        var dirBox = window.FindName("DirBox") as TextBox;
        var install = window.FindName("InstallButton") as Button;
        var uninstall = window.FindName("UninstallButton") as Button;
        var log = window.FindName("LogBox") as TextBox;
        var state = window.FindName("DirStateText") as TextBlock;

        Report("安装位置输入框", dirBox is not null && dirBox.Text.Length > 0,
            dirBox is null ? "找不到" : $"默认填的是「{dirBox.Text}」");

        Report("安装按钮", install is { IsEnabled: true }, install is null ? "找不到" : "可用");

        Report("卸载按钮", uninstall is not null,
            uninstall is null ? "找不到" : uninstall.IsEnabled ? "可用（检测到已安装）" : "灰着（这个位置没装过，对）");

        Report("过程日志框", log is not null, log is null ? "找不到" : $"高度 {log.ActualHeight:0} 像素");

        Report("状态行", state is not null, state?.Text ?? "找不到");

        window.Close();

        // ---- App.xaml 有没有挂上启动钩子
        //
        // 真的踩过一次：App.xaml 里既没有 StartupUri 也没有 Startup，于是双击之后
        // **进程活着、不报错、没有窗口** —— WPF 起来之后消息循环空转，什么都不做。
        // 上面那些检查抓不到它，因为它们是直接构造 MainWindow 的，根本不走 App.OnStartup。
        // 这个文件不长，读一眼就够。
        var appXaml = Path.Combine(RepoRoot(), "src", "Sentinel.Setup", "App.xaml");
        var wired = File.Exists(appXaml)
                    && (File.ReadAllText(appXaml).Contains("Startup=")
                        || File.ReadAllText(appXaml).Contains("StartupUri="));

        Report("App.xaml 的启动钩子", wired,
            wired ? "挂上了" : "既没有 Startup 也没有 StartupUri —— 双击会没有任何反应");

        // ---- 卸载项的接线
        //
        // "装完之后能在「设置 → 应用」里卸载"靠的是两处脚本：install.ps1 放好 Uninstall.exe
        // 并写注册表，uninstall.ps1 把那条删掉。少任何一半，用户就会遇到"装得上、卸不掉"或者
        // "卸载项永远留在系统里"。这两个都是静默失效，所以在这儿盯一眼。
        var installScript = Path.Combine(RepoRoot(), "installer", "install.ps1");
        var uninstallScript = Path.Combine(RepoRoot(), "installer", "uninstall.ps1");

        var installText = File.Exists(installScript) ? File.ReadAllText(installScript) : "";
        var uninstallText = File.Exists(uninstallScript) ? File.ReadAllText(uninstallScript) : "";

        const string key = @"Microsoft\Windows\CurrentVersion\Uninstall\Sentinel";

        Report("install.ps1 收得下安装程序自己", installText.Contains("-SetupExe"),
            "有 -SetupExe 参数");

        Report("install.ps1 登记卸载项", installText.Contains(key) && installText.Contains("Uninstall.exe"),
            "写 Uninstall 注册表项 + 复制 Uninstall.exe");

        Report("uninstall.ps1 注销卸载项", uninstallText.Contains(key),
            uninstallText.Contains(key) ? "会删掉那条" : "**没删** —— 卸载项会永远留着");

        // ---- 参数拼装（唯一能在这台机器上真跑的一段）
        //
        // 安装程序自己一启动就要管理员，点不下去；但"往命令行里塞什么"是可以拿一个假脚本验的。
        // 开关型参数最容易错：值为空时如果不跳过，PowerShell 会把它当位置参数，
        // 于是卸载时 -Purge 变成了 -InstallDir 的值 —— 那种错只有真的删掉用户数据时才发现。
        var script = Path.Combine(outDir, "args-test.ps1");

        File.WriteAllText(script, """
            param([string]$InstallDir, [string]$Source, [switch]$Purge)
            "got: InstallDir=[$InstallDir] Source=[$Source] Purge=[$Purge]"
            """);

        var lines = new List<string>();

        var code = Sentinel.Setup.ScriptRunner
            .RunAsync(script, new[]
            {
                ("-Source", @"E:\payload"),
                ("-InstallDir", @"E:\Sentinel"),
                ("-Purge", (string?)null),
            }, lines.Add)
            .GetAwaiter().GetResult();

        var got = lines.FirstOrDefault(l => l.Contains("got:")) ?? "(没有输出)";

        Console.WriteLine();
        Report("脚本调用的参数", code == 0 && got.Contains(@"InstallDir=[E:\Sentinel]")
                                        && got.Contains(@"Source=[E:\payload]")
                                        && got.Contains("Purge=[True]"),
            got);

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论：安装程序的界面建得起来，控件都在，脚本调用的参数也对。"
            : "结论：有一样不对，见上面。");

        return ok ? 0 : 1;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>从运行目录往上找到仓库根（有 Sentinel.sln 的那一层）。</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "Sentinel.sln")))
                return dir;
        }

        return AppContext.BaseDirectory;
    }
}
