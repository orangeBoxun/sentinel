using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
public sealed class DebateWindow : Window
{
    private readonly AgentState _state;
    private readonly Target _target;

    private readonly ComboBox _windowPicker = new() { Width = 430 };
    private readonly Button _refreshWindows = new() { Content = "刷新", Margin = new Thickness(8, 0, 0, 0) };

    private readonly ComboBox _first = new() { DisplayMemberPath = "Display", Width = 190 };
    private readonly ComboBox _second = new() { DisplayMemberPath = "Display", Width = 190 };
    private readonly ComboBox _opinion = new() { Width = 110 };
    private readonly TextBox _reason = new() { MinWidth = 220 };

    /// <summary>多久停下来问一次；「不限」就一直跑到通过（没人盯着的时候用）。</summary>
    private readonly ComboBox _batch = new() { Width = 170 };

    // 讨论过程按群聊来显示：模型带头像和气泡（左右分开），进度/提示居中成灰字。
    // 纯文本读不出"谁在回谁"这个结构，而讨论的价值就在这个结构里。
    private readonly StackPanel _messages = new() { Margin = new Thickness(8) };

    private readonly ScrollViewer _scroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private readonly StringBuilder _transcript = new();

    /// <summary>常驻输入框：随时能发一句，下一次模型开口前会带上。</summary>
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = double.NaN,
        MinHeight = 38,
        MaxHeight = 90,
        VerticalContentAlignment = VerticalAlignment.Center,
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
        Margin = new Thickness(0, 8, 8, 0),
        IsEnabled = false,
    };

    private readonly Button _send = new()
    {
        Content = "发送",
        Padding = new Thickness(16, 6, 16, 6),
        Margin = new Thickness(0, 8, 0, 0),
        IsEnabled = false,
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    // 用户发的话先排在这里；Core 每次要开口问模型之前取走。
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _notes = new();

    /// <summary>第 3 轮停下来的"继续/收工"决定，等用户点。</summary>
    private TaskCompletionSource<DebateCheckpoint>? _decision;

    /// <summary>哪个模型显示在右边（左边是 A，右边是 B）。</summary>
    private string? _rightSide;

    /// <summary>离屏渲染检查用：指定哪个模型显示在右边。</summary>
    public void SetRightSide(string? name) => _rightSide = name;

    /// <summary>
    /// 往剪贴板写文本。远程桌面、输入法、剪贴板管理器都可能正占着剪贴板，
    /// 这时 OleSetClipboard 会抛 CLIPBRD_E_CANT_OPEN —— 在事件处理器里没接住的话，
    /// 会一路炸回 RunAsync 的 catch（用户看到的就是"讨论出错了"，而且窗口卡在那儿）。
    /// 所以这里短间隔重试几次，失败就返回 false，由调用处提示。
    /// </summary>
    private static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        // 重试别太多：WPF 内部本来就会重试，外面再叠一层只会让"卡一下"变成"卡几秒"。
        for (var i = 0; i < 3; i++)
        {
            try
            {
                // copy: true —— 程序关了以后剪贴板里还在。
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }

    private void CopyOrWarn(string text, string what)
    {
        if (TryCopy(text))
            return;

        Append(new DebateLine("系统",
            $"复制{what}失败：剪贴板被别的程序占着（远程桌面、输入法、剪贴板管理器都可能）。稍后再点一次。",
            IsSystem: true));
    }

    private readonly Button _start = new() { Content = "开始讨论", Padding = new Thickness(18, 6, 18, 6) };
    private readonly Button _stop = new()
    {
        Content = "停止", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0),
        IsEnabled = false,
    };

    private readonly Button _copy = new() { Content = "复制全部", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _copyPatch = new()
    {
        Content = "复制补丁", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0),
        IsEnabled = false,
    };

    private readonly Button _close = new() { Content = "关闭", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

    private CancellationTokenSource? _cts;
    private string _patch = "";

    public DebateWindow(AgentState state, Target target, WindowRef window)
    {
        _state = state;
        _target = target;

        Title = "模型讨论 —— 找出谁判错了、禁令该怎么改";
        Width = 820;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        Content = BuildUi();
        FillChoices();
        RefreshWindowChoices(window.NativeHandle);
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(20) };

        var inputs = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(inputs, Dock.Top);

        inputs.Children.Add(new TextBlock
        {
            Text = "让两个模型各自判一次，按你填的标准找出会\"有把握地判死\"的那一方：另一方先指出它错在哪，"
                   + "它照着写补丁 —— 补丁会被真的加进禁令、重新判这一页，判不对就继续。"
                   + "按你选的间隔停下来问你；下面的输入框随时能发一句话，下一个说话的模型会看到。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var rowWindow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        rowWindow.Children.Add(Label("讨论的窗口"));
        rowWindow.Children.Add(_windowPicker);
        rowWindow.Children.Add(_refreshWindows);
        inputs.Children.Add(rowWindow);

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

        var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row3.Children.Add(Label("停下来问的间隔"));
        row3.Children.Add(_batch);
        row3.Children.Add(new TextBlock
        {
            Text = "「不限」会一直跑到通过（最多 20 轮），适合没时间盯着的时候",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
        });

        inputs.Children.Add(row3);

        inputs.Children.Add(new TextBlock
        {
            Text = "开始时会现读一次所选窗口的内容（读到什么，日志第一行会写出来）。"
                   + "每轮要发 5 次左右请求，按上面的间隔停下来问你，中途可以点「停止」。",
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
        buttons.Children.Add(_stop);
        buttons.Children.Add(_copy);
        buttons.Children.Add(_copyPatch);
        buttons.Children.Add(_close);
        root.Children.Add(buttons);

        var inputRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_send, Dock.Right);
        inputRow.Children.Add(_send);
        inputRow.Children.Add(_input);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        root.Children.Add(inputRow);

        _reason.Width = 320;

        _scroller.Content = _messages;

        var box = new Border
        {
            Child = _scroller,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("Hairline"),
        };

        root.Children.Add(box);

        _refreshWindows.Click += (_, _) => RefreshWindowChoices();
        _start.Click += async (_, _) => await RunAsync();
        _stop.Click += (_, _) => StopRun();
        _send.Click += (_, _) => Send();
        _input.KeyDown += (_, e) =>
        {
            // Enter 发送，Shift+Enter 换行。
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                Send();
                e.Handled = true;
            }
        };
        _copy.Click += (_, _) => CopyOrWarn(_transcript.ToString(), "讨论记录");
        _copyPatch.Click += (_, _) =>
        {
            if (_patch.Length > 0)
                CopyOrWarn(_patch, "补丁");
        };
        _close.Click += (_, _) => Close();
        Closed += (_, _) => StopRun();

        return root;
    }

    private static TextBlock Label(string text, double left = 0) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 6, 0),
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
    };

    /// <summary>
    /// 重新枚举这个目标现在的窗口。为什么不沿用打开时的那一个：目标可能开了好几个窗口
    /// （浏览器尤其如此），而且你很可能在讨论之前又翻了两页 —— 那时窗口句柄可能已经不在了。
    /// </summary>
    private void RefreshWindowChoices(long? prefer = null)
    {
        var picked = prefer ?? (_windowPicker.SelectedItem as WindowRef)?.NativeHandle;

        using var enumerator = new WindowEnumerator();

        var matches = enumerator.Enumerate()
            .Where(w => Sentinel.Core.Engine.TargetMatcher.Matches(_target, w))
            .OrderByDescending(w => w.IsForeground)
            .ThenByDescending(w => w.IsExposed)
            .ToList();

        _windowPicker.ItemsSource = matches;
        _windowPicker.DisplayMemberPath = nameof(WindowRef.MainWindowTitle);

        _windowPicker.SelectedItem =
            matches.FirstOrDefault(w => w.NativeHandle == picked) ?? matches.FirstOrDefault();

        _start.IsEnabled = matches.Count > 0;

        if (matches.Count == 0)
            Append(new DebateLine("系统", "这个目标当前没有窗口 —— 先把那个应用打开，再点「刷新」。", IsSystem: true));
    }

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

        _batch.ItemsSource = new List<Choice<string>>
        {
            new("3", "每 3 轮问一次"),
            new("5", "每 5 轮问一次"),
            new("10", "每 10 轮问一次"),
            new("unlimited", "不限轮数（自动跑）"),
        };

        _batch.SelectedIndex = 0;
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

        if (_windowPicker.SelectedItem is not WindowRef window)
        {
            Append(new DebateLine("系统", "先选一个窗口（没有的话点「刷新」）。", IsSystem: true));
            return;
        }

        _messages.Children.Clear();
        _transcript.Clear();
        _rightSide = providerB.Name;
        _start.IsEnabled = false;
        _copy.IsEnabled = false;
        _copyPatch.IsEnabled = false;
        _stop.IsEnabled = true;
        _patch = "";

        _notes.Clear();
        _input.Clear();
        _input.IsEnabled = true;
        _send.IsEnabled = true;
        _batch.IsEnabled = false;

        _cts = new CancellationTokenSource();

        try
        {
            Append(new DebateLine("系统", "正在读这个窗口的内容…", IsSystem: true));

            ContentSnapshot? snapshot;

            using (var reader = new UiaContentReader())
                snapshot = reader.Read(window, _target.Reader);

            if (snapshot is null)
            {
                Append(new DebateLine("系统", "读不到这个窗口的内容 —— 先确认「自检」里能读到。", IsSystem: true));
                return;
            }

            // 把**实际读到的**贴出来：讨论错了页面是很容易发生、又很难发现的事。
            Append(new DebateLine("系统",
                $"读到：{snapshot.Title ?? "(无标题)"}{Environment.NewLine}" +
                $"       {snapshot.Url ?? "(没有网址)"}（正文 {snapshot.BodyText?.Length ?? 0} 字）",
                IsSystem: true));

            var opinion = (_opinion.SelectedItem as Choice<string?>)?.Value;

            var debate = new ModelDebate(usage: _state.Monitor.Usage);

            var perBatch = (_batch.SelectedItem as Choice<string>)?.Value ?? "3";
            var unlimited = perBatch == "unlimited";

            var batchSize = unlimited
                ? ModelDebate.RoundsPerBatch
                : int.TryParse(perBatch, out var parsed) ? parsed : ModelDebate.RoundsPerBatch;

            var outcome = await debate.RunAsync(
                providerA,
                providerB,
                string.Join("\n", instruction),
                snapshot,
                opinion,
                _reason.Text.Trim(),
                line => Dispatcher.Invoke(() => Append(line)),
                _cts.Token,
                // 决策行要建在 UI 线程上（Core 的续体在线程池的 MTA 线程）。
                rounds => Dispatcher.InvokeAsync(() => AskCheckpointInline(rounds)).Task.Unwrap(),
                DrainNotes,
                batchSize,
                unlimited);

            if (outcome is not null && outcome.Patch.Count > 0)
            {
                _patch = outcome.Text;
                _copyPatch.IsEnabled = true;
                _copyPatch.Content = outcome.Verified ? "复制补丁" : "复制候选（未验证）";

                // 验证过了就单独用一个窗口把补丁摆出来：聊天记录里那句话很容易被刷走。
                if (outcome.Verified)
                {
                    try
                    {
                        BuildPatchWindow(outcome.Patch).ShowDialog();
                    }
                    catch (Exception ex)
                    {
                        // 这个窗口里的任何异常都不该把"讨论已经成功"这件事冲掉。
                        Append(new DebateLine("系统",
                            $"补丁窗口出错了：{ex.GetType().Name}：{ex.Message}", IsSystem: true));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Append(new DebateLine("系统", $"讨论出错了：{ex.GetType().Name}：{ex.Message}", IsSystem: true));
        }
        finally
        {
            _start.IsEnabled = true;
            _copy.IsEnabled = true;
            _stop.IsEnabled = false;
            _input.IsEnabled = false;
            _send.IsEnabled = false;
            _batch.IsEnabled = true;
            _decision?.TrySetResult(new DebateCheckpoint(false));
            _decision = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>验证通过后单独摆出补丁 —— 聊天记录会被后面的话刷走，这一句值得单独看一眼。</summary>
    public Window BuildPatchWindow(IReadOnlyList<string> patch)
    {
        var text = string.Join(Environment.NewLine, patch);

        // 同样的坑：主题里的 TextBox 是固定高度，这里要撑开。
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = double.NaN,
            MinHeight = 72,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 13.5,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 12, 0, 0),
        };

        var apply = new Button
        {
            Content = "追加到规则并保存", Padding = new Thickness(18, 6, 18, 6), IsDefault = true,
        };

        var copy = new Button
        {
            Content = "复制这几句", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0),
        };

        var close = new Button
        {
            Content = "关闭", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        buttons.Children.Add(apply);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var body = new StackPanel { Margin = new Thickness(20) };

        body.Children.Add(new TextBlock
        {
            Text = "✓ 已验证：把下面这几句加到禁令末尾",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = "验证方式：把这几句接在禁令后面，用改过的禁令 + 这一页重新判了一次（不带讨论内容）—— "
                   + "从严判断不会再和正常一起把这类页面放走。"
                   + "可以直接点「追加到规则并保存」，加到这条目标的「模型监督」规则末尾。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
        });

        body.Children.Add(box);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "补丁（已验证）",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Content = body,
        };

        apply.Click += (_, _) =>
        {
            var target = _state.Config.Targets.FirstOrDefault(t => t.Id == _target.Id) ?? _target;

            var rule = target.Rules.FirstOrDefault(r => r.Enabled && r.Match.Type == MatchKind.Model);

            if (rule is null)
            {
                MessageBox.Show(dialog, "这个目标上没有启用中的「模型监督」规则，没地方可加。",
                    "没有可追加的规则", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;
            }

            var added = patch.Where(l => !rule.Match.Patterns.Contains(l)).ToList();

            if (added.Count == 0)
            {
                MessageBox.Show(dialog, $"规则「{rule.Name}」里已经有这几句了。",
                    "不用重复追加", MessageBoxButton.OK, MessageBoxImage.Information);

                return;
            }

            foreach (var line in added)
                rule.Match.Patterns.Add(line);

            if (_state.SaveConfig())
            {
                apply.IsEnabled = false;
                apply.Content = "已追加并保存";

                MessageBox.Show(dialog,
                    $"已追加 {added.Count} 句到规则「{rule.Name}」并保存。",
                    "已保存", MessageBoxButton.OK, MessageBoxImage.Information);

                return;
            }

            // 保存被拒时，内存里的配置已经被放回实际的存储状态 —— 把刚加的先撤掉，免得看起来"加了"。
            foreach (var line in added)
                rule.Match.Patterns.Remove(line);

            MessageBox.Show(dialog,
                $"没保存上：{_state.SaveError ?? "服务拒绝了这次保存"}",
                "保存被拒绝", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        copy.Click += (_, _) =>
        {
            if (TryCopy(text))
                return;

            MessageBox.Show(dialog,
                "剪贴板被别的程序占着（远程桌面、输入法、剪贴板管理器都可能）。"
                + "稍后再点一次，或者直接在上面那个框里选中文字、按 Ctrl+C。",
                "没复制上", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        close.Click += (_, _) => dialog.Close();

        return dialog;
    }

    /// <summary>
    /// 每跑完 3 轮还没通过时问一次 —— 以内联的两颗按钮，不弹窗：决定权在用户，
    /// 输入框一直能用，想补充信息随时发。
    /// </summary>
    public Task<DebateCheckpoint> AskCheckpointInline(int rounds)
    {
        _decision = new TaskCompletionSource<DebateCheckpoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        Append(new DebateLine("系统", $"已经跑了 {rounds} 轮，还没通过 —— 要继续吗？", IsSystem: true));

        var go = new Button { Content = "继续再跑 3 轮", Padding = new Thickness(16, 5, 16, 5) };

        var stop = new Button
        {
            Content = "收工", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };

        row.Children.Add(go);
        row.Children.Add(stop);
        _messages.Children.Add(row);
        _scroller.ScrollToEnd();

        void Decide(bool keepGoing)
        {
            row.IsEnabled = false;

            Append(new DebateLine("系统", keepGoing ? "你选择继续。" : "你选择收工。", IsSystem: true));
            _decision?.TrySetResult(new DebateCheckpoint(keepGoing));
        }

        go.Click += (_, _) => Decide(true);
        stop.Click += (_, _) => Decide(false);

        return _decision.Task;
    }

    /// <summary>用户发的话：直接进对话（右侧、绿气泡），同时排队等下一次模型开口前带上。</summary>
    public void Say(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;

        _transcript.Append($"【你】{text}").Append(Environment.NewLine).Append(Environment.NewLine);

        _messages.Children.Add(Message("你", text, right: true, mine: true));
        _scroller.ScrollToEnd();
    }

    private void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0)
            return;

        _input.Clear();
        Say(text);
        _notes.Enqueue(text);
        _input.Focus();
    }

    /// <summary>Core 每次要开口问模型之前来取一次。</summary>
    private IReadOnlyList<string> DrainNotes()
    {
        var list = new List<string>();

        while (_notes.TryDequeue(out var text))
            list.Add(text);

        return list;
    }

    private void StopRun()
    {
        _cts?.Cancel();
        _decision?.TrySetResult(new DebateCheckpoint(false));
    }

    /// <summary>
    /// 往"群里"发一条：模型带头像和气泡（左右分开），进度/提示居中成灰字。
    /// 做成聊天样式不是为了好看 —— 讨论的关键是"谁在回谁"，纯文本一行行读不出这个结构。
    /// </summary>
    public void Append(DebateLine line)
    {
        _transcript.Append(line.IsSystem ? $"· {line.Text}" : $"【{line.Speaker}】{line.Text}")
                   .Append(Environment.NewLine)
                   .Append(Environment.NewLine);

        _messages.Children.Add(line.IsSystem
            ? SystemNote(line.Text)
            : Message(line.Speaker, line.Text, right: line.Speaker == _rightSide));

        _scroller.ScrollToEnd();
    }

    private static readonly string[] AvatarColors =
    {
        "#4C8BF5", "#34A853", "#F29900", "#A142F4", "#E8453C", "#12A4AF",
    };

    private static TextBlock SystemNote(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        FontSize = 11.5,
        LineHeight = 17,
        Margin = new Thickness(28, 4, 28, 10),
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
    };

    private static UIElement Avatar(string speaker)
    {
        var hash = speaker.Aggregate(0, (h, c) => unchecked(h * 31 + c));

        var hex = speaker == "你"
            ? "#52C41A"
            : AvatarColors[Math.Abs(hash) % AvatarColors.Length];

        var color = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);

        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = color,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(speaker) ? "?" : speaker[..1].ToUpperInvariant(),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static UIElement Message(string speaker, string text, bool right, bool mine = false)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var avatar = Avatar(speaker);

        DockPanel.SetDock(avatar, right ? Dock.Right : Dock.Left);
        row.Children.Add(avatar);

        var bubble = new Border
        {
            MaxWidth = 470,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(10),
            Background = new System.Windows.Media.SolidColorBrush(mine
                ? System.Windows.Media.Color.FromRgb(0xD9, 0xF7, 0xBE)
                : right
                    ? System.Windows.Media.Color.FromRgb(0xD6, 0xE9, 0xFF)
                    : System.Windows.Media.Color.FromRgb(0xF2, 0xF3, 0xF5)),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 20,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A)),
            },
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        stack.Children.Add(new TextBlock
        {
            Text = speaker,
            FontSize = 11,
            Margin = new Thickness(2, 0, 2, 3),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        });

        stack.Children.Add(bubble);
        row.Children.Add(stack);

        return row;
    }
}
