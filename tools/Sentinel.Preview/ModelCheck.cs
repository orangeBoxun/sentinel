using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Sentinel.Agent;
using Sentinel.Core.Config;
using Sentinel.Core.Models;

namespace Sentinel.Preview;

/// <summary>
/// 「大语言模型」这一页整条路走一遍：添加服务商 → 编辑页填东西 → 取模型列表 → 保存 → 回到列表。
///
/// <para>
/// 起因是这一页被整个重做过（照 cc-switch 那套排版）。重做之后光看截图只能看出"排得下"，看不出
/// "填进去的东西还在不在"—— 可编辑下拉框会把 Text 悄悄抹掉、保存之后行上的徽章会不会跟着变，
/// 这些都是截图上看不出来、点一遍才知道的事。
/// </para>
/// <para>
/// 不启服务：Agent 找不到服务时会直接写文件，正好用来单独看"界面 → 配置"这一段。
/// 管道名和配置文件都隔离，绝不碰用户装好的那一套。
/// </para>
/// </summary>
internal static class ModelCheck
{
    public static int Run(string workDir)
    {
        Environment.SetEnvironmentVariable("SENTINEL_PIPE", "Sentinel.Test.ModelCheck.Unused");

        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");
        WriteConfig(configPath);

        using var state = new AgentState(new[] { "--config", configPath });

        if (state.Config.Models.Count != 1 || state.Config.Models[0].Id != "m1")
        {
            Console.WriteLine("读到的不是本检查自己写的配置 —— 可能连上了别的服务，停手。");
            return 1;
        }

        var window = new MainWindow(state)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1180,
            Height = 800,
        };
        // 静默返回已经改成弹窗了：这个检查点按钮时不一定会选中条目，弹真框会把检查挂死。
        window.RefuseToUser = message => Console.WriteLine($"[被拒] {message.Split(char.MinValue)[0]}");


        window.Show();
        Pump();
        window.SelectTab(6);
        Pump();

        var ok = true;

        // ---- 一、列表页：已有的那个服务商应该就在上面，并且标着"使用中"
        var list = (ItemsControl)window.FindName("ModelList")!;
        var rows = ItemsOf(list).ToList();
        Console.WriteLine($"列表里 {rows.Count} 行：{string.Join(" / ", rows.Select(Describe))}");
        ok &= rows.Count == 1 && rows[0].IsActive;

        // ---- 二、点「添加服务商」→ 应该切到编辑页
        Click(window, "添加服务商");
        Pump();

        var editor = (FrameworkElement)window.FindName("ModelEditView")!;
        var listView = (FrameworkElement)window.FindName("ModelListView")!;
        Console.WriteLine($"点过「添加服务商」: 编辑页={editor.Visibility}  列表页={listView.Visibility}");
        ok &= editor.Visibility == Visibility.Visible && listView.Visibility == Visibility.Collapsed;

        // ---- 三、填进去，然后用「获取模型列表」试一次网络。
        //         这里故意填一个不存在的中转站：要验的是**取不到的时候说清楚了没有**，
        //         而不是真去调谁家的接口（那要花用户的钱，也说明不了界面的事）。
        SetText(window, "ModelNameBox", "示例中转");
        SetText(window, "ModelNoteBox", "自己看的备注");
        SetText(window, "ModelEndpointBox", "https://nothing.invalid/v1/chat/completions");
        SetText(window, "ModelCodePicker", "some-model");
        Pump();

        var listUrl = (TextBox)window.FindName("ModelListEndpointBox")!;
        Console.WriteLine($"模型列表地址自动带出: 「{listUrl.Text}」");
        ok &= listUrl.Text.Length > 0;

        // 地址和格式对不上（这里的格式是 OpenAI，地址却是 Anthropic 那条路径）要提醒一句 ——
        // 两边单独看都没问题，合在一起就是一个 404，而服务端只会说"请求不对"。
        SetText(window, "ModelEndpointBox", "https://nothing.invalid/anthropic/v1/messages");
        Pump();

        var endpointHint = ((TextBlock)window.FindName("ModelEndpointHint")!).Text;
        Console.WriteLine($"地址与格式对不上时: {endpointHint.Replace("\n", " / ")}");
        ok &= endpointHint.Contains("这条地址里没有");

        Click(window, "获取模型列表");
        WaitForHint(window, TimeSpan.FromSeconds(20));

        var hint = ((TextBlock)window.FindName("ModelListHint")!).Text;
        Console.WriteLine($"取不到时说的是     : {hint}");
        ok &= hint.Contains("nothing.invalid") || hint.Contains("连不上");

        // 取不到不该把已经填着的模型名弄丢。
        var code = ((ComboBox)window.FindName("ModelCodePicker")!).Text;
        Console.WriteLine($"取过之后模型名还是  : 「{code}」");
        ok &= code == "some-model";

        // ---- 四、保存（是否参与判断在「判断链」页里排，不在编辑页）
        ((CheckBox)window.FindName("ModelStableCheck")!).IsChecked = true;
        Pump();

        Click(window, "保存");
        Pump();

        Console.WriteLine();
        Console.WriteLine($"点过「保存」之后");
        Console.WriteLine($"  编辑页   : {editor.Visibility}（Collapsed = 已经回到列表）");
        Console.WriteLine($"  列表行   : {string.Join(" / ", ItemsOf(list).Select(Describe))}");
        Console.WriteLine($"  磁盘上是 : {string.Join(" / ", OnDisk(configPath).Models.Select(m => $"{m.Name}|{m.Model}|{m.Endpoint}"))}");
        Console.WriteLine($"  判断链   : {string.Join(" → ", OnDisk(configPath).ModelChain)}（首位 {OnDisk(configPath).ActiveModelId}）");

        var onDisk = OnDisk(configPath);
        var saved = onDisk.Models.FirstOrDefault(m => m.Name == "示例中转");

        // 把它加入判断链（界面上是「启用新供应商…」那个弹窗，这里直接调同一个方法）。
        if (saved is not null)
        {
            window.AddProvidersToChain(new[] { saved.Id });
            Pump();
            onDisk = OnDisk(configPath);
        }

        // 勾"参与判断"= 加入判断链（排在后面），不再抢唯一的位置：
        // ActiveModelId 跟着链的首位走，所以还是原来那个。
        ok &= editor.Visibility == Visibility.Collapsed
              && saved is { Model: "some-model" }
              && onDisk.ModelChain.SequenceEqual(new[] { "m1", saved!.Id })
              && onDisk.ActiveModelId == "m1";

        // ---- 五、编辑已有的那个：可编辑下拉框里的模型名要能看见
        //         （这类框有个坑：先填 Text 再把 ItemsSource 清空，Text 会被一起抹掉）
        Click(window, "编辑");
        Pump();

        var loaded = ((ComboBox)window.FindName("ModelCodePicker")!).Text;
        Console.WriteLine();
        Console.WriteLine($"打开已有的服务商，模型名框里是: 「{loaded}」");
        ok &= loaded == "deepseek-flash";

        // ---- 六、「判断链」页：切过去，看它画出来没有、内容对不对
        ((RadioButton)window.FindName("ModelTabChain")!).IsChecked = true;
        Pump();

        var chainList = (ItemsControl)window.FindName("ChainList")!;

        // 拖拽影子的两个前提：拿得到 adorner 层、那一行的快照不是空白
        var adornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(chainList);
        Console.WriteLine($"adorner 层 : {(adornerLayer is null ? "null（影子画不出来）" : "有")}");

        if (chainList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement firstRow)
        {
            var w = Math.Max(1, (int)Math.Ceiling(firstRow.ActualWidth));
            var h = Math.Max(1, (int)Math.Ceiling(firstRow.ActualHeight));
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(firstRow);

            var pixels = new byte[4];
            shot.CopyPixels(new System.Windows.Int32Rect(w / 2, h / 2, 1, 1), pixels, 4, 0);

            Console.WriteLine($"行快照     : {w}x{h}  中心像素 alpha={pixels[3]}  RGB=({pixels[2]},{pixels[1]},{pixels[0]})");

            // 影子必须画在"被要求的位置"上，而不是停在起点（那就等于没有跟着鼠标走）
            if (adornerLayer is not null)
            {
                var ghost = new DragGhostAdorner(chainList, firstRow);
                adornerLayer.Add(ghost);
                ghost.MoveTo(24, 40);
                adornerLayer.UpdateLayout();

                var layerWidth = Math.Max(1, (int)Math.Ceiling(adornerLayer.ActualWidth));
                var layerHeight = Math.Max(1, (int)Math.Ceiling(adornerLayer.ActualHeight));
                var probe = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    layerWidth, layerHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                probe.Render(adornerLayer);

                var atTarget = new byte[4];
                probe.CopyPixels(new System.Windows.Int32Rect(28, 44, 1, 1), atTarget, 4, 0);

                Console.WriteLine($"影子像素   : 在 (24,40) 处 alpha={atTarget[3]}（>0 = 画在那里了）");
                ok &= atTarget[3] > 0;

                adornerLayer.Remove(ghost);
            }
        }
        var chainRows = chainList.Items.Count;
        var chainStatus = ((TextBlock)window.FindName("ModelChainStatus")!).Text;

        Console.WriteLine();
        Console.WriteLine($"判断链页   : {chainRows} 行 / 状态「{chainStatus}」");

        ok &= chainRows == 2;
        ok &= chainStatus.Contains("2 个供应商");

        // 换一下顺序（拖拽落盘走的是同一段逻辑），再确认真的存下去了
        var beforeOrder = OnDisk(configPath).ModelChain.ToList();
        window.ReorderChain(0, 1);
        Pump();

        var afterOrder = OnDisk(configPath).ModelChain.ToList();
        Console.WriteLine($"调换顺序   : {string.Join(" → ", beforeOrder)}  ⇒  {string.Join(" → ", afterOrder)}");
        ok &= afterOrder.Count == 2 && afterOrder[0] == beforeOrder[1] && afterOrder[1] == beforeOrder[0];

        // 「用量」页也要能画出来
        ((RadioButton)window.FindName("ModelTabUsage")!).IsChecked = true;
        Pump();

        var usageStatus = ((TextBlock)window.FindName("ModelUsageStatus")!).Text;
        Console.WriteLine($"用量页     : {usageStatus}");

        ok &= usageStatus.Length > 0;

        // 切回「所有供应商」，确认来回切不炸
        ((RadioButton)window.FindName("ModelTabProviders")!).IsChecked = true;
        Pump();
        ok &= ((ItemsControl)window.FindName("ModelList")!).Items.Count == 2;

        window.Close();

        Console.WriteLine();
        Console.WriteLine(ok ? "结论：这一页整条路是通的。" : "结论：有一处不对，见上面。");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 等「获取模型列表」那一次网络请求回来。
    ///
    /// 它是个 async void 处理函数，点完之后消息队列抽干也不代表跑完了 —— 得等那行提示不再是"正在取"。
    /// </summary>
    private static void WaitForHint(Window window, TimeSpan timeout)
    {
        var hint = (TextBlock)window.FindName("ModelListHint")!;
        var end = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < end)
        {
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            if (!hint.Text.StartsWith("正在取", StringComparison.Ordinal))
                return;

            Thread.Sleep(100);
        }
    }

    private static string Describe(ModelRow row) =>
        $"{row.Name}{(row.IsActive ? "[使用中]" : "[未启用]")}";

    private static IEnumerable<ModelRow> ItemsOf(ItemsControl items) =>
        items.Items.OfType<ModelRow>();

    private static void SetText(Window window, string name, string text)
    {
        if (window.FindName(name) is TextBox box)
            box.Text = text;
        else if (window.FindName(name) is ComboBox combo)
            combo.Text = text;
    }

    /// <summary>按按钮上的字找它，然后点一下 —— 和用户做的是同一件事。</summary>
    private static void Click(Window window, string text)
    {
        var button = Find(window, text) ?? throw new InvalidOperationException($"找不到写着「{text}」的按钮。");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static ButtonBase? Find(DependencyObject root, string text)
    {
        if (root is ButtonBase b && b.Content is string s && s == text)
            return b;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = Find(System.Windows.Media.VisualTreeHelper.GetChild(root, i), text);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static AppConfig OnDisk(string configPath) => ConfigStore.LoadOrDefault(configPath);

    private static void WriteConfig(string path)
    {
        var config = new AppConfig();
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        config.Models.Add(new ModelProvider
        {
            Id = "m1",
            Name = "DeepSeek",
            Format = ApiFormat.Anthropic,
            Endpoint = "https://api.deepseek.com/anthropic/v1/messages",
            Model = "deepseek-flash",
            ApiKey = "sk-old-key",
        });

        config.ActiveModelId = "m1";

        ConfigStore.Save(config, path);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}
