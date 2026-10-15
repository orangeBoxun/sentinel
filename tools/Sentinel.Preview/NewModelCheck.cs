using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sentinel.Agent;
using Sentinel.Core;

namespace Sentinel.Preview;

/// <summary>
/// 验证"新建服务商"的流程：不立刻保存、点取消不留半成品、自动补全地址。
///
/// <para>
/// 起因是一个 bug：点「添加服务商」立刻弹窗说"配置没有保存：没有填请求地址 / 没有填模型名"，
/// 而用户还没来得及填。根因是 <c>NewModel_Click</c> 在 1546 行直接 <c>SaveAndReport()</c>，
/// 保存了一个半成品；点"取消"时，这个半成品留在配置文件里，下次打开还会报错。
/// </para>
/// </summary>
internal static class NewModelCheck
{
    public static int Run(MainWindow window, AgentState state, string outDir)
    {
        Console.WriteLine("=== 新建服务商的流程 ===\n");
        var ok = true;

        var addButton = (Button)window.FindName("NewModelButton")!;
        var cancelButton = (Button)window.FindName("CancelModelButton")!;
        var editView = (FrameworkElement)window.FindName("ModelEditView")!;
        var listView = (FrameworkElement)window.FindName("ModelListView")!;

        // 初始状态：列表里有一个 DeepSeek。
        var initialCount = state.Config.Models.Count;
        Console.WriteLine($"初始状态：列表里 {initialCount} 个服务商");
        ok &= initialCount == 1;

        // 点「添加服务商」：切到编辑页，但**不保存**。
        addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();

        Console.WriteLine($"点过「添加服务商」: 编辑页={editView.Visibility}  列表页={listView.Visibility}");
        ok &= editView.Visibility == Visibility.Visible;
        ok &= listView.Visibility == Visibility.Collapsed;

        var countAfterAdd = state.Config.Models.Count;
        Console.WriteLine($"配置里现在有 {countAfterAdd} 个服务商（{(countAfterAdd == initialCount ? "没变" : "变了")}）");
        ok &= countAfterAdd == initialCount;  // 新建时不保存，所以列表还是 1 个

        // 点「取消」：回到列表页，半成品不留。
        cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();

        Console.WriteLine($"点过「取消」: 编辑页={editView.Visibility}  列表页={listView.Visibility}");
        ok &= editView.Visibility == Visibility.Collapsed;
        ok &= listView.Visibility == Visibility.Visible;

        var countAfterCancel = state.Config.Models.Count;
        Console.WriteLine($"配置里现在有 {countAfterCancel} 个服务商（半成品{(countAfterCancel == initialCount ? "没留下" : "留下了")}）");
        ok &= countAfterCancel == initialCount;

        // 再新建一次，这次填了地址并「应用修改」。
        Console.WriteLine();
        addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();

        var activeBefore = state.Config.ActiveModelId;

        var endpointBox = (TextBox)window.FindName("ModelEndpointBox")!;
        var formatBox = (ComboBox)window.FindName("ModelFormatBox")!;
        var modelBox = (ComboBox)window.FindName("ModelCodePicker")!;
        var applyButton = (Button)window.FindName("ApplyModelButton")!;

        // 先选 Anthropic 格式。
        formatBox.SelectedIndex = 2;  // 0=OpenAI, 1=Responses, 2=Anthropic
        Pump();

        // 填一个只到 base 的地址，然后失焦 —— 应该自动补全成 /v1/messages。
        endpointBox.Text = "https://api.deepseek.com/anthropic";
        endpointBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Pump();

        Console.WriteLine($"填了「https://api.deepseek.com/anthropic」并失焦，自动补全成: 「{endpointBox.Text}」");
        ok &= endpointBox.Text == "https://api.deepseek.com/anthropic/v1/messages";

        // 已经带版本段的 base：**只补最后那一段** —— 智谱那种 /api/paas/v4 就是这一类，
        // 再塞一个 /v1 进去就成了 /v4/v1/chat/completions（错），而正确的是 /v4/chat/completions。
        formatBox.SelectedIndex = 0;   // OpenAI
        Pump();

        endpointBox.Text = "https://open.bigmodel.cn/api/paas/v4";
        endpointBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Pump();

        Console.WriteLine($"带版本段的「…/api/paas/v4」补全成: 「{endpointBox.Text}」");
        ok &= endpointBox.Text == "https://open.bigmodel.cn/api/paas/v4/chat/completions";

        // **末尾带斜杠也要补。** 以前这里有一条"斜杠 = 我写完了"的暗号会跳过补全，而粘贴过来的
        // 地址常常自带尾巴斜杠 —— 于是勾着"自动补全"却什么都不发生。
        endpointBox.Text = "https://open.bigmodel.cn/api/paas/v4/";
        endpointBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Pump();

        Console.WriteLine($"带尾巴斜杠的「…/api/paas/v4/」补全成: 「{endpointBox.Text}」");
        ok &= endpointBox.Text == "https://open.bigmodel.cn/api/paas/v4/chat/completions";

        // 关掉自动补全：**原样使用，一个字符都不动** —— 有些服务商的路径就是不长标准那样，
        // 补错了比不补更气人（智谱那种地址就是这么被补错的）。
        var autoComplete = (CheckBox)window.FindName("ModelAutoCompleteCheck")!;
        autoComplete.IsChecked = false;
        Pump();

        endpointBox.Text = "https://open.bigmodel.cn/api/paas/v4";
        endpointBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Pump();

        Console.WriteLine($"关掉自动补全后，同样填「…/api/paas/v4」: 「{endpointBox.Text}」");
        ok &= endpointBox.Text == "https://open.bigmodel.cn/api/paas/v4";

        autoComplete.IsChecked = true;   // 还原，别影响后面的应用
        Pump();

        // 填模型名、点「应用修改」。
        modelBox.Text = "deepseek-flash";
        Pump();

        applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();

        // 添加服务商**不许动"当前启用的是谁"** —— 新建的那个默认不勾，而"没勾"不是
        // "关掉模型监督"。以前这里会把 ActiveModelId 清空，于是"加一条管道"被判成放宽、
        // 要令牌（用户就是这么卡住的）。
        Console.WriteLine($"添加之后，当前启用的服务商: 「{state.Config.ActiveModelId}」（原来「{activeBefore}」）");
        ok &= state.Config.ActiveModelId == activeBefore;

        var countAfterApply = state.Config.Models.Count;
        Console.WriteLine($"点过「应用修改」: 配置里现在有 {countAfterApply} 个服务商（新条目{(countAfterApply == initialCount + 1 ? "加进去了" : "没加进去")}）");
        ok &= countAfterApply == initialCount + 1;

        Console.WriteLine($"回到列表页了吗: {listView.Visibility == Visibility.Visible}");
        ok &= listView.Visibility == Visibility.Visible;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论：新建时不立刻保存，点取消不留半成品，地址自动补全。"
            : "× 有些检查没过。");

        return ok ? 0 : 1;
    }

    private static void Pump() => System.Windows.Threading.Dispatcher.CurrentDispatcher
        .Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>
    /// 写一份**属于这次检查自己**的配置。
    ///
    /// 这个检查会点「应用修改」，也就是真的走保存那条路。没有自己的配置文件、没有隔离的管道名，
    /// 保存请求就会经用户正在跑的那个 Service 落到他的真配置上 —— 我第一次跑正是这么往
    /// E:\Sentinel\config.json 里塞了四个服务商的。
    /// </summary>
    public static void WriteConfig(string path)
    {
        var config = new Sentinel.Core.Models.AppConfig();
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        config.Models.Add(new Sentinel.Core.Models.ModelProvider
        {
            Id = "m1",
            Name = "DeepSeek",
            Format = Sentinel.Core.Models.ApiFormat.Anthropic,
            Endpoint = "https://api.deepseek.com/anthropic/v1/messages",
            Model = "deepseek-flash",
            ApiKey = "sk-example",
        });

        config.ActiveModelId = "m1";

        Sentinel.Core.Config.ConfigStore.Save(config, path);
    }
}
