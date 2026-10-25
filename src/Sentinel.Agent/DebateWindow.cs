using System.Windows;
using System.Windows.Controls;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Model;
using Sentinel.Core.Models;
using Sentinel.Platform;

namespace Sentinel.Agent;

/// <summary>
/// 「模型讨论」：挑两个服务商，把你自己的判断也告诉它们，让它们互相挑错，
/// 最后给出一份**可以直接照抄的禁令改法**。
///
/// <para>
/// 它跑的是一次真实的模型会话（会花钱），所以要点「开始讨论」才发请求。
/// 用当前窗口里真实读到的那一页 —— 讨论的对象必须是你实际卡住的那个页面，
/// 不然改出来的引言只会对样例有效。
/// </para>
/// </summary>
internal sealed class DebateWindow : Window
{
    private readonly AgentState _state;
    private readonly Target _target;
    private readonly WindowRef _window;

    private readonly ComboBox _first = new() { DisplayMemberPath = "Display", Width = 190 };
    private readonly ComboBox _second = new() { DisplayMemberPath = "Display", Width = 190 };
    private readonly ComboBox _opinion = new() { Width = 110 };
    private readonly TextBox _reason = new() { MinWidth = 220 };

    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI"),
        FontSize = 12.5,
        Margin = new Thickness(0, 14, 0, 0),
        Padding = new Thickness(10),
    };

    private readonly Button _start = new() { Content = "开始讨论", Padding = new Thickness(18, 6, 18, 6) };
    private readonly Button _copy = new() { Content = "复制全部", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _close = new() { Content = "关闭", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

    public DebateWindow(AgentState state, Target target, WindowRef window)
    {
        _state = state;
        _target = target;
        _window = window;

        Title = "模型讨论 —— 找出谁判错了、禁令该怎么改";
        Width = 820;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        Content = BuildUi();
        FillChoices();
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(20) };

        var inputs = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(inputs, Dock.Top);

        inputs.Children.Add(new TextBlock
        {
            Text = "让两个模型互相看对方的结论，找出更可能判错的一方，并给出禁令的改法。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        row1.Children.Add(Label("模型 A"));
        row1.Children.Add(_first);
        row1.Children.Add(Label("模型 B", left: 14));
        row1.Children.Add(_second);
        inputs.Children.Add(row1);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row2.Children.Add(Label("你认为"));
        row2.Children.Add(_opinion);
        row2.Children.Add(Label("理由（可选）", left: 14));
        row2.Children.Add(_reason);
        inputs.Children.Add(row2);

        inputs.Children.Add(new TextBlock
        {
            Text = $"讨论用的页面：「{_window.MainWindowTitle}」（开始时会现读一次内容）。这一项会真的发几次请求，会产生费用。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
            Margin = new Thickness(0, 10, 0, 0),
        });

        root.Children.Add(inputs);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(_start);
        buttons.Children.Add(_copy);
        buttons.Children.Add(_close);
        root.Children.Add(buttons);

        _reason.MinWidth = 240;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = _log;

        var box = new Border
        {
            Child = scroll,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("Hairline"),
        };

        root.Children.Add(box);

        _start.Click += async (_, _) => await RunAsync();
        _copy.Click += (_, _) => Clipboard.SetText(_log.Text);
        _close.Click += (_, _) => Close();

        return root;
    }

    private static TextBlock Label(string text, double left = 0) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 6, 0),
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
    };

    private void FillChoices()
    {
        var providers = _state.Config.Models.Select(m => new Choice<string>(m.Id, m.Name)).ToList();

        _first.ItemsSource = providers;
        _second.ItemsSource = providers;

        var chain = _state.Config.ModelChain.Where(id => providers.Any(p => p.Value == id)).ToList();

        _first.SelectedItem = providers.FirstOrDefault(p => chain.Count > 0 && p.Value == chain[0])
                              ?? providers.FirstOrDefault();

        _second.SelectedItem = providers.FirstOrDefault(p => chain.Count > 1 && p.Value == chain[1])
                               ?? providers.FirstOrDefault(p => !ReferenceEquals(p, _first.SelectedItem));

        _opinion.ItemsSource = new List<Choice<string?>>
        {
            new(null, "不确定"),
            new("违规", "违规"),
            new("不违规", "不违规"),
        };

        _opinion.SelectedIndex = 0;
    }

    private async Task RunAsync()
    {
        if (_first.SelectedItem is not Choice<string> a || _second.SelectedItem is not Choice<string> b)
        {
            Append(new DebateLine("系统", "先选两个服务商。", IsSystem: true));
            return;
        }

        if (a.Value == b.Value)
        {
            Append(new DebateLine("系统", "两个模型要选不同的 —— 讨论才有意义。", IsSystem: true));
            return;
        }

        var providerA = _state.Config.Models.FirstOrDefault(m => m.Id == a.Value);
        var providerB = _state.Config.Models.FirstOrDefault(m => m.Id == b.Value);

        if (providerA is null || providerB is null)
            return;

        var instruction = _target.Rules
            .Where(r => r.Enabled && r.Match.Type == MatchKind.Model)
            .SelectMany(r => r.Match.Patterns)
            .ToList();

        if (instruction.Count == 0)
        {
            Append(new DebateLine("系统", "这个目标上没有「模型监督」规则，没有禁令可讨论。", IsSystem: true));
            return;
        }

        _log.Clear();
        _start.IsEnabled = false;
        _copy.IsEnabled = false;

        try
        {
            Append(new DebateLine("系统", "正在读这个窗口的内容…", IsSystem: true));

            ContentSnapshot? snapshot;

            using (var reader = new UiaContentReader())
                snapshot = reader.Read(_window, _target.Reader);

            if (snapshot is null)
            {
                Append(new DebateLine("系统", "读不到这个窗口的内容 —— 先确认「自检」里能读到。", IsSystem: true));
                return;
            }

            var opinion = (_opinion.SelectedItem as Choice<string?>)?.Value;

            var debate = new ModelDebate(usage: _state.Monitor.Usage);

            await debate.RunAsync(
                providerA,
                providerB,
                string.Join("\n", instruction),
                snapshot,
                opinion,
                _reason.Text.Trim(),
                line => Dispatcher.Invoke(() => Append(line)),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Append(new DebateLine("系统", $"讨论出错了：{ex.GetType().Name}：{ex.Message}", IsSystem: true));
        }
        finally
        {
            _start.IsEnabled = true;
            _copy.IsEnabled = true;
        }
    }

    private void Append(DebateLine line)
    {
        var prefix = line.IsSystem ? "· " : $"【{line.Speaker}】";

        _log.AppendText($"{prefix}{line.Text}{Environment.NewLine}{Environment.NewLine}");
        _log.ScrollToEnd();
    }
}
