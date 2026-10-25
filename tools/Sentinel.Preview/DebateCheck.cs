using System.IO;
using System.Windows;
using System.Windows.Threading;
using Sentinel.Agent;
using Sentinel.Core.Config;
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

        // 「要不要继续」那个小窗也画一张：它同样是代码里拼的布局，主题里"固定高度输入框"
        // 那个坑已经踩过一次，不能只看源码。
        var checkpoint = debate.BuildCheckpointWindow(3, _ => { });
        checkpoint.WindowStartupLocation = WindowStartupLocation.Manual;
        checkpoint.Left = -20000;
        checkpoint.Top = -20000;
        checkpoint.ShowInTaskbar = false;
        checkpoint.ShowActivated = false;
        checkpoint.Show();
        Pump();

        var cw = (int)Math.Max(1, checkpoint.ActualWidth);
        var ch = (int)Math.Max(1, checkpoint.ActualHeight);

        var cbitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            cw, ch, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        cbitmap.Render(checkpoint);

        var cpath = Path.Combine(workDir, "checkpoint-window.png");
        var cencoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        cencoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cbitmap));

        using (var stream = File.Create(cpath))
            cencoder.Save(stream);

        Console.WriteLine($"继续窗尺寸: {cw} x {ch}");
        Console.WriteLine($"继续窗截图: {cpath}");

        checkpoint.Close();
        debate.Close();

        return File.Exists(path) && File.Exists(cpath) ? 0 : 1;
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
