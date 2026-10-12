using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sentinel.Agent;
using Sentinel.Core.Models;

namespace Sentinel.Preview;

/// <summary>
/// Runs the real windows off the screen, captures each page, and exits.
///
/// <see cref="AgentState"/> is never <c>Start()</c>ed — that is the monitor, and starting it would
/// mean acting on the targets named in whatever config happens to be loaded. Reading that config is
/// all this needs.
/// </summary>
public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 控制台默认按系统代码页（中文 Windows 是 GBK）输出，而这个工具的输出被管道读成 UTF-8 ——
        // 不指定的话，中文全是乱码，而"看不懂输出"和"没有输出"一样没用。
        //
        // 用 SetOut 而不是 Console.OutputEncoding：输出被重定向到管道时，后者会因为拿不到控制台
        // 句柄直接抛异常，而前者不看句柄。
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))
        {
            AutoFlush = true,
        });

        // 另一件事：真的点一次「暂停」，看它有没有生效。
        if (e.Args.Length >= 2 && e.Args[0] == "--pause-check")
        {
            Shutdown(PauseCheck.Run(e.Args[1]));
            return;
        }

        // 改一个策略的提醒方式，看它到底什么时候被存下来。
        if (e.Args.Length >= 2 && e.Args[0] == "--policy-check")
        {
            Shutdown(PolicyCheck.Run(e.Args[1]));
            return;
        }

        // 解锁保护开着的时候，「停止监控」拦没拦住。
        if (e.Args.Length >= 2 && e.Args[0] == "--protection-check")
        {
            Shutdown(ProtectionCheck.Run(e.Args[1]));
            return;
        }

        // 改了表单切走会不会丢：脏提示认不认得出、点完应用表单还在不在。
        if (e.Args.Length >= 2 && e.Args[0] == "--dirty-check")
        {
            Shutdown(DirtyCheck.Run(e.Args[1]));
            return;
        }

        // 从 .NET 这一侧问一次接口，看是网络不通、地址不对、还是 key 不对。
        // 第 5 个参数给 "list" 就改去试「获取模型列表」那条路。
        if (e.Args.Length >= 2 && e.Args[0] == "--probe-check")
        {
            Shutdown(ProbeCheck.Run(
                e.Args[1],
                e.Args.Length > 2 ? e.Args[2] : "anthropic",
                e.Args.Length > 3 ? e.Args[3] : "",
                e.Args.Length > 4 ? e.Args[4] : "",
                e.Args.Length > 5 ? e.Args[5] : "judge"));
            return;
        }

        // 保存被拒之后，界面有没有回到"实际生效的样子"。
        if (e.Args.Length >= 2 && e.Args[0] == "--refuse-check")
        {
            Shutdown(RunCheck(() => RefuseCheck.Run(e.Args[1])));
            return;
        }

        // 看门狗被停掉会不会封印保险箱 —— 两个方向都要验（误判的代价很贵）。
        if (e.Args.Length >= 2 && e.Args[0] == "--watchdog-check")
        {
            Shutdown(RunCheck(() => WatchdogCheck.Run(e.Args[1])));
            return;
        }

        // 审计两份（本地 + 服务权威的那份）到底有没有落地。
        if (e.Args.Length >= 2 && e.Args[0] == "--audit-check")
        {
            Shutdown(RunCheck(() => AuditCheck.Run(e.Args[1])));
            return;
        }

        // 「服务结束不再心跳的 Agent」那件事 —— 它会杀进程，所以三道防线都要验。
        if (e.Args.Length >= 2 && e.Args[0] == "--kill-check")
        {
            Shutdown(RunCheck(() => KillCheck.Run(e.Args[1])));
            return;
        }

        // 安装程序的窗口建不建得起来（它的清单要管理员，启动不了，只能这样验）。
        if (e.Args.Length >= 2 && e.Args[0] == "--setup-check")
        {
            Shutdown(RunCheck(() => SetupCheck.Run(e.Args[1])));
            return;
        }

        // 按一次诊断页的「测一次模型监督」，把那报告原样打出来。
        if (e.Args.Length >= 2 && e.Args[0] == "--diag-check")
        {
            Shutdown(RunCheck(() => DiagCheck.Run(e.Args[1])));
            return;
        }

        // 模型监督端到端：拿你配置里的服务商，喂一个假的页面，看整条链会不会真的拦下来。
        if (e.Args.Length >= 1 && e.Args[0] == "--supervision-check")
        {
            Shutdown(RunCheck(SupervisionCheck.Run));
            return;
        }

        // 配置里有半成品服务商时，别的编辑还能不能存下去（用户被卡住的那个）。
        if (e.Args.Length >= 2 && e.Args[0] == "--save-check")
        {
            Shutdown(RunCheck(() => SaveCheck.Run(e.Args[1])));
            return;
        }

        // 新建服务商的流程：不立刻保存、点取消不留半成品、自动补全地址。
        if (e.Args.Length >= 2 && e.Args[0] == "--newmodel-check")
        {
            // 两道隔离，缺一不可 —— 这个检查**会触发保存**，写错地方就是改用户的真配置：
            //
            //   管道名：不隔离就会连上用户正在跑的那个 Service。配置的主人是 Service，
            //           保存请求经它写下去，写的是**它的**配置文件。
            //   --config：不隔离的话，找不到服务时它会自己写默认位置（%ProgramData% 或用户目录）。
            Environment.SetEnvironmentVariable("SENTINEL_PIPE", "Sentinel.Test.NewModelCheck.Unused");

            Directory.CreateDirectory(e.Args[1]);
            var configPath = Path.Combine(e.Args[1], "config.json");
            NewModelCheck.WriteConfig(configPath);

            using var demoState = new AgentState(new[] { "--config", configPath });

            // 最后一道保险：确认手上这份确实是本检查自己写的。光设管道名只是"希望"没人接。
            if (demoState.Config.Models.Count != 1 || demoState.Config.Models[0].Id != "m1")
            {
                Console.WriteLine("读到的不是本检查自己写的配置 —— 可能连上了别的服务，停手。");
                Shutdown(1);
                return;
            }

            var demoWindow = new MainWindow(demoState);
            Offscreen(demoWindow, 1180, 800);
            demoWindow.Show();
            Pump();

            Shutdown(NewModelCheck.Run(demoWindow, demoState, e.Args[1]));
            return;
        }

        // 「大语言模型」这一页整条路：添加 → 填 → 取列表 → 保存 → 回到列表。
        if (e.Args.Length >= 2 && e.Args[0] == "--models-check")
        {
            Shutdown(ModelCheck.Run(e.Args[1]));
            return;
        }

        // 把全屏提醒真的弹一次，量它有没有盖住屏幕。
        if (e.Args.Length >= 1 && e.Args[0] == "--alert-check")
        {
            Shutdown(AlertCheck.Run());
            return;
        }

        var outDir = e.Args.Length > 0
            ? e.Args[0]
            : Path.Combine(Path.GetTempPath(), "sentinel-preview");

        Directory.CreateDirectory(outDir);

        using var state = new AgentState(Array.Empty<string>());

        // 「大语言模型」页签在真实配置里通常是空的（那是要你自己填的），空列表就看不出版面。
        // 塞两个示例进去 —— **只在内存里，不保存**，这个预览从不写配置。
        // 要在建窗口之前塞：列表是窗口 Loaded 时从配置生成的。
        if (state.Config.Models.Count == 0)
        {
            state.Config.Models.Add(new Sentinel.Core.Models.ModelProvider
            {
                Id = "model-demo",
                Name = "DeepSeek",
                Format = Sentinel.Core.Models.ApiFormat.Anthropic,
                Endpoint = "https://api.deepseek.com/anthropic/v1/messages",
                Model = "deepseek-flash",
                ApiKey = "sk-example-key",
            });

            state.Config.Models.Add(new Sentinel.Core.Models.ModelProvider
            {
                Id = "model-demo-2",
                Name = "本地中转",
                Format = Sentinel.Core.Models.ApiFormat.OpenAi,
                Endpoint = "https://relay.example.com/v1/chat/completions",
                Model = "gpt-4o-mini",
                ApiKey = "sk-example-key-2",
            });

            state.Config.ActiveModelId = "model-demo";
        }

        var window = new MainWindow(state);
        Offscreen(window, 1180, 800);
        window.Show();

        // 空框看不出"输入了但看不见"这类问题 —— 填上你实际会填的值再截。
        if (window.FindName("TargetList") is ListBox list && list.Items.Count > 0)
            list.SelectedIndex = 0;
        Pump();

        foreach (var (name, text) in new[]
                 {
                     ("TargetNameBox", "Codex"), ("TargetProcessBox", "codex"),
                     ("IntervalBox", "1000"), ("TargetPauseDaysBox", "3"),
                     ("TargetPauseMinutesBox", "15"),
                 })
        {
            if (window.FindName(name) is TextBox box)
                box.Text = text;
        }
        Pump();

        var pages = new[]
        {
            "01-overview", "02-vault", "03-targets", "04-rules",
            "05-policies", "06-schedules", "07-models", "08-logs", "09-diagnose",
        };

        for (var i = 0; i < pages.Length; i++)
        {
            window.SelectTab(i);
            Capture(window, Path.Combine(outDir, pages[i] + ".png"));
        }

        // 量出来的实际尺寸，比在截图上目测可靠 —— 输入框到底多高、是不是被内容撑开了，看这里。
        // 停在「目标」页量：那里的控件种类最全。
        window.SelectTab(2);
        Pump();
        Measure(window);

        // 「开启解锁保护」的面板默认收着，点一下再截一张 —— 收着的面板等于没做过验证。
        window.SelectTab(0);
        Pump();
        if (window.FindName("EnableButton") is System.Windows.Controls.Primitives.ButtonBase enable)
        {
            enable.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            Capture(window, Path.Combine(outDir, "13-enable-panel.png"));
        }

        // 大语言模型：列表页截完，再点一次第一行的「编辑」把编辑页也截下来 ——
        // 编辑页是整页切换过来的，只看列表页等于有一半没看过。
        window.SelectTab(6);
        Pump();
        if (FirstActionButton(window, "编辑") is { } edit)
        {
            edit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            Capture(window, Path.Combine(outDir, "15-model-edit.png"));

            // 编辑页比一屏长，只截顶上等于「模型」那一块和三个按钮都没看过。
            if (window.FindName("ModelEditView") is ScrollViewer editor)
            {
                editor.ScrollToEnd();
                Pump();
                Capture(window, Path.Combine(outDir, "16-model-edit-bottom.png"));
            }
        }

        // 规则页的「模型监督」形态：类型一换，下面那个框的标签和提示都该跟着换。
        // 不换的话，人会以为那里要填关键词，然后填进去一个永远不命中的词。
        window.SelectTab(3);
        Pump();

        if (window.FindName("RuleList") is ListBox ruleList && ruleList.Items.Count > 0)
        {
            ruleList.SelectedIndex = 0;
            Pump();

            if (window.FindName("RuleTypeBox") is ComboBox typeBox
                && typeBox.Items.OfType<Choice<MatchKind>>().FirstOrDefault(c => c.Value == MatchKind.Model)
                    is { } modelChoice)
            {
                typeBox.SelectedItem = modelChoice;
                Pump();
                Capture(window, Path.Combine(outDir, "14-rule-model.png"));
            }
        }

        window.Close();

        // 把每个页面在最窄窗口下再走一遍。控件被挤出可视区这种毛病只在窄窗口下露出来，
        // 而且不报错、没有滚动条 —— 只是"东西不见了"，最不容易自己发现。
        var narrow = new MainWindow(state);
        Offscreen(narrow, 860, 640);   // 860 是 MainWindow 的 MinWidth
        narrow.Show();

        for (var i = 0; i < pages.Length; i++)
        {
            narrow.SelectTab(i);
            Capture(narrow, Path.Combine(outDir, "n-" + pages[i] + ".png"));
        }

        narrow.Close();

        // 再把每个页面在宽窗口下走一遍。
        var wide = new MainWindow(state);
        Offscreen(wide, 1680, 900);
        wide.Show();

        for (var i = 0; i < pages.Length; i++)
        {
            wide.SelectTab(i);
            Capture(wide, Path.Combine(outDir, "w-" + pages[i] + ".png"));
        }

        wide.Close();

        // 保险箱：四个状态各自是一整页，一次只露一个。每个状态都在默认尺寸和最窄尺寸下各截一张。
        var vault = new VaultWindow(state);
        Offscreen(vault, 980, 720);
        vault.Show();

        var states = new[]
        {
            ("setup", "SetupPanel"),
            ("locked", "LockedPanel"),
            ("sealed", "SealedPanel"),
            ("unlocked", "UnlockedPanel"),
        };

        foreach (var width in new[] { 980, 760 })
        {
            vault.Width = width;
            vault.Height = 720;

            foreach (var (label, name) in states)
            {
                ShowOnly(vault, name);
                Pump();
                Capture(vault, Path.Combine(outDir, $"vault-{label}-{width}.png"));
            }
        }

        Measure(vault);
        ProbeHit(vault);

        vault.Close();

        Console.WriteLine(outDir);
        Shutdown();
    }

    /// <summary>在服务商列表里找第一个写着某个字的按钮（那一行上的「编辑」「测试」「删除」）。</summary>
    private static System.Windows.Controls.Primitives.ButtonBase? FirstActionButton(DependencyObject root, string text)
    {
        if (root is System.Windows.Controls.Primitives.ButtonBase b && b.Content is string s && s == text)
            return b;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FirstActionButton(VisualTreeHelper.GetChild(root, i), text);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// 保险箱的四个状态叠在同一个格子里，一次只露一个。预览里没有密码，切不过去，所以直接改可见性 ——
    /// 布局（工具栏换不换行、列宽跟不跟窗口走）和真解锁时是同一套。
    /// </summary>
    private static void ShowOnly(Window window, string name)
    {
        foreach (var other in new[] { "SetupPanel", "LockedPanel", "SealedPanel", "UnlockedPanel" })
        {
            if (window.FindName(other) is FrameworkElement element)
                element.Visibility = other == name ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 跑一个检查，异常也不许把进程留下。
    ///
    /// <para>
    /// 检查抛异常时，WPF 的消息循环还开着 —— 进程就一直挂着不走（跑的人只看到"卡住了"，
    /// 而真正的错在哪一行被埋在几百行堆栈里）。所以这里把异常打印到开头就结束。
    /// </para>
    /// </summary>
    private static int RunCheck(Func<int> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"检查抛异常了：{ex.GetType().Name}");
            Console.WriteLine(ex.Message);
            Console.WriteLine();
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Off every monitor, and not stealing focus. Shown rather than merely measured because a
    /// TabControl only builds the page it has selected — an unshown window has no selected page to
    /// build.
    /// </summary>
    private static void Offscreen(Window window, int width, int height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Width = width;
        window.Height = height;
    }

    private static void Capture(Window window, string path)
    {
        Pump();

        if (window.Content is not FrameworkElement root)
            throw new InvalidOperationException($"{window.GetType().Name} 没有内容可以截。");

        // The OS title bar is not part of the visual tree, so rendering the Window itself would leave
        // its strip blank. The content element is exactly the client area — but a root panel with no
        // background of its own would come out transparent, so it borrows the window's.
        if (root is Panel panel && panel.Background is null)
            panel.Background = window.Background ?? Brushes.White;

        root.UpdateLayout();

        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"{window.GetType().Name} 没有量出尺寸。");

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var file = File.Create(path);
        encoder.Save(file);

        Console.WriteLine($"{Path.GetFileName(path),-24} {width}×{height}");
    }

    /// <summary>
    /// Switches tabs, applying templates and laying out are all queued at lower dispatcher
    /// priorities. Capturing before that queue drains gets a half-built page — or the previous one.
    /// </summary>
    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// 打印几个坐标上的命中元素。
    ///
    /// <para>
    /// 滚轮是问"鼠标底下是谁"，再顺着可视树上浮。所以一个没有 Background 的容器，即使铺满整片区域，
    /// 命中也穿过它 —— 那一片就滑不动。这个探针用来区分"滚动条坏了"和"根本没接到滚轮"。
    /// </para>
    /// </summary>
    private static void ProbeHit(Window window)
    {
        if (window.Content is not FrameworkElement root)
            return;

        Console.WriteLine();
        Console.WriteLine($"{"位置",-12}{"x",7}{"y",7}  {"命中元素",-22}{"名字"}");

        foreach (var (label, fx, fy) in new[]
                 {
                     ("左上", 0.03, 0.15), ("正文中间", 0.35, 0.50),
                     ("右侧空白", 0.90, 0.50), ("底部", 0.35, 0.92),
                 })
        {
            var x = root.ActualWidth * fx;
            var y = root.ActualHeight * fy;
            var hit = VisualTreeHelper.HitTest(root, new Point(x, y))?.VisualHit as FrameworkElement;

            Console.WriteLine($"{label,-12}{x,7:0}{y,7:0}  {hit?.GetType().Name ?? "null",-22}{hit?.Name}");
        }
    }

    /// <summary>Prints the laid-out size of the controls people actually judge by eye.</summary>
    private static void Measure(Window window)
    {
        Console.WriteLine();
        Console.WriteLine($"── {window.GetType().Name} ──");
        Console.WriteLine($"{"控件",-22}{"类型",-14}{"字号",7}{"宽",7}{"高",7}");

        foreach (var element in Descendants(window.Content))
        {
            if (string.IsNullOrEmpty(element.Name))
                continue;

            var size = element is Control c ? c.FontSize : 0;
            Console.WriteLine(
                $"{element.Name,-22}{element.GetType().Name,-14}{size,7:0.#}{element.ActualWidth,7:0}{element.ActualHeight,7:0}");
        }

        // 滚轮能不能滚，取决于 ScrollableHeight 是不是大于 0 —— 光看截图看不出来，
        // 内容恰好放得下的时候，界面和"滚动坏了"长得一模一样。
        Console.WriteLine();
        Console.WriteLine($"{"ScrollViewer",-22}{"实际高",8}{"内容高",8}{"可视高",8}{"竖条",10}{"横条",10}");

        foreach (var viewer in Descendants(window.Content).OfType<ScrollViewer>())
        {
            var name = string.IsNullOrEmpty(viewer.Name) ? "(匿名)" : viewer.Name;
            Console.WriteLine(
                $"{name,-22}{viewer.ActualHeight,8:0}{viewer.ExtentHeight,8:0}{viewer.ViewportHeight,8:0}" +
                $"{viewer.ComputedVerticalScrollBarVisibility,10}{viewer.ComputedHorizontalScrollBarVisibility,10}");
        }
    }

    private static IEnumerable<FrameworkElement> Descendants(object? root)
    {
        if (root is not DependencyObject node)
            yield break;

        if (node is FrameworkElement element)
            yield return element;

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i)))
                yield return child;
        }
    }
}
