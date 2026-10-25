using System.IO;
using System.Windows;
using System.Windows.Threading;
using Sentinel.Agent;
using Sentinel.Core.Config;
using Sentinel.Core.Model;
using Sentinel.Core.Models;

namespace Sentinel.Preview;

/// <summary>
/// 把「模型讨论」窗口在屏幕外渲染成一张 PNG —— 用来检查布局。
///
/// <para>
/// 起因：用户说"有个小窗口有文字极其窄小、其他地方大片空白"。这种问题看代码看不出来
/// （DockPanel 的填充、ScrollViewer 的尺寸，都是运行时才定的），只能把它画出来看。
/// </para>
/// </summary>
internal static class DebateCheck
{
    public static int Run(string workDir)
    {
        Environment.SetEnvironmentVariable("SENTINEL_PIPE", $"Sentinel.Test.DebateCheck.{Environment.ProcessId}");

        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");
        WriteConfig(configPath);

        using var state = new AgentState(new[] { "--config", configPath });

        var target = state.Config.Targets[0];
        var window = new WindowRef(1234, "Codex", "示例页面", @"C:\app\Codex.exe", 0xABCD);

        var debate = new DebateWindow(state, target, window)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };

        debate.Show();
        Pump();

        // 塞几条示例，把群聊样式画出来 —— 空面板看不出头像、气泡和左右分栏。
        debate.SetRightSide("DeepSeek");

        debate.Append(new DebateLine("系统", "你的标准：这一页「违规」，理由：我在查资料", IsSystem: true));
        debate.Append(new DebateLine("系统",
            "按你说的「违规」，这一页：\n"
            + "· 智谱：正常判不违规；从严判不违规 → 直接定论，把这一页放走  ← 要修的是这一方\n"
            + "· DeepSeek：正常判违规；从严判违规 → 自己就判对了", IsSystem: true));
        debate.Append(new DebateLine("智谱",
            "正常「不违规」从严「不违规」（一致 → 会定论）\n理由：知乎搜索结果页，中间页不算浏览内容"));
        debate.Append(new DebateLine("DeepSeek",
            "正常「违规」从严「违规」（一致 → 会定论）\n理由：知乎搜索热点新闻，与学习工作无关"));
        debate.Append(new DebateLine("系统",
            "第 1 轮：「DeepSeek」先说它漏在哪，再让「智谱」答辩，两边把话说完，最后看重新判这一页的结果。",
            IsSystem: true));
        debate.Append(new DebateLine("DeepSeek",
            "它把搜索结果页当成了中间页，没看搜索词和结果标题 —— 标题已经全是同一个热点事件了。"));
        debate.Append(new DebateLine("智谱",
            "第一段：我认为搜索页本身不该按内容判，用户可能只是在找资料。\n"
            + "第二段：补丁：\n- 搜索页只要搜索词或结果标题指向娱乐、热点新闻，就从严判违规。"));
        debate.Append(new DebateLine("系统",
            "  用改过的禁令 + 这一页，重新走一次监控的判断（不带讨论里的任何内容）："
            + "智谱 正常「不违规」从严「违规」；DeepSeek 正常「违规」从严「违规」", IsSystem: true));

        // 常驻输入框发出来的一句话，以及"继续/收工"的内联按钮。
        debate.Say("这页是我在查资料，不是摸鱼");
        _ = debate.AskCheckpointInline(3);

        Pump();

        var width = (int)Math.Max(1, debate.ActualWidth);
        var height = (int)Math.Max(1, debate.ActualHeight);

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(debate);

        var path = Path.Combine(workDir, "debate-window.png");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using (var stream = File.Create(path))
            encoder.Save(stream);

        Console.WriteLine($"窗口尺寸: {width} x {height}");
        Console.WriteLine($"截图     : {path}");

        // 通过之后单独展示补丁的那个窗口，也画一张。
        var patchWindow = debate.BuildPatchWindow(new[]
        {
            "搜索页只要搜索词或结果标题指向娱乐、热点新闻，就从严判违规。",
            "搜索结果页不按\"中间页\"放过，仍按结果内容判断。",
        });

        patchWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        patchWindow.Left = -20000;
        patchWindow.Top = -20000;
        patchWindow.ShowInTaskbar = false;
        patchWindow.ShowActivated = false;
        patchWindow.Show();
        Pump();

        var pw = (int)Math.Max(1, patchWindow.ActualWidth);
        var ph = (int)Math.Max(1, patchWindow.ActualHeight);

        var pbitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            pw, ph, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        pbitmap.Render(patchWindow);

        var ppath = Path.Combine(workDir, "patch-window.png");
        var pencoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        pencoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pbitmap));

        using (var stream = File.Create(ppath))
            pencoder.Save(stream);

        Console.WriteLine($"补丁窗尺寸: {pw} x {ph}");
        Console.WriteLine($"补丁窗截图: {ppath}");

        patchWindow.Close();
        debate.Close();

        return File.Exists(path) && File.Exists(ppath) ? 0 : 1;
    }

    private static void WriteConfig(string path)
    {
        var config = new AppConfig();
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        config.Models.Add(new ModelProvider
        {
            Id = "m1", Name = "智谱", Format = ApiFormat.OpenAi,
            Endpoint = "https://relay.invalid/v1/chat/completions", Model = "glm-4-flash", ApiKey = "k",
        });

        config.Models.Add(new ModelProvider
        {
            Id = "m2", Name = "DeepSeek", Format = ApiFormat.Anthropic,
            Endpoint = "https://relay.invalid/anthropic/v1/messages", Model = "deepseek-flash", ApiKey = "k",
        });

        config.ModelChain = new List<string> { "m1", "m2" };
        config.ActiveModelId = "m1";

        config.Targets.Add(new Target
        {
            Id = "t1",
            DisplayName = "讨论测试目标",
            ProcessNames = { "notepad" },
            Rules =
            {
                new Rule
                {
                    Id = "r1",
                    Name = "模型规则",
                    PolicyId = "p",
                    Enabled = true,
                    Match = new MatchSpec { Type = MatchKind.Model, Patterns = { "禁止看与工作无关的娱乐内容" } },
                },
            },
        });

        ConfigStore.Save(config, path);
    }

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}
