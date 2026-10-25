using System.Text;
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

    // 讨论过程按群聊来显示：模型带头像和气泡（左右分开），进度/提示居中成灰字。
    // 纯文本读不出"谁在回谁"这个结构，而讨论的价值就在这个结构里。
    private readonly StackPanel _messages = new() { Margin = new Thickness(8) };

    private readonly ScrollViewer _scroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private readonly StringBuilder _transcript = new();

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
                   + "每 3 轮停下来问你一次，可以继续，也可以补充信息给两个模型。",
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

        inputs.Children.Add(new TextBlock
        {
            Text = "开始时会现读一次所选窗口的内容（读到什么，日志第一行会写出来）。"
                   + "每轮要发 4 次左右请求，每 3 轮会停下来问你一次，中途可以点「停止」。",
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
        _stop.Click += (_, _) => _cts?.Cancel();
        _copy.Click += (_, _) => CopyOrWarn(_transcript.ToString(), "讨论记录");
        _copyPatch.Click += (_, _) =>
        {
            if (_patch.Length > 0)
                CopyOrWarn(_patch, "补丁");
        };
        _close.Click += (_, _) => Close();
        Closed += (_, _) => _cts?.Cancel();

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

            var outcome = await debate.RunAsync(
                providerA,
                providerB,
                string.Join("\n", instruction),
                snapshot,
                opinion,
                _reason.Text.Trim(),
                line => Dispatcher.Invoke(() => Append(line)),
                _cts.Token,
                // 必须切回 UI 线程再建窗口：Core 里 await 之后用的是 ConfigureAwait(false)，
                // 续体落在线程池的 MTA 线程上 —— 在那里 new Window 会直接抛"调用线程必须为 STA"。
                rounds => Dispatcher.InvokeAsync(() => AskCheckpoint(rounds)).Task);

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

        var copy = new Button
        {
            Content = "复制这几句", Padding = new Thickness(18, 6, 18, 6), IsDefault = true,
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
                   + "从严判断不会再和正常一起把这类页面放走。",
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
    /// 每跑完 3 轮还没通过时问一次：继续吗？顺手补充的信息会原样交给两个模型。
    /// 决策权在这里交回用户 —— 后面每 3 轮都还会再问一次。
    /// </summary>
    private DebateCheckpoint AskCheckpoint(int rounds)
    {
        var result = new DebateCheckpoint(false);
        var dialog = BuildCheckpointWindow(rounds, r => result = r);

        dialog.ShowDialog();

        return result;
    }

    /// <summary>建那个"要不要继续"的小窗（单独拆出来是为了能被离屏渲染检查布局）。</summary>
    public Window BuildCheckpointWindow(int rounds, Action<DebateCheckpoint> done)
    {
        // 主题里的 TextBox 是"固定高度的单行输入框"，这里必须显式覆盖，否则又变成一条细带。
        var note = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = double.NaN,
            MinHeight = 80,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            Padding = new Thickness(8),
        };

        var go = new Button
        {
            Content = "继续再跑 3 轮", Padding = new Thickness(18, 6, 18, 6), IsDefault = true,
        };

        var stop = new Button
        {
            Content = "收工", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        buttons.Children.Add(go);
        buttons.Children.Add(stop);

        var body = new StackPanel { Margin = new Thickness(20) };

        body.Children.Add(new TextBlock
        {
            Text = $"已经跑了 {rounds} 轮，还没通过。",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = "继续也可以，收工也可以 —— 由你决定。"
                   + "如果两边都漏了某些信息（比如\"这页是我在查资料\"），写在下面，会原样交给两个模型。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
        });

        body.Children.Add(new TextBlock
        {
            Text = "补充信息（可空）",
            Margin = new Thickness(0, 12, 0, 4),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"),
        });

        body.Children.Add(note);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "要不要继续讨论",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Content = body,
        };

        go.Click += (_, _) =>
        {
            done(new DebateCheckpoint(true, note.Text.Trim()));
            dialog.Close();
        };

        stop.Click += (_, _) =>
        {
            done(new DebateCheckpoint(false));
            dialog.Close();
        };

        return dialog;
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

        var color = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString(AvatarColors[Math.Abs(hash) % AvatarColors.Length])!);

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

    private static UIElement Message(string speaker, string text, bool right)
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
            Background = new System.Windows.Media.SolidColorBrush(right
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
