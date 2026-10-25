using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Config;
using Sentinel.Core.Engine;
using Sentinel.Core.Logging;
using Sentinel.Core.Model;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;
using Sentinel.Core.Vault;
using Sentinel.Platform;

namespace Sentinel.Agent;

public partial class MainWindow : Window
{
    /// <summary>Tab order in MainWindow.xaml — the tray menu jumps straight to the log.</summary>
    public const int OverviewTab = 0;
    public const int VaultTabIndex = 1;
    public const int TargetTabIndex = 2;
    public const int RuleTabIndex = 3;
    public const int PolicyTabIndex = 4;
    public const int ScheduleTabIndex = 5;

    /// <summary>大语言模型页签所在的位置。日志/诊断在它后面，所以序号各自加一。</summary>
    public const int ModelTabIndex = 6;

    public const int LogTabIndex = 7;
    public const int DiagnoseTabIndex = 8;

    private readonly AgentState _state;
    private readonly ObservableCollection<string> _recent = new();
    private readonly DispatcherTimer _statusTimer;

    /// <summary>每个表单一份"改了没应用"的判定，见 <see cref="DirtyForm"/>。</summary>
    private readonly List<DirtyForm> _dirtyForms = new();

    private readonly DirtyForm _targetForm;
    private readonly DirtyForm _ruleForm;
    private readonly DirtyForm _policyForm;
    private readonly DirtyForm _scheduleForm;
    private readonly DirtyForm _modelForm;

    /// <summary>
    /// 上一次由程序自动填进「模型列表地址」的那个值。
    ///
    /// 用途只有一个：判断那个框里现在装的还是不是"我们填的"。是的话，改请求地址时可以跟着更新；
    /// 人自己动过之后就再也不碰它 —— 程序改掉人手填的东西，是这类自动填充最常见的毛病。
    /// </summary>
    private string _autoFilledListEndpoint = "";

    /// <summary>正在做程序自己的事（重新绑定列表、跳页签）—— 这时候不许弹"要不要保存"。</summary>
    private bool _programmatic;

    /// <summary>上一次真正生效的页签，回退用。</summary>
    private int _lastTabIndex;

    private List<Choice<WindowRef>> _windows = new();
    private Target? _selectedTarget;
    private Rule? _selectedRule;
    private Policy? _selectedPolicy;
    private Schedule? _selectedSchedule;
    private ModelProvider? _selectedModel;

    public MainWindow(AgentState state)
    {
        _state = state;

        InitializeComponent();

        RecentList.ItemsSource = _recent;

        RuleTypeBox.ItemsSource = new[]
        {
            new Choice<MatchKind>(MatchKind.Keyword, "关键词（包含即命中）"),
            new Choice<MatchKind>(MatchKind.Regex, "正则表达式"),
            new Choice<MatchKind>(MatchKind.Whitelist, "白名单（命中即放行）"),
            new Choice<MatchKind>(MatchKind.Model, "模型监督（需要配服务商）"),
        };

        AlertStyleBox.ItemsSource = new[]
        {
            new Choice<AlertStyle>(AlertStyle.Balloon, "托盘气泡（不打扰，但容易忽略）"),
            new Choice<AlertStyle>(AlertStyle.Fullscreen, "全屏提醒（盖住屏幕，强制打断）"),
        };

        ModelFormatBox.ItemsSource = new[]
        {
            new Choice<ApiFormat>(ApiFormat.OpenAi, "OpenAI Chat Completions"),
            new Choice<ApiFormat>(ApiFormat.Responses, "OpenAI Responses"),
            new Choice<ApiFormat>(ApiFormat.Anthropic, "Anthropic Messages"),
        };

        _state.Monitor.Outcome += OnOutcome;
        _state.Monitor.Failed += OnMonitorFailed;
        _state.Monitor.Stalled += OnMonitorFailed;

        // 表单的脏判定。签名要把「应用修改」真正读到的每一个字段都包进去 —— 漏一个，改了它就不会
        // 提示"有未保存的修改"，而那正是这里要防的事。
        _targetForm = AddForm(TargetTabIndex, "目标", TargetSignature, ApplyTargetForm, TargetDirtyMark, ApplyTargetButton);
        _ruleForm = AddForm(RuleTabIndex, "规则", RuleSignature, ApplyRuleForm, RuleDirtyMark, ApplyRuleButton);
        _policyForm = AddForm(PolicyTabIndex, "策略", PolicySignature, ApplyPolicyForm, PolicyDirtyMark, ApplyPolicyButton);
        _scheduleForm = AddForm(ScheduleTabIndex, "时间表", ScheduleSignature, ApplyScheduleForm, ScheduleDirtyMark, ApplyScheduleButton);
        _modelForm = AddForm(ModelTabIndex, "服务商", ModelSignature, ApplyModelForm, ModelDirtyMark, ApplyModelButton);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        Loaded += (_, _) =>
        {
            RefreshAll();
            RefreshWindows();
            RefreshLog();
            RefreshVaultStatus();
            _lastTabIndex = Tabs.SelectedIndex;
        };

        // 页签切换也要拦一下：表单是脏的而人走了，改动就没了。
        // 回退要挡掉自己触发的那一次，否则会一直问。
        Tabs.SelectionChanged += (_, _) =>
        {
            if (!_programmatic && Tabs.SelectedIndex != _lastTabIndex && !ConfirmLeaveDirtyForm(_lastTabIndex))
            {
                var previous = _lastTabIndex;
                Programmatic(() => Tabs.SelectedIndex = previous);
                return;
            }

            _lastTabIndex = Tabs.SelectedIndex;

            // The vault's state can change without this window doing anything — a seal is written by the
            // watchdog — so it is re-read on the way in rather than cached.
            if (Tabs.SelectedIndex == VaultTabIndex)
                RefreshVaultStatus();
        };

        Vault.Closed += RefreshVaultStatus;

        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _state.Monitor.Outcome -= OnOutcome;
            _state.Monitor.Failed -= OnMonitorFailed;
            _state.Monitor.Stalled -= OnMonitorFailed;
            Vault.Closed -= RefreshVaultStatus;
        };
    }

    public void SelectTab(int index)
    {
        if (index < 0 || index >= Tabs.Items.Count)
            return;

        // 程序自己跳页签（托盘菜单、被拒的保存把你送到「概览」）不该弹出"要不要保存"。
        Programmatic(() => Tabs.SelectedIndex = index);
        _lastTabIndex = index;
    }

    // ------------------------------------------------------------------ 有未保存的修改

    private DirtyForm AddForm(int tabIndex, string name, Func<string> signature, Func<bool> apply,
                              TextBlock mark, Button button)
    {
        var form = new DirtyForm(tabIndex, name, signature, apply, mark, button, Loud(), Quiet());
        _dirtyForms.Add(form);
        return form;
    }

    private Style Loud() => (Style)FindResource("PrimaryIconButton");

    private Style Quiet() => (Style)FindResource("IconButton");

    /// <summary>
    /// 做一件程序自己的事，期间不弹"要不要保存"。
    ///
    /// <para>
    /// 重新绑定列表会把选中项打到 -1，再选回来 —— 那两次 SelectionChanged 里表单看起来"换了个条目"，
    /// 而其实什么也没发生。不挡掉的话，保存一次就会弹出一次询问。
    /// </para>
    /// </summary>
    private void Programmatic(Action action)
    {
        var previous = _programmatic;
        _programmatic = true;
        try
        {
            action();
        }
        finally
        {
            _programmatic = previous;
        }
    }

    private DirtyForm? DirtyFormOnCurrentTab() =>
        _dirtyForms.FirstOrDefault(f => f.TabIndex == Tabs.SelectedIndex);

    /// <summary>正在把选中项拨回去 —— 那一次什么都别动，表单上的改动要留着。</summary>
    private bool _revertingSelection;

    /// <summary>
    /// 弹那个"有未保存的修改"的框这件事本身。默认就是弹一个真的 MessageBox。
    ///
    /// <para>
    /// 留成可替换的，是为了离线检查能在没有人点的情况下把三条分支（应用 / 放弃 / 留下）都走一遍 ——
    /// 一个真的模态框会把检查挂死在那儿，那等于这三条分支一条都没验过。
    /// </para>
    /// </summary>
    public Func<string, MessageBoxResult> AskLeaveDirtyForm
    {
        // 惰性取默认实现：字段初始值设定项里引不到实例方法。
        get => _askLeaveDirtyForm ??= DefaultAskLeaveDirtyForm;
        set => _askLeaveDirtyForm = value;
    }

    private Func<string, MessageBoxResult>? _askLeaveDirtyForm;

    private MessageBoxResult DefaultAskLeaveDirtyForm(string name) =>
        MessageBox.Show(this,
            $"「{name}」这一页有还没应用修改的改动，离开就会丢掉。\n\n要现在应用吗？",
            "有未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);


    /// <summary>
    /// 要离开某一页的表单了，而它还有没应用的改动 —— 问一句。返回 false 表示"留下"。
    ///
    /// <para>
    /// 这个提示存在的理由：改动只在点「应用修改」时才写下去，之前切走就静悄悄地丢了。以前没有任何
    /// 提示，看起来像是"改了、点了、但没保存"。它不是保存失败，是根本没触发保存。
    /// </para>
    /// <para>
    /// <paramref name="tabIndex"/> 是"正要离开的那一页"。切页签时必须传它：事件到这儿的时候
    /// <c>Tabs.SelectedIndex</c> 已经是新的那一页了，照着它去找会问错对象（问一张刚打开、
    /// 什么都没改的表单）。
    /// </para>
    /// </summary>
    private bool ConfirmLeaveDirtyForm(int? tabIndex = null)
    {
        if (_programmatic)
            return true;

        var index = tabIndex ?? Tabs.SelectedIndex;

        if (_dirtyForms.FirstOrDefault(f => f.TabIndex == index) is not { IsDirty: true } form)
            return true;

        // 询问期间把界面上别的动作挡掉：弹窗里点"是"会去保存，保存会重新绑定列表。
        _programmatic = true;
        try
        {
            var answer = AskLeaveDirtyForm(form.Name);

            return answer switch
            {
                // 应用了才算能走。被拒（要令牌之类）就留下 —— 和点"取消"是同一个结果。
                MessageBoxResult.Yes => form.Apply() && !form.IsDirty,

                // 明确说了不要，那就走。
                MessageBoxResult.No => true,

                _ => false,
            };
        }
        finally
        {
            _programmatic = false;
            RefreshDirtyForms();
        }
    }

    /// <summary>
    /// 把选中项拨回去。
    ///
    /// <para>
    /// 关键是<b>什么都不做</b>：拨回去会再触发一次 SelectionChanged，而那一次按常规会把表单按实体
    /// 重新填一遍 —— 那等于"留下"，改动却没了。所以那一次既不问、也不重填。
    /// </para>
    /// </summary>
    private void RevertSelection(Selector list, object? previous)
    {
        _revertingSelection = true;
        Programmatic(() => list.SelectedItem = previous);
        _revertingSelection = false;
    }

    /// <summary>拨回去的那一次 SelectionChanged：什么都不做，表单上的改动要留着。</summary>
    private bool IgnoreRevert() => _revertingSelection;

    private void RefreshDirtyForms()
    {
        foreach (var form in _dirtyForms)
            form.Refresh();
    }

    /// <summary>
    /// 刷新之后把选中项和表单认回来。
    ///
    /// <para>
    /// <b>关键在于：刷新之后配置可能已经被整体换成新读出来的对象了</b>（被拒之后服务会把存下来的
    /// 那份重新读一遍），所以 <c>_selectedTarget</c> 这些字段指向的都是上一份配置里的对象 ——
    /// 它们不在新列表里，选不回去，表单也就空了、或者留着被拒的那份值。
    /// 所以按 Id 在新配置里重新认一遍，再让列表和新对象对上，那几个"选中项变了"的处理函数
    /// 就会把表单按配置重填 —— 这正是我们要的。
    /// </para>
    /// </summary>
    private void RestoreSelection(Selection selection)
    {
        var ruleId = selection.Rule;
        var policyId = selection.Policy;
        var scheduleId = selection.Schedule;
        var modelId = selection.Model;

        var target = _stagedTarget is { } stagedTarget && stagedTarget.Id == selection.Target
            ? stagedTarget
            : _state.Config.Targets.FirstOrDefault(t => t.Id == selection.Target);
        var rule = _stagedRule is { } staged && staged.Id == ruleId
            ? staged
            : target?.Rules.FirstOrDefault(r => r.Id == ruleId);
        var policy = _state.Config.Policies.FirstOrDefault(p => p.Id == policyId);
        var schedule = _state.Config.Schedules.FirstOrDefault(s => s.Id == scheduleId);
        var model = _state.Config.Models.FirstOrDefault(m => m.Id == modelId);

        // **只认回来原来选过的那些，别的一律不许碰。** 无条件地写一遍会把"刷新自己填好的"
        // 又清成 null：首次加载时 selection 里全是空的，于是这里把下拉框刚选上的目标抹掉 ——
        // 而从那以后每次记下的都是"什么都没选"，保存被拒之后的还原就整段跳过了。
        if (selection.Target is not null)
        {
            _selectedTarget = target;
            _selectedRule = rule;
        }

        if (selection.Policy is not null)
            _selectedPolicy = policy;

        if (selection.Schedule is not null)
            _selectedSchedule = schedule;

        if (selection.Model is not null)
            _selectedModel = model;

        Programmatic(() =>
        {
            // 按"原来选过谁"逐项认回来。**没选过的别碰** —— 首次加载时什么都没选，
            // 无条件地选一遍会把刷新刚填好的列表清空（规则列表尤其：它的 ItemsSource 跟着目标走）。
            if (selection.Target is not null)
            {
                // 这一步会重绑 TargetList，并触发"选中项变了"把目标表单填一遍。
                RefreshTargetList();
                TargetList.SelectedItem = target;

                RuleList.ItemsSource = RuleRows();
                RuleList.SelectedItem = rule;
            }

            if (selection.Policy is not null)
            {
                RefreshPolicyList();
                PolicyList.SelectedItem = policy;
            }

            if (selection.Schedule is not null)
            {
                RefreshScheduleList();
                ScheduleList.SelectedItem = schedule;
            }
        });

        // 大语言模型那一页是"列表 + 整页编辑"，表单不在列表旁边 —— 重新打开一次编辑页，
        // 它自己会按配置填。
        if (model is not null && ModelEditView.Visibility == Visibility.Visible)
            OpenModelEditor(model);

    }

    // ---- 每个表单"现在长什么样"。要把「应用修改」读到的字段**全部**写进来。

    private string TargetSignature() => string.Join('\u001f',
        TargetNameBox.Text, TargetProcessBox.Text, TargetTitleBox.Text,
        ScanUrlCheck.IsChecked, ScanTitleCheck.IsChecked, ScanBodyCheck.IsChecked,
        IntervalBox.Text, TargetEnabledCheck.IsChecked);

    private string RuleSignature() => string.Join('\u001f',
        RuleNameBox.Text, RuleEnabledCheck.IsChecked,
        (RuleTypeBox.SelectedItem as Choice<MatchKind>)?.Value,
        FieldUrlCheck.IsChecked, FieldTitleCheck.IsChecked, FieldBodyCheck.IsChecked,
        RulePatternsBox.Text,
        (RulePolicyBox.SelectedItem as Choice<string?>)?.Value,
        (RuleScheduleBox.SelectedItem as Choice<string?>)?.Value,
        RulePriorityBox.Text, RuleReminderBox.Text,
        RulePauseEnabledCheck.IsChecked,
        RulePauseWorkdayPeriodBox.SelectedIndex, RulePauseWorkdayTimesBox.Text, RulePauseWorkdayMinutesBox.Text,
        RulePauseWeekendPeriodBox.SelectedIndex, RulePauseWeekendTimesBox.Text, RulePauseWeekendMinutesBox.Text);

    private string PolicySignature() => string.Join('\u001f',
        PolicyNameBox.Text,
        ActionNotifyCheck.IsChecked, ActionMinimizeCheck.IsChecked, ActionCloseCheck.IsChecked,
        ActionKillCheck.IsChecked, ActionLogCheck.IsChecked,
        GraceBox.Text, CooldownBox.Text, DebounceBox.Text, RepeatBox.Text,
        (AlertStyleBox.SelectedItem as Choice<AlertStyle>)?.Value);

    private string ScheduleSignature() => string.Join('\u001f',
        ScheduleIdBox.Text,
        DayMonCheck.IsChecked, DayTueCheck.IsChecked, DayWedCheck.IsChecked, DayThuCheck.IsChecked,
        DayFriCheck.IsChecked, DaySatCheck.IsChecked, DaySunCheck.IsChecked,
        ScheduleWindowsBox.Text);

    private string ModelSignature() => string.Join('\u001f',
        ModelNameBox.Text, ModelNoteBox.Text, ModelWebsiteBox.Text,
        ModelEndpointBox.Text, ModelListEndpointBox.Text, ModelAutoCompleteCheck.IsChecked,
        ModelCodePicker.Text,
        (ModelFormatBox.SelectedItem as Choice<ApiFormat>)?.Value,
        ModelStableCheck.IsChecked,
        CurrentKey());

    // ------------------------------------------------------------------ 保险箱

    private void OpenVault_Click(object sender, RoutedEventArgs e) => Vault.Show(_state);

    /// <summary>
    /// Reads the seal and heartbeat so the tab is honest before anything is clicked. Opening a
    /// session does no key derivation, and the one write it can do — recording a seal the files
    /// already justify — is a write that is supposed to happen anyway.
    /// </summary>
    private void RefreshVaultStatus()
    {
        try
        {
            using var session = new VaultSession(_state.ConfigPath, null, _state.VaultSecret, _state.ClearSeal);

            VaultStatusText.Text = session.IsSealed
                ? $"状态：已封印 —— {session.SealReason}\n只有监护码B 能打开。"
                : session.Phase switch
                {
                    VaultPhase.NotSetUp => "状态：还没有建立保险箱。",
                    VaultPhase.Locked => "状态：已建立，当前锁着。",
                    _ => "状态：已打开。",
                };
        }
        catch (Exception ex)
        {
            VaultStatusText.Text = $"状态：读不出来 —— {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ 概览

    private void RefreshAll()
    {
        // 先记下"原来选的是谁"：刷新之后配置可能已经是另一批对象了（被拒之后服务会把存下来的
        // 那份重新读一遍），旧对象不在新的列表里，选中项会全丢 —— 表单也就没了内容。
        var selection = Selected();

        ConfigPathText.Text = _state.ConfigPath;
        DryRunCheck.IsChecked = _state.Config.Ui.DryRun;
        RefreshTargetList();
        RefreshPolicyList();
        RefreshScheduleList();
        RefreshModelList();
        RefreshModelStatus();
        RefreshRuleTargetPicker();
        RefreshDiagTargets();
        RefreshUnlockPanel();
        RefreshPauseStatus();
        UpdateStatus();

        RestoreSelection(selection);
    }

    /// <summary>现在选中的是哪些条目。只记 Id —— 对象随时可能被换掉。</summary>
    private sealed record Selection(string? Target, string? Rule, string? Policy, string? Schedule, string? Model);

    private Selection Selected() =>
        new(_selectedTarget?.Id, _selectedRule?.Id, _selectedPolicy?.Id, _selectedSchedule?.Id, _selectedModel?.Id);

    private void UpdateStatus()
    {
        var running = _state.Monitor.IsRunning;
        var config = _state.Config;

        // 颜色从主题取，不用 .NET 那几个自带色（SeaGreen 之类的偏蓝绿，和这套暖调中性色放在一起很跳）。
        // 状态点、标题字、卡片底色三处一起变 —— 一眼扫过就能知道现在是什么状态。
        string headline, detail, tone, surface;

        if (_state.ConfigLoadError is not null)
        {
            (headline, detail, tone, surface) =
                ("配置解析失败", "没有在监控任何东西 —— 去「日志」页看具体错误。", "Danger", "DangerSurface");
        }
        else if (running)
        {
            (headline, detail, tone, surface) = ("监控运行中",
                config.Ui.DryRun ? "安全模式：只记录，不执行动作。" : "已启用动作。", "Success", "SuccessSurface");
        }
        else
        {
            (headline, detail, tone, surface) =
                ("监控已停止", "规则现在不会生效。", "TextSecondary", "Surface");
        }

        StatusText.Text = headline;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(tone);
        StatusDetailText.Text = detail;
        StatusDot.Fill = (System.Windows.Media.Brush)FindResource(tone);
        StatusCard.Background = (System.Windows.Media.Brush)FindResource(surface);

        // 空列表盖一层提示，一有内容就撤掉。
        RecentEmpty.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TargetCountText.Text = config.Targets.Count.ToString();
        RuleCountText.Text = config.Targets.Sum(t => t.Rules.Count).ToString();
        PolicyCountText.Text = config.Policies.Count.ToString();

        var error = _state.Monitor.LastError is { } e ? $"  ·  最近错误: {e}" : "";

        // 被跳过的窗口不是小事：那段时间里它里面的内容没被看过。所以它得有自己的一行，
        // 而不是混在"最近错误"里 —— 那不是错误，是一条正在生效的绕过路径。
        var skipped = _state.Monitor.Reader is { MutedCount: > 0 } reader
            ? $"  ·  {reader.MutedCount} 个窗口读不动，已暂时跳过"
            : "";

        StatusBarText.Text = $"{headline}  ·  {config.Targets.Count} 个目标  ·  {config.Policies.Count} 个策略{error}{skipped}";

        // 「有未保存的修改」跟着每秒刷新一次。放在这里而不是放在每个输入框的事件里：Change 事件
        // 覆盖不到"程序改了控件"（比如下拉框重绑），而这个判断必须和界面上看到的一模一样。
        RefreshDirtyForms();
    }

    private void OnOutcome(MonitorOutcome outcome)
    {
        Dispatcher.Invoke(() =>
        {
            _recent.Insert(0, $"[{outcome.Now:HH:mm:ss}] {outcome.Target.DisplayName} | {outcome.Stage} | " +
                              $"{outcome.Verdict.Reason ?? "放行"} | {outcome.Detail} | {outcome.Snapshot.Url}");
            while (_recent.Count > 200)
                _recent.RemoveAt(_recent.Count - 1);
        });
    }

    private void OnMonitorFailed(string message) => Dispatcher.Invoke(UpdateStatus);

    private void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        _state.Start();
        UpdateStatus();
    }

    private void StopMonitor_Click(object sender, RoutedEventArgs e)
    {
        // 界面变灰是给人看的，这一道是给"万一"用的。
        //
        // 为什么这件事非拦不可：「停止监控」只取消本地的监控循环，**不写配置、不经过服务、
        // 不碰令牌判定** —— 它是唯一一个完全在受管那条路之外的放宽动作。而且它停掉之后服务
        // 也救不回来：心跳就在这个循环里发，循环一停心跳就停，服务 20 秒后拉新 Agent，
        // 新 Agent 撞上单实例锁秒退（旧进程还活着占着它），于是永远拉不起来。
        if (_state.UnlockConfigured)
        {
            MessageBox.Show(this,
                "解锁保护开着，所以停不了监控。\n\n"
                + "能停就等于绕过了它 —— 所有规则会立刻不再生效，而且不需要任何令牌。\n\n"
                + "真想停的话，先在「解锁保护」那一块点「关闭解锁保护…」（那一步要令牌）。",
                "停止监控", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _state.Monitor.Stop();
        UpdateStatus();
    }

    /// <summary>
    /// Every edit in this window saves immediately, which means every one of them can be refused —
    /// including a refusal that needs an unlock token. One method to save, so there is one method
    /// that has to report it.
    ///
    /// <para>
    /// Returns whether it actually went in. The callers that ignore it are the ones where a refusal is
    /// already reported loudly; the表单 uses it to decide whether an edit still counts as applied.
    /// </para>
    /// </summary>
    private bool SaveAndReport()
    {
        var saved = _state.SaveConfig();

        if (saved)
            UnlockTokenText.Clear();
        else
            UnlockPanelError.Text = "";   // the refusal is already reported above the panel

        // Timestamped, and in the status bar rather than on one tab. This line is the only
        // confirmation a save produces, and a confirmation that reads the same every time is
        // indistinguishable from a button that does nothing.
        SaveStatusText.Text = saved
            ? $"✓ 配置已保存 {DateTime.Now:HH:mm:ss}"
            : "✗ 配置没有保存";

        SaveStatusText.Foreground = saved
            ? System.Windows.Media.Brushes.DarkGreen
            : System.Windows.Media.Brushes.Firebrick;

        if (saved)
        {
            ShowValidationResult();
            RefreshUnlockPanel();
            UpdateStatus();
            return true;
        }

        // The config in memory has just been put back to what is stored, so everything drawn from it
        // has to be drawn again. Without this the window keeps showing the edit that was refused —
        // a target still unticked, a pause still switched on — and the only honest thing on screen
        // is the error message.
        RefreshAll();
        ShowValidationResult();
        ReportRefusedSave();
        return false;
    }
    /// <summary>
    /// Says out loud that the edit did not take.
    ///
    /// <para>
    /// A refused save is the one outcome that must never be quiet: the change is on screen, the user
    /// believes it happened, and it did not. The message used to exist only on the overview tab,
    /// which is no help at all when the change was made on the targets tab — the screen you were
    /// looking at simply did nothing.
    /// </para>
    /// </summary>
    private void ReportRefusedSave()
    {
        if (_state.PendingUnlockRequest is not null)
        {
            // The unlock code and the box to paste a token into are on the overview tab. Go there
            // rather than describing where to find them.
            SelectTab(OverviewTab);

            RefuseToUser(
                "这次修改会放宽限制，所以没有保存。\n\n" +
                Widenings() +
                "放宽需要一份在别的设备上用私钥签发的令牌。解锁码已经放在「概览」页上了 —— " +
                "复制它，拿去签名，再把令牌贴回来。\n\n" +
                "界面已经还原成实际生效的样子。那次改动本身留着，贴回令牌就会重新提交。");

            return;
        }

        RefuseToUser("配置没有保存：\n\n" + (_state.SaveError ?? "原因不明"));
    }

    /// <summary>
    /// 弹出"这次修改没有保存"。
    ///
    /// <para>
    /// 做成可替换的，和「有未保存的修改」那个询问同一个理由：离线检查里出现真模态框就没人点，
    /// 整个检查挂死在那儿 —— 而且看起来像"卡住了"，看不出原因。更麻烦的是**拒绝的理由就在
    /// 这个框里**，检查读不到它，只能靠猜。
    /// </para>
    /// </summary>
    public Action<string> RefuseToUser
    {
        // 惰性取默认实现：字段初始值设定项里引不到实例方法。
        get => _refuseToUser ??= ShowRefusal;
        set => _refuseToUser = value;
    }

    private Action<string>? _refuseToUser;

    private void ShowRefusal(string message) =>
        MessageBox.Show(this, message, "这次修改没有保存", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// 点了按钮，却没有可操作的对象 —— 说出来，别静默返回。
    ///
    /// <para>
    /// 这些地方原来都是一句 <c>return;</c>：没选中条目的时候点「新建」或者「应用修改」，
    /// 不保存、不提示，而屏幕上还留着你打的字。人看到的是"点了没反应""存不进去"，
    /// 程序里却一次异常都没有 —— 比报错难查得多。（真出过一次：界面里配好了 http，
    /// 文件里一个字都没有。）
    /// </para>
    /// </summary>
    /// <summary>
    /// "放宽的是哪一处" —— 直接来自服务判定时给出的说法。
    ///
    /// <para>
    /// 没有它的时候，这里只能说一句"会放宽限制"，而人看着屏幕上自己刚动的那一处，
    /// 只会想"我没改什么不该改的啊"。真正放宽的往往是顺手动的另一处：改了优先级、
    /// 取消勾选了一个字段、把匹配内容删了一条。
    /// </para>
    /// </summary>
    private string Widenings()
    {
        const string marker = "放宽的地方：";

        var error = _state.SaveError;

        if (string.IsNullOrWhiteSpace(error))
            return "";

        var at = error.IndexOf(marker, StringComparison.Ordinal);

        return at < 0 ? "" : error[(at + marker.Length)..].Trim() + "\n\n";
    }

    /// <summary>
    /// 把手里选中的条目认回**当前**那份配置上去。
    ///
    /// <para>
    /// 心跳发现配置版本变了 —— <b>包括界面自己刚存成功的那一次</b> —— 后台就会叫一次
    /// <c>ReloadConfig()</c>，把整份配置换成一批新对象（那条路只在监控线程上走，界面收不到通知）。
    /// 界面手里的 <c>_selectedRule</c> 这时候就成了**孤儿**：往它里面写，而保存时发出去的却是
    /// 新配置 —— 改动不在里面，服务看"什么都没变"，照样回一句成功。屏幕上写着"✓ 配置已保存"，
    /// 文件里一个字都没有。用户看到的就是"界面上加了没效果"。
    /// </para>
    /// <para>
    /// 每个会改动配置的地方都先过这一道。认不回来（真被别处删了）就让它变成 null，
    /// 由 <see cref="SayNothingSelected"/> 出声 —— 不许静默写进废纸。
    /// </para>
    /// </summary>
    private void ReattachSelection()
    {
        _selectedTarget = ReferenceEquals(_selectedTarget, _stagedTarget)
            ? _stagedTarget
            : Current(_selectedTarget, _state.Config.Targets, t => t.Id);

        // 规则在目标里面找：**跨目标按 Id 认会认错人** —— 这份配置里 codex 和 doubao
        // 各有一条都叫 rule 的规则。
        _selectedRule = ReferenceEquals(_selectedRule, _stagedRule)
            ? _stagedRule
            : _selectedTarget is null
                ? null
                : Current(_selectedRule, _selectedTarget.Rules, r => r.Id);

        _selectedPolicy = Current(_selectedPolicy, _state.Config.Policies, p => p.Id);
        _selectedSchedule = Current(_selectedSchedule, _state.Config.Schedules, s => s.Id);
        // 服务商例外：**新建一个服务商时它按设计还没进配置**（要等点「应用修改」才进去），
        // 所以"配置里没有"不等于"这个是孤儿" —— 那种情况必须留着手里的新对象，
        // 否则刚填好的地址和钥匙会被这一句丢掉。
        _selectedModel = Current(_selectedModel, _state.Config.Models, m => m.Id, keepIfMissing: true);
    }

    private static T? Current<T>(
        T? held, IEnumerable<T> pool, Func<T, string> id, bool keepIfMissing = false) where T : class
    {
        if (held is null)
            return null;

        foreach (var candidate in pool)
        {
            if (ReferenceEquals(candidate, held))
                return held;   // 还在里面 —— 一个字节都不用动
        }

        var wanted = id(held);
        var found = pool.FirstOrDefault(candidate => id(candidate) == wanted);

        // 认回来的是"同一个条目、换了对象"的孤儿就换掉；Id 根本不在配置里，那是两回事：
        // 要么它刚被别处删了（清空，让 SayNothingSelected 出声），要么它本来就是个还没保存的新条目。
        if (found is not null)
            return found;

        return keepIfMissing ? held : null;
    }

    private void SayNothingSelected(string what) =>
        RefuseToUser($"没有选中任何{what}，所以这次操作没有发生，什么都不会改。\n\n先在列表里点中一条，再试一次。");

    // ------------------------------------------------------------------ 解锁令牌

    /// <summary>
    /// How an unlock code becomes a token. Kept in one place because three surfaces say it — the
    /// panel for an edit, the panel for closing the protection, and the clipboard confirmation — and
    /// they had already drifted: all three still pointed at the CLI, which is the workflow the
    /// offline HTML page exists to replace.
    /// </summary>
    private const string SigningHint =
        "在保管私钥的那台设备上打开 sentinel-unlock.html，把解锁码粘进去签名，再把令牌贴回这里。";

    /// <summary>
    /// One panel behind two reasons to need a token: an edit that would weaken things, and a
    /// deliberate decision to take the rule down. Same code, same signature, same paste box — only
    /// the sentence and the button differ.
    /// </summary>
    private void RefreshUnlockPanel()
    {
        var release = _state.PendingReleaseRequest;
        var pending = release ?? _state.PendingUnlockRequest;

        UnlockPanel.Visibility = string.IsNullOrEmpty(pending) ? Visibility.Collapsed : Visibility.Visible;
        ReleaseButton.Visibility = _state.UnlockConfigured ? Visibility.Visible : Visibility.Collapsed;

        // 两条路互斥：开着才谈得上关，没开才谈得上开。
        EnableButton.Visibility = _state.UnlockConfigured ? Visibility.Collapsed : Visibility.Visible;

        if (_state.UnlockConfigured)
            EnablePanel.Visibility = Visibility.Collapsed;

        // 「停止监控」也要跟着它。
        //
        // 它是唯一一个完全在受管那条路之外的放宽动作：只取消本地的监控循环，不写配置、
        // 不经过服务、不碰令牌判定。所以保护开着的时候必须关掉它，否则解锁保护形同虚设。
        // 和托盘「退出」用的是同一个判断（见 TrayIcon.RefreshExitItem）。
        StopMonitorButton.IsEnabled = !_state.UnlockConfigured;
        StopMonitorButton.ToolTip = _state.UnlockConfigured
            ? "解锁保护开着的时候停不了 —— 能停就等于绕过了它。真想停，先点「关闭解锁保护…」（要令牌）。"
            : null;

        if (!string.IsNullOrEmpty(pending))
        {
            var closing = release is not null;

            UnlockRequestText.Text = pending;
            UnlockPanelTitle.Text = closing ? "关闭解锁保护需要令牌" : "这次修改会放宽限制，需要解锁令牌";
            UnlockRetryButton.Content = closing ? "用令牌关闭保护" : "用令牌重试保存";

            UnlockPanelHint.Text = closing
                ? "要关掉保护，" + SigningHint
                  + "关掉之后所有放宽都不再需要令牌；想重新开启，把公钥文件放回配置目录就行。"
                : Widenings() + "刚才那次改动没有被采用，界面已经还原成现在实际生效的样子 —— 你可以在别的页签上确认。"
                  + "要接着做，" + SigningHint
                  + "令牌对应的是那一次的具体改动，而且只能用一次。";
        }

        UnlockStatusText.Text = _state.UnlockConfigured
            ? "解锁保护：已启用 —— 放宽限制需要机器外签发的令牌；收紧（加规则、延长生效时间、缩短间隔）永远不需要。"
            : $"解锁保护：{_state.UnlockStatus ?? "未配置"} —— 现在还不需要令牌。";
    }

    private void CopyUnlockRequest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(UnlockRequestText.Text);
            UnlockPanelError.Text = "解锁码已复制。" + SigningHint;
        }
        catch (Exception ex)
        {
            // The clipboard is shared with every other process and can refuse at any time.
            UnlockPanelError.Text = $"复制失败，请手动选中复制：{ex.Message}";
        }
    }

    // ------------------------------------------------------------------ 开启解锁保护

    /// <summary>
    /// 把生成密钥对的页面交给默认浏览器。这是**一次性**的设置步骤：以后真正要一直带着的是签名页，
    /// 但这个页面留着无妨。
    /// </summary>
    private void OpenKeygenPage_Click(object sender, RoutedEventArgs e) =>
        OpenToolPage(ToolPage.TryOpenKeygen, "生成密钥对");

    /// <summary>把签名页交给默认浏览器。每次都走这一步，所以这个页面要一直留着。</summary>
    private void OpenSignPage_Click(object sender, RoutedEventArgs e) =>
        OpenToolPage(ToolPage.TryOpenSign, "签名换令牌");

    private void OpenToolPage(OpenAttempt open, string title)
    {
        if (open(out var error))
            return;

        // 打不开就说清楚：一个什么都不做的按钮比没有按钮更糟。
        MessageBox.Show(this, error, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private delegate bool OpenAttempt(out string? error);

    /// <summary>Opens the paste box. Nothing is sent yet — the key is checked before it leaves here.</summary>
    private void RequestEnable_Click(object sender, RoutedEventArgs e)
    {
        EnablePanel.Visibility = Visibility.Visible;
        EnableButton.Visibility = Visibility.Collapsed;
        EnableError.Text = "";
        PublicKeyText.Focus();
    }

    private void CancelEnable_Click(object sender, RoutedEventArgs e)
    {
        EnablePanel.Visibility = Visibility.Collapsed;
        EnableButton.Visibility = Visibility.Visible;
        EnableError.Text = "";
    }

    /// <summary>Reads a .pem the user picked — the phone page offers the key as a download for this.</summary>
    private void LoadPublicKeyFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择公钥文件",
            Filter = "公钥 / 文本 (*.pem;*.pub;*.txt)|*.pem;*.pub;*.txt|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            PublicKeyText.Text = File.ReadAllText(dialog.FileName);
            EnableError.Text = "";
        }
        catch (Exception ex)
        {
            EnableError.Text = $"读不了这个文件：{ex.Message}";
        }
    }

    private void ConfirmEnable_Click(object sender, RoutedEventArgs e)
    {
        // 锁上之后再想给某条规则加「允许暂停」，就得动令牌了 —— 因为那一步是放宽。
        // 也就是说：想让暂停这个口子在强力模式下还能用，就得在锁之前开好。
        // 这个先后顺序不写出来是猜不到的，所以在这里拦一下。
        var pausable = _state.Config.Targets.SelectMany(t => t.Rules).Count(r => r.Pause.Enabled);

        if (pausable == 0)
        {
            var answer = MessageBox.Show(this,
                "还没有任何一条规则开着「允许暂停」。\n\n" +
                "暂停是唯一不需要令牌就能让一步的口子，但它得先开好：保护一旦打开，"
                + "再想给某条规则加暂停额度就需要令牌了。\n\n" +
                "（如果你本来就不打算用暂停，直接继续。）\n\n" +
                "现在开启保护吗？",
                "开启解锁保护", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
                return;
        }

        if (_state.EnableProtection(PublicKeyText.Text, out var message))
        {
            PublicKeyText.Text = "";
            EnablePanel.Visibility = Visibility.Collapsed;
            EnableError.Text = "";

            RefreshAll();
            MessageBox.Show(this, message, "解锁保护", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Refused: keep the box open with what they pasted, so the fix is one edit away.
        EnableError.Text = message;
    }

    /// <summary>The proactive door: asking to take the rule down, without pretending it is an edit.</summary>
    private void RequestRelease_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "关闭解锁保护？\n\n" +
            "关掉之后，放宽限制、停掉目标、删掉规则 —— 所有修改都不再需要令牌，谁坐在这台机器前都能改。\n\n" +
            "要重新打开，在「概览」页点「开启解锁保护…」，把公钥粘回去就行（不需要令牌）。\n\n" +
            "需要一份用私钥签发的令牌才能关。现在去要吗？",
            "关闭解锁保护", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
            return;

        if (_state.RequestRelease(out var message))
        {
            SelectTab(OverviewTab);
            RefreshAll();
            return;
        }

        MessageBox.Show(this, message, "关闭解锁保护", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RetryWithToken_Click(object sender, RoutedEventArgs e)
    {
        var closing = _state.PendingReleaseRequest is not null;
        string? message;

        var ok = closing
            ? _state.ConfirmRelease(UnlockTokenText.Text, out message)
            : _state.RetryPending(UnlockTokenText.Text, out message);

        if (ok)
            UnlockTokenText.Clear();

        SaveStatusText.Text = ok
            ? $"✓ {(closing ? "保护已关闭" : "配置已保存")} {DateTime.Now:HH:mm:ss}"
            : "✗ 没有生效";

        SaveStatusText.Foreground = ok
            ? (System.Windows.Media.Brush)FindResource("Success")
            : (System.Windows.Media.Brush)FindResource("Danger");

        // The panel's own line is for a token that was tried and did not work; the first refusal has
        // already been said above it, and saying it three times helps nobody.
        UnlockPanelError.Text = ok ? "" : (message ?? "");

        // 但状态栏和一行小字是会被漏掉的 —— 贴完令牌点了「用令牌重试」而什么都没发生，
        // 看起来就像按钮坏了。别的十六个保存入口失败都会弹窗，这里也该一样。
        if (!ok)
        {
            MessageBox.Show(this,
                message ?? "令牌没有被接受，所以这次修改还是没有生效。",
                closing ? "关闭解锁保护" : "用令牌重试", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshAll();
        ShowValidationResult();
    }

    /// <summary>Puts away a request that is not going to be answered.</summary>
    private void CancelUnlock_Click(object sender, RoutedEventArgs e)
    {
        _state.CancelPendingUnlock();
        RefreshAll();
    }

    // ------------------------------------------------------------------ 暂停

    /// <summary>
    /// Spends the selected rule's pause, with no unlocking anywhere in the path. The allowance is
    /// on the rule and the bookkeeping is with the Service — where the user cannot write it — so
    /// this is a bounded exception rather than a way round the rule.
    /// </summary>
    private void PauseRule_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule is null)
        {
            RefreshPauseStatus("先在左边选一条规则。", ok: false);
            return;
        }

        var ok = _state.PauseRule(_selectedRule.Id, out var message);

        // The list itself does not change — only the button caption and the line under it.
        RefreshPauseStatus(message, ok);
    }

    private void RefreshPauseStatus(string? extra = null, bool ok = true)
    {
        var options = _selectedRule?.Pause ?? new PauseOptions();
        var pauses = _state.Pauses;
        var now = DateTimeOffset.Now;

        PauseButton.Content = options.Enabled ? $"暂停 {options.For(now).Minutes} 分钟" : "暂停";
        PauseButton.IsEnabled = options.Enabled && _selectedRule is not null;

        var lines = new List<string>();

        if (_selectedRule is not { } rule)
        {
            // 一个灰着的按钮不解释自己为什么灰，比根本没有这个按钮更让人恼火。
            lines.Add("先在左边选一条规则，再看能不能暂停。");
        }
        else if (!options.Enabled)
        {
            lines.Add($"「{rule.Name}」还没允许暂停 —— 在下面「暂停」一节里勾上「允许暂停这条规则」，再点「应用修改」。"
                    + "开这个开关算放宽（保护开着的时候要令牌），但开好之后，暂停本身不需要令牌。");
        }
        else if (pauses.IsPaused(rule.Id, now))
        {
            lines.Add($"「{rule.Name}」正在暂停中 —— 这一轮到 {pauses.PausedUntil(rule.Id, now):HH:mm} 结束。");
        }
        else if (pauses.AvailableAt(rule.Id, options, now) is { } next)
        {
            // 走到这里只剩一种"还不能停"：这一期（今天/本周/本月）的次数用光了。
            lines.Add($"「{rule.Name}」现在停不了 —— 「{PauseState.Scope(options, now)}」的次数已经用完了，"
                    + $"要等到 {next:MM-dd HH:mm}。");
        }
        else
        {
            var budget = options.For(now);

            lines.Add($"「{rule.Name}」现在可以暂停 {budget.Minutes} 分钟"
                    + $"（{PauseState.Scope(options, now)}还能停 {pauses.LeftNow(rule.Id, options, now)} 次）。");
        }

        if (extra is not null)
            lines.Insert(0, extra);

        PauseText.Text = string.Join("\n", lines);
        PauseText.Foreground = !ok
            ? (System.Windows.Media.Brush)FindResource("Danger")
            : extra is null
                ? (System.Windows.Media.Brush)FindResource("TextSecondary")
                : (System.Windows.Media.Brush)FindResource("Success");
    }

    private void ValidateConfig_Click(object sender, RoutedEventArgs e) => ShowValidationResult(check: true);

    /// <summary>
    /// Draws the state of the config.
    ///
    /// <para>
    /// The earlier version returned early when a save had failed, which meant the one time you most
    /// wanted a check — right after a refusal — the button printed the refusal again instead of
    /// checking anything. A stale <c>SaveError</c> is still worth mentioning, but above the answer
    /// rather than instead of it.
    /// </para>
    /// </summary>
    /// <param name="check">
    /// True when the user pressed 校验配置 rather than when a save just happened. Only the wording
    /// differs, but a check has to look like it ran even when it finds the same thing twice.
    /// </param>
    private void ShowValidationResult(bool check = false)
    {
        var lines = new List<string>();

        if (_state.SaveError is { } saveError)
            lines.Add($"⚠ 上一次保存没有成功：{saveError}");

        if (_state.ConfigLoadError is { } loadError)
        {
            // A file that failed to parse is the most misleading state there is: the Agent looks like
            // it is running while monitoring nothing.
            lines.Add(
                "⚠ 配置文件解析失败，现在跑的是空白配置 —— 等于没有监控任何东西。\n" +
                $"文件：{_state.ConfigPath}\n" +
                $"原因：{loadError}\n" +
                "改好文件后重新打开 Sentinel。注意：在那之前，任何一次编辑都会立刻把当前这份空白配置写回去，覆盖你改好的文件。");
        }

        var issues = _state.Validate();
        var errors = issues.Count(issue => issue.Severity == IssueSeverity.Error);
        var warnings = issues.Count - errors;

        var body = issues.Count == 0
            ? "没有错误，也没有提醒。"
            : string.Join(Environment.NewLine, issues.Select(issue => "• " + issue));

        // Timestamped, like the save line: a check that prints the same sentence every time is
        // indistinguishable from a button that does nothing, which is exactly how this one read.
        lines.Add(check
            ? $"校验于 {DateTime.Now:HH:mm:ss} —— {errors} 个错误，{warnings} 条提醒。\n{body}"
            : body);

        ValidationText.Text = string.Join("\n\n", lines);

        ValidationText.Foreground = errors > 0 || _state.SaveError is not null || _state.ConfigLoadError is not null
            ? System.Windows.Media.Brushes.Firebrick
            : warnings > 0
                ? System.Windows.Media.Brushes.DarkGoldenrod
                : System.Windows.Media.Brushes.DarkGreen;
    }

    private void DryRun_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        var wanted = DryRunCheck.IsChecked == true;

        // A refresh sets this box while drawing the current state, and saving that would be saving
        // what is already saved — once per refresh, including the refresh a refused save causes.
        if (wanted == _state.Config.Ui.DryRun)
            return;

        _state.Config.Ui.DryRun = wanted;
        SaveAndReport();
        UpdateStatus();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.GetDirectoryName(_state.ConfigPath)!);

    // ------------------------------------------------------------------ 目标

    /// <summary>
    /// 还没保存的新目标（点「新建」造出来的草稿）。
    ///
    /// <para>
    /// 和规则那条草稿同一个道理：以前点「新建」会立刻把一条**没有进程名、也没有标题条件**的
    /// 目标写进配置，而校验把那种目标判成错误（"它会匹配所有窗口"）—— 于是保存被拒，
    /// 用户根本建不出目标，看到的只是一句"配置没有保存"。
    /// </para>
    /// </summary>
    private Target? _stagedTarget;

    /// <summary>目标列表该显示什么：配置里的 + 末尾那条还没保存的草稿。</summary>
    private IReadOnlyList<Target> TargetRows() =>
        _stagedTarget is null ? _state.Config.Targets : _state.Config.Targets.Append(_stagedTarget).ToList();

    /// <summary>「这个目标还没进配置」那一行要不要显示。</summary>
    private void RefreshTargetStagedMark() =>
        TargetStagedMark.Visibility = ReferenceEquals(_selectedTarget, _stagedTarget)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void RefreshTargetList()
    {
        // 先记住要选谁：重绑 ItemsSource 会把选中项打到 -1，而那次 SelectionChanged 会把
        // _selectedTarget 清成 null —— 之后再问"原来选的是谁"就晚了。
        // 按 Id 认回来：配置被整体换过之后（保存被拒时就是这样），手里的对象已经不在新列表里。
        // 草稿不在配置里，按 Id 认不回来，所以单独照顾一下。
        var wanted = ReferenceEquals(_selectedTarget, _stagedTarget)
            ? _stagedTarget
            : _selectedTarget is { } current
                ? _state.Config.Targets.FirstOrDefault(t => t.Id == current.Id)
                : null;

        Programmatic(() =>
        {
            TargetList.ItemsSource = null;
            TargetList.ItemsSource = TargetRows();

            // 选回来。不选的话，光点一下「应用修改」就把正在编辑的东西从眼前弄丢了（以前就是这样）：
            // 选中项掉到 -1，表单跟着变灰。
            if (wanted is not null)
                TargetList.SelectedItem = wanted;
        });
    }

    private void TargetList_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // 换条目就是离开现在这个表单。脏的话先问一句，问不住就把选中项拨回去。
        if (IgnoreRevert())
            return;

        if (!ConfirmLeaveDirtyForm())
        {
            RevertSelection(TargetList, _selectedTarget);
            return;
        }

        _selectedTarget = TargetList.SelectedItem as Target;
        TargetForm.IsEnabled = _selectedTarget is not null;
        RefreshTargetStagedMark();

        // 换了目标，手里那条还没保存的规则草稿属于上一个目标 —— 丢掉。
        _stagedRule = null;

        if (_selectedTarget is null)
        {
            _targetForm.Clear();
            return;
        }

        TargetNameBox.Text = _selectedTarget.DisplayName;
        TargetProcessBox.Text = string.Join(", ", _selectedTarget.ProcessNames);
        TargetTitleBox.Text = _selectedTarget.WindowTitlePattern ?? "";
        ScanUrlCheck.IsChecked = _selectedTarget.Reader.ScanUrl;
        ScanTitleCheck.IsChecked = _selectedTarget.Reader.ScanTitle;
        ScanBodyCheck.IsChecked = _selectedTarget.Reader.ScanBody;
        IntervalBox.Text = _selectedTarget.Reader.IntervalMs.ToString();
        TargetEnabledCheck.IsChecked = _selectedTarget.Enabled;

        _targetForm.Reload();
    }

    private void NewTarget_Click(object sender, RoutedEventArgs e)
    {
        // 脏表单上点「新建」也是离开 —— 先问一句再动。
        if (!ConfirmLeaveDirtyForm())
            return;

        // **不保存。** 只在内存里造一条草稿：一条还没有进程名的新目标现在写进配置，
        // 校验会（正确地）判它"会匹配所有窗口"并拒绝整次保存 —— 于是人连目标都建不出来。
        _stagedTarget = new Target
        {
            Id = UniqueTargetId("target"),
            DisplayName = "新目标",
            Reader = new ReaderOptions { IntervalMs = 1000 },
        };

        _stagedRule = null;
        _selectedTarget = _stagedTarget;
        RefreshTargetList();
        RefreshRuleTargetPicker();
        TargetForm.IsEnabled = true;
        RefreshDirtyForms();
    }

    private void DeleteTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveDirtyForm())
            return;
        ReattachSelection();

        if (_selectedTarget is null)
        {
            SayNothingSelected("目标");
            return;
        }

        // 还没保存的草稿：丢掉就是丢掉，配置里没有它，也不用保存。
        if (ReferenceEquals(_selectedTarget, _stagedTarget))
        {
            _stagedTarget = null;
            _selectedTarget = null;
            _stagedRule = null;
            TargetForm.IsEnabled = false;
            _targetForm.Clear();
            RefreshTargetList();
            RefreshRuleTargetPicker();
            return;
        }

        if (MessageBox.Show($"删除目标「{_selectedTarget.DisplayName}」？", "Sentinel",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _state.Config.Targets.Remove(_selectedTarget);
        SaveAndReport();
        _selectedTarget = null;
        TargetForm.IsEnabled = false;
        RefreshTargetList();
        RefreshRuleTargetPicker();
    }

    private void ApplyTarget_Click(object sender, RoutedEventArgs e) => ApplyTargetForm();

    /// <summary>把表单写进选中的目标并保存。返回"真的存下去了没有"。</summary>
    private bool ApplyTargetForm()
    {
        ReattachSelection();

        // 这是点「新建」造出来的草稿吗？如果是，这**一次**点应用就是它进配置的那一刻。
        if (_selectedTarget is null)
        {
            SayNothingSelected("目标");
            return false;
        }

        // 这是点「新建」造出来的草稿吗？如果是，这**一次**点应用就是它进配置的那一刻。
        var isNew = ReferenceEquals(_selectedTarget, _stagedTarget);

        _selectedTarget.DisplayName = TargetNameBox.Text.Trim();
        _selectedTarget.ProcessNames = TargetProcessBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var titlePattern = TargetTitleBox.Text.Trim();
        _selectedTarget.WindowTitlePattern = string.IsNullOrEmpty(titlePattern) ? null : titlePattern;

        _selectedTarget.Reader.ScanUrl = ScanUrlCheck.IsChecked == true;
        _selectedTarget.Reader.ScanTitle = ScanTitleCheck.IsChecked == true;
        _selectedTarget.Reader.ScanBody = ScanBodyCheck.IsChecked == true;
        _selectedTarget.Reader.IntervalMs = ParseInt(IntervalBox.Text, 1000, 0, 600_000);
        _selectedTarget.Enabled = TargetEnabledCheck.IsChecked == true;

        if (isNew && !_state.Config.Targets.Contains(_selectedTarget))
            _state.Config.Targets.Add(_selectedTarget);

        var saved = SaveAndReport();

        if (isNew)
        {
            if (saved)
            {
                _stagedTarget = null;      // 进去了，不再是草稿
            }
            else
            {
                // 没存进去（比如它还没有进程名）：从配置里摘回去，仍旧当草稿拿着 ——
                // 用户填的东西不能丢。被拒的原因（"它会匹配所有窗口"）弹窗里会说。
                _state.Config.Targets.Remove(_selectedTarget);
            }
        }

        RefreshTargetList();
        ShowValidationResult();
        _targetForm.Reload();

        return saved;
    }

    private string UniqueTargetId(string baseId)
    {
        if (_state.Config.Targets.All(t => t.Id != baseId))
            return baseId;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseId}-{i}";
            if (_state.Config.Targets.All(t => t.Id != candidate))
                return candidate;
        }
    }

    // ------------------------------------------------------------------ 规则

    /// <summary>
    /// 还没保存的那条新规则（点「新建」造出来的草稿）。
    ///
    /// <para>
    /// 它<b>不在配置里</b> —— 这正是关键。以前点「新建」会立刻把一条空规则写进配置，于是用户在表单里
    /// 把匹配方式从「关键词」改成「模型鉴定」再点应用时，服务拿它和已经存下来的那条空规则一比：
    /// 同一条规则换了匹配方式，一律算放宽，于是要令牌 —— 而人只是想把这条规则填完，
    /// 什么也没想放宽。空规则还会一直躺在配置文件里（"我东西都还没填，怎么就进去了"）。
    /// </para>
    /// <para>
    /// 草稿只在<b>点「应用修改」</b>时才进配置 —— 和「大语言模型」那一页同一套做法。
    /// </para>
    /// </summary>
    private Rule? _stagedRule;

    /// <summary>规则列表该显示什么：配置里的规则 + 末尾那条还没保存的草稿。</summary>
    private IReadOnlyList<Rule> RuleRows()
    {
        var rules = _selectedTarget?.Rules;

        if (rules is null)
            return _stagedRule is null ? Array.Empty<Rule>() : new[] { _stagedRule };

        return _stagedRule is null ? rules : rules.Append(_stagedRule).ToList();
    }

    /// <summary>
    /// 「这条还没进配置」那一行要不要显示。
    ///
    /// <para>
    /// 草稿在列表里和存好的规则长得一模一样 —— 不说一句，人会以为已经保存了，
    /// 那正是"我明明配了却没生效"的老毛病换个地方重演。
    /// </para>
    /// </summary>
    private void RefreshStagedMark()
    {
        RuleStagedMark.Visibility = ReferenceEquals(_selectedRule, _stagedRule)
            ? Visibility.Visible
            : Visibility.Collapsed;

        RefreshTargetStagedMark();
    }

    /// <summary>重绑规则列表，并把选中的那条认回来 —— 草稿也在列表里，所以它选得回来。</summary>
    private void ShowRules()
    {
        var wanted = _selectedRule;
        RefreshStagedMark();

        Programmatic(() =>
        {
            RuleList.ItemsSource = null;
            RuleList.ItemsSource = RuleRows();

            if (wanted is not null && RuleRows().Contains(wanted))
                RuleList.SelectedItem = wanted;
        });
    }

    private void RefreshRuleTargetPicker()
    {
        // **按 Id 认回来，不能按对象。** 保存被拒之后配置会被整体换成新读出来的对象，手里那个
        // 已经不在新列表里了 —— 把它塞进下拉框只会得到"什么都没选"，而那个 handler 会把
        // _selectedTarget 清成 null（规则是挂在目标下面的，于是规则也跟着丢了）。
        var wanted = ReferenceEquals(_selectedTarget, _stagedTarget)
            ? _stagedTarget
            : _selectedTarget is { } current
                ? _state.Config.Targets.FirstOrDefault(t => t.Id == current.Id)
                : null;

        var rows = TargetRows();

        Programmatic(() =>
        {
            RuleTargetPicker.ItemsSource = null;
            RuleTargetPicker.ItemsSource = rows;
            RuleTargetPicker.SelectedItem = wanted ?? rows.FirstOrDefault();
        });
    }

    private void RuleTargetPicker_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (IgnoreRevert())
            return;

        // 换目标会把规则列表整个换掉，右边那个表单上的东西就没了 —— 和换规则是同一种"离开"。
        if (!ConfirmLeaveDirtyForm())
        {
            RevertSelection(RuleTargetPicker, _selectedTarget);
            return;
        }

        // 换目标了，手里那条还没保存的草稿跟着走没有意义 —— 它属于上一个目标。
        _stagedRule = null;

        _selectedTarget = RuleTargetPicker.SelectedItem as Target;
        ShowRules();
        RuleForm.IsEnabled = false;
        _selectedRule = null;
        _ruleForm.Clear();
    }

    /// <summary>
    /// 「模型监督」那一支里，下面那个框装的不是匹配式，而是"我要禁止自己看什么"那句话 ——
    /// 标签和提示都得跟着换。不换的话，人会以为那里要填关键词，然后填进去一个永远不命中的词。
    /// </summary>
    private void RuleType_Changed(object sender, RoutedEventArgs e)
    {
        var isModel = (RuleTypeBox.SelectedItem as Choice<MatchKind>)?.Value == MatchKind.Model;

        RulePatternsLabel.Text = isModel
            ? "我要禁止自己看什么（用一句话说清楚）"
            : "匹配内容（每行一条）";

        RulePatternsHint.Visibility = isModel ? Visibility.Visible : Visibility.Collapsed;
        RulePatternsHint.Text = isModel
            ? "写人话，比如「和编程无关的短视频」或者「一看就停不下来的推荐流」。"
              + "模型返回一个判定和一句短原因，命中时那句原因会显示在提醒里。"
              + "每次判定都要发一次请求，所以它比关键词慢几秒、也花钱 —— 页面内容没变时不会重复判。"
            : "";
    }

    private void RuleList_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // 换规则 = 离开现在这个表单。
        if (IgnoreRevert())
            return;

        if (!ConfirmLeaveDirtyForm())
        {
            RevertSelection(RuleList, _selectedRule);
            return;
        }

        _selectedRule = RuleList.SelectedItem as Rule;
        RuleForm.IsEnabled = _selectedRule is not null;
        RefreshStagedMark();

        RefreshPauseStatus();

        if (_selectedRule is null)
        {
            _ruleForm.Clear();
            return;
        }

        RuleNameBox.Text = _selectedRule.Name;
        RuleTypeBox.SelectedItem = ((Choice<MatchKind>[])RuleTypeBox.ItemsSource!)
            .FirstOrDefault(c => c.Value == _selectedRule.Match.Type);
        FieldUrlCheck.IsChecked = _selectedRule.Match.Fields.Contains(MatchField.Url);
        FieldTitleCheck.IsChecked = _selectedRule.Match.Fields.Contains(MatchField.Title);
        FieldBodyCheck.IsChecked = _selectedRule.Match.Fields.Contains(MatchField.Body);
        RulePatternsBox.Text = string.Join(Environment.NewLine, _selectedRule.Match.Patterns);
        RulePriorityBox.Text = _selectedRule.Priority.ToString();
        RuleReminderBox.Text = _selectedRule.Reminder ?? "";
        RuleEnabledCheck.IsChecked = _selectedRule.Enabled;

        RulePauseEnabledCheck.IsChecked = _selectedRule.Pause.Enabled;

        RulePauseWorkdayTimesBox.Text = _selectedRule.Pause.Workday.Times.ToString();
        RulePauseWorkdayMinutesBox.Text = _selectedRule.Pause.Workday.Minutes.ToString();
        RulePauseWeekendTimesBox.Text = _selectedRule.Pause.Weekend.Times.ToString();
        RulePauseWeekendMinutesBox.Text = _selectedRule.Pause.Weekend.Minutes.ToString();

        RefreshPolicyChoices();
        RefreshScheduleChoices();
        RefreshPauseChoices();

        // 显式再叫一次：如果新旧规则的类型一样，SelectedItem 没变、事件不会触发，
        // 而标签可能还停在上一轮的样子。
        RuleType_Changed(this, new RoutedEventArgs());

        _ruleForm.Reload();

    }

    private void RefreshPolicyChoices()
    {
        var choices = _state.Config.Policies
            .Select(p => new Choice<string>(p.Id, $"{p.Name}（{string.Join("+", p.Actions)}）"))
            .ToList();

        RulePolicyBox.ItemsSource = choices;
        RulePolicyBox.SelectedItem = choices.FirstOrDefault(c => c.Value == _selectedRule?.PolicyId);
    }

    private void RefreshScheduleChoices()
    {
        var choices = new List<Choice<string?>> { new(null, "（始终生效）") };
        choices.AddRange(_state.Config.Schedules.Select(s => new Choice<string?>(s.Id, s.Id)));

        RuleScheduleBox.ItemsSource = choices;
        RuleScheduleBox.SelectedItem = choices.FirstOrDefault(c => c.Value == _selectedRule?.ScheduleId)
                                       ?? choices[0];
    }

    /// <summary>暂停额度的周期下拉：两套（工作日 / 周末）各一个。</summary>
    private void RefreshPauseChoices()
    {
        var choices = new List<Choice<PausePeriod>>
        {
            new(PausePeriod.Day, "每天"),
            new(PausePeriod.Week, "每周"),
            new(PausePeriod.Month, "每月"),
        };

        Select(RulePauseWorkdayPeriodBox, _selectedRule?.Pause.Workday.Period);
        Select(RulePauseWeekendPeriodBox, _selectedRule?.Pause.Weekend.Period);

        void Select(ComboBox box, PausePeriod? period)
        {
            box.ItemsSource = choices;
            box.SelectedItem = choices.FirstOrDefault(c => c.Value == (period ?? PausePeriod.Day))
                               ?? choices[0];
        }
    }

    private static void ApplyPauseBudget(PauseBudget budget, ComboBox period, TextBox times, TextBox minutes)
    {
        if (period.SelectedItem is Choice<PausePeriod> choice)
            budget.Period = choice.Value;

        budget.Times = ParseInt(times.Text, budget.Times, 1, 100);
        budget.Minutes = ParseInt(minutes.Text, budget.Minutes, 1, 24 * 60);
    }

    private void NewRule_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveDirtyForm())
            return;
        ReattachSelection();

        if (_selectedTarget is null)
        {
            SayNothingSelected("目标");
            return;
        }

        var rule = new Rule
        {
            Id = UniqueRuleId(_selectedTarget, "rule"),
            Name = "新规则",
            PolicyId = _state.Config.Policies.FirstOrDefault()?.Id ?? "",
            Match = new MatchSpec { Fields = { MatchField.Url } },
        };

        // **不保存。** 只在内存里造一条草稿，用户填完点「应用修改」才进配置 ——
        // 空规则不该躺在配置文件里，也不该逼着人为了"把类型改成模型鉴定"去拿令牌。
        _stagedRule = rule;
        _selectedRule = rule;
        ShowRules();
        RuleForm.IsEnabled = true;
        RefreshDirtyForms();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveDirtyForm())
            return;
        ReattachSelection();

        if (_selectedTarget is null || _selectedRule is null)
        {
            SayNothingSelected("规则");
            return;
        }

        // 还没保存的草稿：丢掉就是丢掉。配置里没有它，所以这里不用保存，也谈不上"放宽"。
        if (ReferenceEquals(_selectedRule, _stagedRule))
        {
            _stagedRule = null;
            _selectedRule = null;
            RuleForm.IsEnabled = false;
            _ruleForm.Clear();
            ShowRules();
            return;
        }

        _selectedTarget.Rules.Remove(_selectedRule);
        SaveAndReport();
        _selectedRule = null;
        RuleForm.IsEnabled = false;
        _ruleForm.Clear();

        // 同上：被拒时配置已经还原，_selectedTarget 是空的。
        if (_selectedTarget is null)
            return;

        ShowRules();
    }

    private void ApplyRule_Click(object sender, RoutedEventArgs e) => ApplyRuleForm();

    private bool ApplyRuleForm()
    {
        ReattachSelection();

        if (_selectedRule is null)
        {
            SayNothingSelected("规则");
            return false;
        }

        // 这是点「新建」造出来的草稿吗？如果是，这**一次**点应用就是它进配置的那一刻：
        // 服务拿它和"里面还没有它"的那份比 —— 新增一条规则是收紧，不要令牌，
        // 哪怕你在表单里把匹配方式改成了模型鉴定。
        var isNew = ReferenceEquals(_selectedRule, _stagedRule);

        _selectedRule.Name = RuleNameBox.Text.Trim();
        _selectedRule.Enabled = RuleEnabledCheck.IsChecked == true;

        if (RuleTypeBox.SelectedItem is Choice<MatchKind> type)
            _selectedRule.Match.Type = type.Value;

        var fields = new List<MatchField>();
        if (FieldUrlCheck.IsChecked == true) fields.Add(MatchField.Url);
        if (FieldTitleCheck.IsChecked == true) fields.Add(MatchField.Title);
        if (FieldBodyCheck.IsChecked == true) fields.Add(MatchField.Body);
        _selectedRule.Match.Fields = fields;

        _selectedRule.Match.Patterns = RulePatternsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (RulePolicyBox.SelectedItem is Choice<string> policy)
            _selectedRule.PolicyId = policy.Value;

        _selectedRule.ScheduleId = (RuleScheduleBox.SelectedItem as Choice<string?>)?.Value;
        _selectedRule.Priority = ParseInt(RulePriorityBox.Text, 0, -10_000, 10_000);

        var reminder = RuleReminderBox.Text.Trim();
        _selectedRule.Reminder = string.IsNullOrEmpty(reminder) ? null : reminder;

        // Through the same path as every other edit, so loosening any of these asks for the token,
        // while tightening them — fewer, shorter, or off — is free.
        _selectedRule.Pause.Enabled = RulePauseEnabledCheck.IsChecked == true;

        ApplyPauseBudget(_selectedRule.Pause.Workday, RulePauseWorkdayPeriodBox,
                         RulePauseWorkdayTimesBox, RulePauseWorkdayMinutesBox);

        ApplyPauseBudget(_selectedRule.Pause.Weekend, RulePauseWeekendPeriodBox,
                         RulePauseWeekendTimesBox, RulePauseWeekendMinutesBox);

        if (isNew && _selectedTarget is not null && !_selectedTarget.Rules.Contains(_selectedRule))
            _selectedTarget.Rules.Add(_selectedRule);

        // 规则是挂在目标下面的：如果这个目标自己还是草稿，它也得跟着进配置 ——
        // 否则规则会挂在一个不在配置里的目标上，存下去等于没存。
        if (isNew && _selectedTarget is not null && !_state.Config.Targets.Contains(_selectedTarget))
            _state.Config.Targets.Add(_selectedTarget);

        var saved = SaveAndReport();

        if (isNew)
        {
            if (saved)
            {
                _stagedRule = null;      // 进去了，不再是草稿
            }
            else
            {
                // 没存进去（比如被拒）：从配置里摘回去，仍旧当草稿拿着 ——
                // 用户填的东西不能丢，也不能留在配置里。
                _selectedTarget?.Rules.Remove(_selectedRule);
            }
        }

        // 重绑列表会先把选中项打到 -1（表单跟着清空），再选回来。整个过程是程序自己做的，
        // 所以不许把"要不要保存"勾出来 —— 那会在一次保存中途弹窗。
        //
        // 选回来的对象要先记下来：重绑那一下会把 _selectedRule 清成 null，之后再问就已经晚了。
        ShowRules();

        RefreshPauseStatus();
        ShowValidationResult();
        _ruleForm.Reload();

        return saved;
    }

    private static string UniqueRuleId(Target target, string baseId)
    {
        if (target.Rules.All(r => r.Id != baseId))
            return baseId;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseId}-{i}";
            if (target.Rules.All(r => r.Id != candidate))
                return candidate;
        }
    }

    // ------------------------------------------------------------------ 策略

    private void RefreshPolicyList()
    {
        var wanted = _selectedPolicy;

        Programmatic(() =>
        {
            PolicyList.ItemsSource = null;
            PolicyList.ItemsSource = _state.Config.Policies;

            // 重绑会把选中项打到 -1；不选回来的话，刚点完「应用修改」表单就变灰了。
            if (wanted is not null && _state.Config.Policies.Contains(wanted))
                PolicyList.SelectedItem = wanted;
        });
    }

    private void PolicyList_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // 换策略 = 离开现在这个表单。
        if (IgnoreRevert())
            return;

        if (!ConfirmLeaveDirtyForm())
        {
            RevertSelection(PolicyList, _selectedPolicy);
            return;
        }

        _selectedPolicy = PolicyList.SelectedItem as Policy;
        PolicyForm.IsEnabled = _selectedPolicy is not null;

        if (_selectedPolicy is null)
        {
            _policyForm.Clear();
            return;
        }

        PolicyNameBox.Text = _selectedPolicy.Name;
        ActionNotifyCheck.IsChecked = _selectedPolicy.Actions.Contains(ActionType.Notify);
        ActionMinimizeCheck.IsChecked = _selectedPolicy.Actions.Contains(ActionType.Minimize);
        ActionCloseCheck.IsChecked = _selectedPolicy.Actions.Contains(ActionType.Close);
        ActionKillCheck.IsChecked = _selectedPolicy.Actions.Contains(ActionType.Kill);
        ActionLogCheck.IsChecked = _selectedPolicy.Actions.Contains(ActionType.Log);
        GraceBox.Text = _selectedPolicy.GraceSeconds.ToString();
        CooldownBox.Text = _selectedPolicy.CooldownSeconds.ToString();
        DebounceBox.Text = _selectedPolicy.DebounceCount.ToString();
        RepeatBox.Text = _selectedPolicy.RepeatSeconds.ToString();
        AlertStyleBox.SelectedItem = ((Choice<AlertStyle>[])AlertStyleBox.ItemsSource!)
            .FirstOrDefault(c => c.Value == _selectedPolicy.AlertStyle);

        UpdateConditionalFields();

        _policyForm.Reload();
    }

    private void Action_Changed(object sender, RoutedEventArgs e) => UpdateConditionalFields();

    /// <summary>
    /// Shows the fields that mean something for the actions currently ticked, and hides the rest — a
    /// visible field that does nothing is worse than no field.
    /// </summary>
    private void UpdateConditionalFields()
    {
        var actions = ReadActions();
        var reminderOnly = PolicyKind.IsReminderOnly(new Policy { Actions = actions });
        var touchesWindow = actions.Any(a => a is ActionType.Minimize or ActionType.Close or ActionType.Kill);

        RepeatPanel.Visibility = reminderOnly ? Visibility.Visible : Visibility.Collapsed;
        GracePanel.Visibility = touchesWindow ? Visibility.Visible : Visibility.Collapsed;
        AlertStylePanel.Visibility = actions.Contains(ActionType.Notify) ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<ActionType> ReadActions()
    {
        var actions = new List<ActionType>();
        if (ActionNotifyCheck.IsChecked == true) actions.Add(ActionType.Notify);
        if (ActionMinimizeCheck.IsChecked == true) actions.Add(ActionType.Minimize);
        if (ActionCloseCheck.IsChecked == true) actions.Add(ActionType.Close);
        if (ActionKillCheck.IsChecked == true) actions.Add(ActionType.Kill);
        if (ActionLogCheck.IsChecked == true) actions.Add(ActionType.Log);
        return actions;
    }

    private void NewPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveDirtyForm())
            return;

        var id = "policy";
        if (_state.Config.Policies.Any(p => p.Id == id))
        {
            for (var i = 2; ; i++)
            {
                if (_state.Config.Policies.All(p => p.Id != $"policy-{i}"))
                {
                    id = $"policy-{i}";
                    break;
                }
            }
        }

        var policy = new Policy
        {
            Id = id,
            Name = "新策略",
            Actions = { ActionType.Notify },
            GraceSeconds = 5,
            CooldownSeconds = 60,
            DebounceCount = 1,
        };

        _state.Config.Policies.Add(policy);
        SaveAndReport();
        RefreshPolicyList();
        PolicyList.SelectedItem = policy;
    }

    private void DeletePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveDirtyForm())
            return;
        ReattachSelection();

        if (_selectedPolicy is null)
        {
            SayNothingSelected("策略");
            return;
        }

        var inUse = _state.Config.Targets
            .SelectMany(t => t.Rules)
            .Any(r => r.PolicyId == _selectedPolicy.Id);

        if (inUse && MessageBox.Show(
                $"策略「{_selectedPolicy.Name}」还有规则在用，删除后那些规则不会动作。继续？", "Sentinel",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _state.Config.Policies.Remove(_selectedPolicy);
        SaveAndReport();
        _selectedPolicy = null;
        PolicyForm.IsEnabled = false;
        RefreshPolicyList();
    }

    private void ApplyPolicy_Click(object sender, RoutedEventArgs e) => ApplyPolicyForm();

    private bool ApplyPolicyForm()
    {
        ReattachSelection();
        if (_selectedPolicy is null)
        {
            SayNothingSelected("策略");
            return false;
        }

        _selectedPolicy.Name = PolicyNameBox.Text.Trim();

        var actions = ReadActions();
        _selectedPolicy.Actions = actions;

        _selectedPolicy.GraceSeconds = ParseInt(GraceBox.Text, 0, 0, 86_400);
        _selectedPolicy.CooldownSeconds = ParseInt(CooldownBox.Text, 0, 0, 86_400);
        _selectedPolicy.DebounceCount = ParseInt(DebounceBox.Text, 1, 1, 1000);

        // Keep the stored config honest: the field is hidden for policies it cannot affect, so a
        // leftover value from before would be misleading in the JSON.
        _selectedPolicy.RepeatSeconds = PolicyKind.IsReminderOnly(_selectedPolicy)
            ? ParseInt(RepeatBox.Text, 0, 0, 86_400)
            : 0;

        if (AlertStyleBox.SelectedItem is Choice<AlertStyle> alertStyle)
            _selectedPolicy.AlertStyle = alertStyle.Value;

        var saved = SaveAndReport();

        RefreshPolicyList();
        RefreshPolicyChoices();
        ShowValidationResult();
        _policyForm.Reload();

        return saved;
    }

    // ------------------------------------------------------------------ 大语言模型

    /// <summary>
    /// 这一页在讲两件不同的事：一是<b>有没有服务商、正在用哪一个</b>（配置），二是<b>最近一次调用
    /// 有没有出问题</b>（运行状态）。前者正常不代表后者没事。
    /// </summary>
    private void RefreshModelStatus()
    {
        var config = _state.Config;
        var active = config.Models.FirstOrDefault(m => m.Id == config.ActiveModelId);

        ModelStatusText.Text = active is not null
            ? $"当前启用：{active.Name}（{active.Model}）"
            : config.Models.Count == 0
                ? "还没有服务商。「模型监督」那种规则现在不会生效。"
                : "当前没有启用任何服务商 —— 模型监督是关着的。";

        if (_state.Monitor.ModelSupervisor?.BlockingIssue is { } issue)
            ModelStatusText.Text += $"\n⚠ {issue}";
    }

    /// <summary>现在参与判断的服务商，按顺序。老配置（没有链）就是那一个 ActiveModelId。</summary>
    private List<string> ModelChain()
    {
        var config = _state.Config;

        if (config.ModelChain is { Count: > 0 })
            return config.ModelChain.ToList();

        return string.IsNullOrWhiteSpace(config.ActiveModelId)
            ? new List<string>()
            : new List<string> { config.ActiveModelId };
    }

    // ------------------------------------------------------------------ 判断链

    private ObservableCollection<ChainRow> _chainRows = new();
    private ChainRow? _dragging;
    private DragGhostAdorner? _ghost;
    private System.Windows.Point _grabOffset;
    private bool _chainOrderChanged;

    /// <summary>顶部那两个按钮：切换「所有供应商」/「判断链」。</summary>
    private void ModelTab_Changed(object sender, RoutedEventArgs e)
    {
        if (ModelChainView is null || ModelListView is null)
            return;   // 初始化还没走完

        var chain = ModelTabChain.IsChecked == true;
        var usage = ModelTabUsage.IsChecked == true;

        ModelListView.Visibility = !chain && !usage ? Visibility.Visible : Visibility.Collapsed;
        ModelChainView.Visibility = chain ? Visibility.Visible : Visibility.Collapsed;
        ModelUsageView.Visibility = usage ? Visibility.Visible : Visibility.Collapsed;
        ModelEditView.Visibility = Visibility.Collapsed;

        if (chain)
            RefreshChainList();

        if (usage)
            RefreshUsage();
    }

    private void RefreshChainList()
    {
        var config = _state.Config;

        var providers = ModelChain()
            .Select(id => config.Models.FirstOrDefault(m => m.Id == id))
            .Where(m => m is not null)
            .ToList();

        _chainRows = new ObservableCollection<ChainRow>(
            providers.Select((m, i) => new ChainRow(m!, i)));

        ChainList.ItemsSource = _chainRows;

        if (providers.Count == 0)
        {
            ModelChainStatus.Text =
                "判断链是空的 —— 模型监督现在什么都不判。点「启用新供应商…」把要用的加进来。";
            return;
        }

        var stable = providers.Count(m => m!.Stable);

        ModelChainStatus.Text = stable == 0
            ? "⚠ 链里一个「能够稳定提供服务」的都没有 —— 它们连不上时不会提醒你。去「所有供应商」里勾上至少一个。"
            : $"判断链上有 {providers.Count} 个供应商，其中 {stable} 个标了「稳定」。"
              + "拖动左侧 ☰ 可以调整先后顺序。";
    }

    /// <summary>把几个供应商追加到判断链尾。弹窗和离线检查都走这里。</summary>
    public void AddProvidersToChain(IEnumerable<string> ids) => PersistChain(ModelChain().Concat(ids));

    // ------------------------------------------------------------------ 用量

    private bool _usageFiltersBuilt;
    private bool _buildingUsageFilters;

    private void RefreshUsage_Click(object sender, RoutedEventArgs e) => RefreshUsage();

    private void UsageFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_buildingUsageFilters && _usageFiltersBuilt)
            RenderUsage();
    }

    /// <summary>
    /// 离线检查用：对折线图最后一个"命中区"**真的触发一次 MouseEnter**，返回悬浮窗开没开。
    ///
    /// <para>
    /// 特意走真实事件、而不是直接调 Show：悬浮窗的内容是由闭包生成的，而闭包最容易出的错是
    /// 抓住 for 循环变量（所有 lambda 共用一个，循环结束就停在越界值）—— 那样鼠标一移上去
    /// 就崩进程。直接调 Show 永远测不出这一类。
    /// </para>
    /// </summary>
    public bool ShowUsageTipForCheck()
    {
        if (UsageLineChart.Tag is not ChartTooltip tooltip
            || UsageLineChart.Children.OfType<UIElement>().LastOrDefault() is not { } target)
        {
            return false;
        }

        target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = UIElement.MouseEnterEvent,
        });

        var open = tooltip.IsOpen;

        target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = UIElement.MouseLeaveEvent,
        });

        return open;
    }

    private void UsageCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_usageFiltersBuilt)
            RenderUsage();
    }

    private void RefreshUsage()
    {
        BuildUsageFilters();
        RenderUsage();
        RefreshUsageTable();
    }

    private void BuildUsageFilters()
    {
        if (_usageFiltersBuilt)
            return;

        _buildingUsageFilters = true;

        try
        {
            UsageRangeBox.ItemsSource = new List<Choice<int>>
            {
                new(0, "今天（按小时）"),
                new(7, "近 7 天"),
                new(30, "近 30 天"),
            };

            UsageRangeBox.SelectedIndex = 1;

            var providers = new List<Choice<string?>> { new(null, "全部") };
            providers.AddRange(_state.Config.Models.Select(m => new Choice<string?>(m.Id, m.Name)));

            UsageProviderBox.ItemsSource = providers;
            UsageProviderBox.SelectedIndex = 0;

            UsageMetricBox.ItemsSource = new List<Choice<string>>
            {
                new("requests", "请求数"),
                new("failures", "失败数"),
                new("tokens", "token"),
            };

            UsageMetricBox.SelectedIndex = 0;

            _usageFiltersBuilt = true;
        }
        finally
        {
            _buildingUsageFilters = false;
        }
    }

    private void RenderUsage()
    {
        var store = _state.Monitor.Usage;

        var range = (UsageRangeBox.SelectedItem as Choice<int>)?.Value ?? 7;
        var metric = (UsageMetricBox.SelectedItem as Choice<string>)?.Value ?? "requests";
        var metricLabel = (UsageMetricBox.SelectedItem as Choice<string>)?.Display ?? "请求数";
        var providerId = (UsageProviderBox.SelectedItem as Choice<string?>)?.Value;

        // 0 = 今天：按小时摊开（00:00 到现在）；其余按天。
        List<(string Label, IReadOnlyDictionary<string, Sentinel.Core.Model.UsageCounters> Providers)> buckets =
            range == 0
                ? store.Hourly().Select(b => (b.Slot, b.Providers)).ToList()
                : store.Daily(range).Select(b => (b.Day, b.Providers)).ToList();

        var daily = buckets;

        var ids = daily
            .SelectMany(d => d.Providers.Keys)
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var shown = providerId is null ? ids : ids.Where(id => id == providerId).ToList();

        string NameOf(string id) =>
            _state.Config.Models.FirstOrDefault(m => m.Id == id)?.Name ?? $"(已删除：{id})";

        double MetricOf(Sentinel.Core.Model.UsageCounters c) => metric switch
        {
            "failures" => c.Failures,
            "tokens" => c.PromptTokens + c.CompletionTokens,
            _ => c.Requests,
        };

        double ValueOn(string id, (string Label, IReadOnlyDictionary<string, Sentinel.Core.Model.UsageCounters> Providers) bucket) =>
            bucket.Providers.TryGetValue(id, out var counters) ? MetricOf(counters) : 0;

        var labels = daily
            .Select(d => range == 0 ? d.Label : d.Label.Length >= 10 ? d.Label[5..] : d.Label)
            .ToList();

        var series = shown
            .Select((id, index) => new Charts.Series(
                NameOf(id), Charts.ColorOf(index), daily.Select(d => ValueOn(id, d)).ToArray()))
            .ToList();

        UsageLineTitle.Text = range == 0
            ? $"今天（按小时 · {metricLabel}）"
            : $"每天（{metricLabel}）";
        var span = range == 0 ? "今天" : range == 7 ? "近 7 天" : "近 30 天";

        UsageBarTitle.Text = $"各服务商合计（{span} · {metricLabel}）";
        UsagePieTitle.Text = $"占比（{span} · {metricLabel}）";

        var rangeTotals = new Dictionary<string, Sentinel.Core.Model.UsageCounters>(StringComparer.Ordinal);

        foreach (var bucket in daily)
        {
            foreach (var (id, counters) in bucket.Providers)
            {
                if (!rangeTotals.TryGetValue(id, out var sum))
                {
                    sum = new Sentinel.Core.Model.UsageCounters();
                    rangeTotals[id] = sum;
                }

                sum.Requests += counters.Requests;
                sum.Failures += counters.Failures;
                sum.PromptTokens += counters.PromptTokens;
                sum.CompletionTokens += counters.CompletionTokens;
                sum.Reported += counters.Reported;
            }
        }

        var totalRequests = rangeTotals.Values.Sum(c => c.Requests);
        var totalFailures = rangeTotals.Values.Sum(c => c.Failures);
        var totalTokens = rangeTotals.Values.Sum(c => c.PromptTokens + c.CompletionTokens);
        var totalReported = rangeTotals.Values.Sum(c => c.Reported);

        // 没有数据就从 0 开始显示，别用"—"糊过去。
        UsageRequestsText.Text = totalRequests.ToString("N0");
        UsageFailuresText.Text = totalFailures.ToString("N0");
        UsageTokensText.Text = totalReported > 0 || totalTokens > 0 ? totalTokens.ToString("N0") : "0";
        UsageRateText.Text = totalRequests > 0
            ? $"{(double)totalFailures / totalRequests:P0}"
            : "—";

        // 缓存命中率：分母按各家语义分开算 —— OpenAI 的 prompt_tokens 里**含**缓存命中的部分，
        // Anthropic 的 input_tokens **不含**（命中在 cache_read 里单独给）。混在一起算会差一倍。
        double cachedHits = 0;
        double cachedBase = 0;

        foreach (var (id, counters) in rangeTotals)
        {
            if (counters.CacheReported == 0)
                continue;

            var format = _state.Config.Models.FirstOrDefault(m => m.Id == id)?.Format;

            cachedHits += counters.CachedTokens;
            cachedBase += format == Sentinel.Core.Models.ApiFormat.Anthropic
                ? counters.PromptTokens + counters.CachedTokens
                : counters.PromptTokens;
        }

        UsageCacheText.Text = cachedBase > 0 ? $"{cachedHits / cachedBase:P0}" : "—";

        Charts.Line(UsageLineChart, labels, series, index =>
        {
            var lines = shown
                .Select(id => $"{NameOf(id)}：{ValueOn(id, daily[index]):N0}")
                .ToList();

            lines.Add($"合计：{shown.Sum(id => ValueOn(id, daily[index])):N0}");

            return new Charts.Tip(labels[index], lines);
        });

        UsageLineLegend.Children.Clear();

        foreach (var item in series)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };

            entry.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = item.Brush,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            entry.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            UsageLineLegend.Children.Add(entry);
        }

        var totals = shown
            .Select(id => (Label: NameOf(id), Value: daily.Sum(d => ValueOn(id, d))))
            .ToList();

        var grandTotal = totals.Sum(t => t.Value);

        Charts.Tip TipFor(int index)
        {
            var id = shown[index];
            var counters = rangeTotals.GetValueOrDefault(id);
            var share = grandTotal > 0 ? totals[index].Value / grandTotal : 0;

            var lines = new List<string> { $"占比 {share:P0}" };

            if (counters is not null)
            {
                lines.Add($"请求 {counters.Requests:N0} 次 · 失败 {counters.Failures:N0} 次");
                lines.Add($"token {counters.PromptTokens + counters.CompletionTokens:N0}");
            }

            return new Charts.Tip(NameOf(id), lines);
        }

        Charts.Bars(UsageBarChart, totals, TipFor);
        Charts.Pie(UsagePieChart, totals, TipFor);

        ModelUsageStatus.Text = totals.Sum(t => t.Value) <= 0
            ? "这个范围内还没有用量记录 —— 模型监督判过一次之后这里才有数据。"
            : "一次判定请求算一次；「失败」是没拿到判定的那些（超时、连不上、5xx、格式看不懂）。"
              + "token 只统计服务商回了 usage 的请求；升级前没有时段信息的记录算在 00:00。";
    }

    private void RefreshUsageTable()
    {
        var store = _state.Monitor.Usage;

        var today = store.Since(1);
        var week = store.Since(7);
        var month = store.Since(30);

        var ids = today.Keys
            .Concat(week.Keys)
            .Concat(month.Keys)
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var rows = new List<UsageRow>();

        foreach (var id in ids)
        {
            var provider = _state.Config.Models.FirstOrDefault(m => m.Id == id);

            rows.Add(new UsageRow(
                provider is null ? $"(已删除：{id})" : $"{provider.Name}（{provider.Model}）",
                FormatUsage(today.GetValueOrDefault(id)),
                FormatUsage(week.GetValueOrDefault(id)),
                FormatUsage(month.GetValueOrDefault(id))));
        }

        if (rows.Count > 0)
        {
            rows.Add(new UsageRow(
                "合计",
                FormatUsage(Sum(today)),
                FormatUsage(Sum(week)),
                FormatUsage(Sum(month))));
        }

        UsageList.ItemsSource = rows;
    }

    private static Sentinel.Core.Model.UsageCounters Sum(IReadOnlyDictionary<string, Sentinel.Core.Model.UsageCounters> from)
    {
        var sum = new Sentinel.Core.Model.UsageCounters();

        foreach (var counters in from.Values)
        {
            sum.Requests += counters.Requests;
            sum.Failures += counters.Failures;
            sum.PromptTokens += counters.PromptTokens;
            sum.CompletionTokens += counters.CompletionTokens;
            sum.Reported += counters.Reported;
        }

        return sum;
    }

    private static string FormatUsage(Sentinel.Core.Model.UsageCounters? counters)
    {
        if (counters is null || counters.Requests == 0)
            return "—";

        var text = counters.Requests + " 次";

        if (counters.Failures > 0)
            text += $"（{counters.Failures} 失败）";

        text += counters.Reported > 0
            ? $" · {counters.PromptTokens + counters.CompletionTokens:N0} tok"
            : " · 无用量";

        return text;
    }

    /// <summary>把链写回配置并保存。顺序变化是中性的，增删走放宽判定（可能要令牌）。</summary>
    private void PersistChain(IEnumerable<string> ids)
    {
        var chain = ids.ToList();
        var config = _state.Config;

        config.ModelChain = chain;

        // ActiveModelId 跟着链的首位：界面显示、以及老版本读这份配置时都用它。
        config.ActiveModelId = chain.FirstOrDefault();

        SaveAndReport();

        RefreshModelList();
        RefreshModelStatus();
        RefreshChainList();
    }

    private void AddChainProviders_Click(object sender, RoutedEventArgs e)
    {
        var config = _state.Config;
        var chain = ModelChain();
        var candidates = config.Models.Where(m => !chain.Contains(m.Id)).ToList();

        if (candidates.Count == 0)
        {
            ModelChainStatus.Text = "所有供应商都已经在链里了。";
            return;
        }

        var window = new Window
        {
            Title = "启用新供应商",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
        };

        var panel = new StackPanel { Margin = new Thickness(22) };

        panel.Children.Add(new TextBlock
        {
            Text = "勾选要加入判断链的供应商（按这里从上到下的顺序，追加到链尾）：",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        var boxes = new List<(CheckBox Box, ModelProvider Provider)>();

        foreach (var provider in candidates)
        {
            var box = new CheckBox
            {
                Content = $"{provider.Name}（{provider.Model}）",
                Margin = new Thickness(0, 0, 0, 10),
            };

            boxes.Add((box, provider));
            panel.Children.Add(box);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var ok = new Button { Content = "加入", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(18, 6, 18, 6),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        ok.Click += (_, _) => { window.DialogResult = true; window.Close(); };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;

        if (window.ShowDialog() != true)
            return;

        var added = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Provider.Id).ToList();

        if (added.Count > 0)
            AddProvidersToChain(added);
    }

    private void RemoveChainRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChainRow row)
            return;

        // 从链里拿掉一个供应商 = 放宽（它本来会拦下的东西，现在没人拦了）——
        // 保存时可能要令牌，被拒会走和解锁那套一样的流程。
        PersistChain(ModelChain().Where(id => id != row.Provider.Id));
    }

    /// <summary>
    /// 鼠标现在压在第几行 —— 按**布局位置**算，不看命中测试。
    ///
    /// <para>
    /// 这是抖动那个坑的解法：换位时的滑动用 RenderTransform 做，而**命中测试会跟着这个位移走**。
    /// 动画还没跑完，鼠标底下"命中的"已经是另一行，于是每移一像素就再换一次，两行来回弹。
    /// 把每一行当前的动画偏移减掉，拿到的是它在布局里的位置，动画再怎么跑都稳定。
    /// </para>
    /// </summary>
    private int TargetRowIndexAt(System.Windows.Point position)
    {
        for (var i = 0; i < _chainRows.Count; i++)
        {
            if (ChainList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            var animatedOffset = (container.RenderTransform as System.Windows.Media.TranslateTransform)?.Y ?? 0;
            var top = container.TranslatePoint(new System.Windows.Point(0, 0), ChainList).Y - animatedOffset;

            if (position.Y >= top && position.Y < top + container.ActualHeight)
                return i;
        }

        return -1;
    }

    /// <summary>每一行现在在列表里的纵坐标（换位之前拍一张）。</summary>
    private Dictionary<FrameworkElement, double> CaptureRowPositions()
    {
        var positions = new Dictionary<FrameworkElement, double>();

        for (var i = 0; i < _chainRows.Count; i++)
        {
            if (ChainList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
                positions[container] = container.TranslatePoint(new System.Windows.Point(0, 0), ChainList).Y;
        }

        return positions;
    }

    /// <summary>换位之后让每一行从原位置滑到新位置（180ms、缓出）。</summary>
    private void SlideIntoPlace(Dictionary<FrameworkElement, double> before)
    {
        ChainList.UpdateLayout();

        foreach (var (container, oldY) in before)
        {
            if (!container.IsVisible)
                continue;

            var newY = container.TranslatePoint(new System.Windows.Point(0, 0), ChainList).Y;
            var delta = oldY - newY;

            if (Math.Abs(delta) < 0.5)
                continue;

            var transform = new System.Windows.Media.TranslateTransform(0, delta);
            container.RenderTransform = transform;

            transform.BeginAnimation(
                System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                    },
                });
        }
    }

    /// <summary>把第 from 行挪到第 to 行并保存。拖拽走的是同一段落盘逻辑。</summary>
    public void ReorderChain(int from, int to)
    {
        if (from < 0 || to < 0 || from >= _chainRows.Count || to >= _chainRows.Count || from == to)
            return;

        _chainRows.Move(from, to);

        for (var n = 0; n < _chainRows.Count; n++)
            _chainRows[n].SetPosition(n);

        PersistChain(_chainRows.Select(r => r.Provider.Id));
    }

    private void ChainDragStart(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChainRow row)
            return;

        var container = FindRowContainer(row);

        if (container is null)
            return;

        _dragging = row;
        _grabOffset = e.GetPosition(container);

        // 跟着鼠标走的"影子"：拿这一刻的静态快照，所以下面把原行调淡不会把影子也弄淡。
        if (AdornerLayer.GetAdornerLayer(ChainList) is { } layer)
        {
            _ghost = new DragGhostAdorner(ChainList, container);
            layer.Add(_ghost);
            _ghost.MoveTo(e.GetPosition(ChainList).X - _grabOffset.X,
                          e.GetPosition(ChainList).Y - _grabOffset.Y);
        }

        // 原行**完全隐去**、只留它占的那一格：拖动期间"这一项"由跟着光标的影子代表。
        // 原来调成 0.35 会变成"光标上有个影子、原地还留着一个残影"，还会盖住让位上来的行。
        ApplyDraggedLook();

        // 捕获在**列表**上，不是手柄上：这样拖出列表边界也能收到移动/松开事件。
        ChainList.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// 拖动期间：被拖的那一行隐去（它由影子代表），其余正常。
    ///
    /// <para>
    /// 每次换位都要重刷一遍，而且按 <c>DataContext</c> 现找容器 —— 列表重排之后容器有可能被
    /// 重建，抱着拖动开始时的那个引用会漏掉新的那一行，残影就又冒出来了。
    /// </para>
    /// </summary>
    private void ApplyDraggedLook()
    {
        for (var i = 0; i < _chainRows.Count; i++)
        {
            if (ChainList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            container.Opacity = _dragging is not null && ReferenceEquals(container.DataContext, _dragging)
                ? 0
                : 1;
        }
    }

    private FrameworkElement? FindRowContainer(ChainRow row)
    {
        for (var i = 0; i < _chainRows.Count; i++)
        {
            if (ChainList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container
                && ReferenceEquals(container.DataContext, row))
            {
                return container;
            }
        }

        return null;
    }

    private void ChainDragMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var position = e.GetPosition(ChainList);

        _ghost?.MoveTo(position.X - _grabOffset.X, position.Y - _grabOffset.Y);

        var to = TargetRowIndexAt(position);

        if (to < 0)
            return;

        var from = _chainRows.IndexOf(_dragging!);

        if (from < 0 || from == to)
            return;

        var before = CaptureRowPositions();

        _chainRows.Move(from, to);
        _chainOrderChanged = true;

        for (var n = 0; n < _chainRows.Count; n++)
            _chainRows[n].SetPosition(n);

        ApplyDraggedLook();
        SlideIntoPlace(before);
    }

    private void ChainDragEnd(object sender, MouseEventArgs e)
    {
        if (_dragging is null)
            return;

        _dragging = null;

        if (_ghost is { } ghost)
        {
            _ghost = null;

            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) => AdornerLayer.GetAdornerLayer(ChainList)?.Remove(ghost);
            ghost.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        ApplyDraggedLook();

        if (ChainList.IsMouseCaptured)
            ChainList.ReleaseMouseCapture();

        // 只是点了一下手柄、没真的拖动，就别白存一次配置。
        if (!_chainOrderChanged)
            return;

        _chainOrderChanged = false;

        // 顺序只影响"先问谁"（省不省钱），不影响松紧 —— 所以随手拖、随手存。
        PersistChain(_chainRows.Select(r => r.Provider.Id));
    }

    /// <summary>整条链的连通性：只测地址可达（不花钱）。稳定供应商不通会直接说出来。</summary>
    private async void TestChain_Click(object sender, RoutedEventArgs e)
    {
        if (_chainRows.Count == 0)
        {
            ModelChainStatus.Text = "链是空的，没什么可测的。";
            return;
        }

        ModelChainStatus.Text = "正在测（只测地址可达，不花钱）…";

        var connectivity = new Connectivity();
        var brokenStable = new List<string>();

        foreach (var row in _chainRows)
        {
            var reachable = await connectivity.IsReachableAsync(row.Provider.Endpoint);

            row.TestResult = reachable ? "✓ 地址可达" : "✗ 连不上";

            if (!reachable && row.Provider.Stable)
                brokenStable.Add(row.Provider.Name);
        }

        ModelChainStatus.Text = brokenStable.Count == 0
            ? "所有「稳定」的供应商都可达 ✓"
            : $"⚠ 这些稳定供应商连不上：{string.Join("、", brokenStable)} —— "
              + "监控会反复全屏提醒，直到它恢复，或者你把它从链里移除（那要令牌）。";
    }

    private void RefreshModelList()
    {
        // 每次重绑都重新生成行对象：一行要显示的“使用中”来自
        // config.ActiveModelId，而它在别处（保存、删掉服务商）会变。
        var chain = ModelChain();

        ModelList.ItemsSource = _state.Config.Models
            .Select(m => new ModelRow(m, chain.Contains(m.Id)))
            .ToList();

        ModelEmptyHint.Visibility = _state.Config.Models.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        RefreshChainList();
    }

    // ---- 列表页 ↔ 编辑页

    private void NewModel_Click(object sender, RoutedEventArgs e)
    {
        // 新建时**不保存** —— 只是在内存里造一个对象，切到编辑页让用户填。
        // 第一次点「应用修改」时才真正保存并加进列表。这样：
        // 1. 点"取消"时，半成品条目不会留在配置文件里
        // 2. 不会触发"没填地址/模型名"的校验报错（它还没到该校验的时候）
        var provider = new ModelProvider
        {
            Id = UniqueModelId("model"),
            Name = "新服务商",
            Format = ApiFormat.OpenAi,
        };

        // 直接进编辑页：刚建的一个空服务商在列表里没什么可看的。
        OpenModelEditor(provider);
    }

    private void EditModelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelRow row })
            OpenModelEditor(row.Provider);
    }

    /// <summary>
    /// 行上的「测试」。用的是**存下来的**那份 —— 列表里看到的就是它，
    /// 在这里对着屏幕重新编辑一遍反而不知道测的是谁。编辑页里那个则反过来。
    /// </summary>
    private async void TestModelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModelRow row })
            return;

        var (text, tone) = await ProbeAsync(row.Provider);

        MessageBox.Show(this, text, $"测试连接 · {row.Name}", MessageBoxButton.OK,
            tone switch
            {
                "Success" => MessageBoxImage.Information,
                "Danger" => MessageBoxImage.Warning,
                _ => MessageBoxImage.Information,
            });
    }

    private void DeleteModelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelRow row })
            DeleteModel(row.Provider);
    }

    private void DeleteModel(ModelProvider provider)
    {
        // 同上：列表里的那一行也可能是上一批配置里的对象，按 Id 认回当前这份。
        provider = Current(provider, _state.Config.Models, m => m.Id);

        if (provider is null)
        {
            RefreshModelList();
            return;
        }

        if (MessageBox.Show(this, $"删除服务商「{provider.Name}」？", "Sentinel",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var wasActive = provider.Id == _state.Config.ActiveModelId;

        _state.Config.Models.Remove(provider);

        // 删掉的正好是启用的那个 —— 顺带把启用也清掉。这就是"停用模型监督"，
        // 所以保存会按放宽来要求令牌。
        if (wasActive)
            _state.Config.ActiveModelId = null;

        if (ReferenceEquals(provider, _selectedModel))
            CloseModelEditor(force: true);

        SaveAndReport();
        RefreshModelList();
        RefreshModelStatus();
        RefreshRuleTargetPicker();
    }

    private void OpenModelEditor(ModelProvider provider)
    {
        _selectedModel = provider;
        _autoFilledListEndpoint = "";

        ModelEditTitle.Text = string.IsNullOrWhiteSpace(provider.Name) ? "编辑服务商" : provider.Name;
        ModelNameBox.Text = provider.Name;
        ModelNoteBox.Text = provider.Note;
        ModelWebsiteBox.Text = provider.Website;
        ModelEndpointBox.Text = provider.Endpoint;
        ModelAutoCompleteCheck.IsChecked = provider.AutoCompleteEndpoint;
        ModelFormatBox.SelectedItem = ((Choice<ApiFormat>[])ModelFormatBox.ItemsSource!)
            .FirstOrDefault(c => c.Value == provider.Format);
        // 可编辑下拉框有个坑：ItemsSource 为空的时候，程序设进去的 Text 读得出来、屏幕上却是空的。
        // 所以把当前这个值塞进列表并选中它 —— 这样一定显示得出来，而它仍然是可编辑的，
        // 用户照样能直接手打一个列表里没有的名字。
        ModelCodePicker.ItemsSource = string.IsNullOrWhiteSpace(provider.Model)
            ? null
            : new[] { provider.Model };
        ModelCodePicker.SelectedIndex = string.IsNullOrWhiteSpace(provider.Model) ? -1 : 0;

        ModelStableCheck.IsChecked = provider.Stable;
        ModelKeySecret.Password = provider.ApiKey;
        ModelKeyBox.Text = provider.ApiKey;
        SetKeyVisible(false);
        ModelTestResult.Text = "";

        // 列表地址：存过就用存的，没存过就按请求地址带一个出来。
        ModelListEndpointBox.Text = provider.ModelListEndpoint.Length > 0
            ? provider.ModelListEndpoint
            : ModelClient.SuggestListEndpoint(provider.Endpoint);

        if (provider.ModelListEndpoint.Length == 0)
            _autoFilledListEndpoint = ModelListEndpointBox.Text.Trim();

        ShowEndpointHint();
        ShowListEndpointHint();

        ModelListView.Visibility = Visibility.Collapsed;
        ModelEditView.Visibility = Visibility.Visible;

        _modelForm.Reload();
        ModelNameBox.Focus();
    }

    private void CancelModel_Click(object sender, RoutedEventArgs e) => CloseModelEditor(force: false);

    /// <summary>回到列表页。脏的话先问一句 —— 和换页签、换条目是同一件事。</summary>
    private void CloseModelEditor(bool force)
    {
        if (!force && !ConfirmLeaveDirtyForm(ModelTabIndex))
            return;

        _selectedModel = null;
        ModelEditView.Visibility = Visibility.Collapsed;
        ModelListView.Visibility = Visibility.Visible;
        _modelForm.Clear();

        RefreshModelList();
        RefreshModelStatus();
    }

    // ---- 凭据显示

    private void ModelKeyToggle_Click(object sender, RoutedEventArgs e) =>
        SetKeyVisible(ModelKeyToggle.IsChecked == true);

    /// <summary>
    /// 密码框和文本框二选一。WPF 的 TextBox 没有掩码字符，PasswordBox 又不能显示，
    /// 所以只能是两个控件轮流出现 —— 切的时候把值搬过去。
    /// </summary>
    private void SetKeyVisible(bool visible)
    {
        if (visible)
            ModelKeyBox.Text = ModelKeySecret.Password;
        else
            ModelKeySecret.Password = ModelKeyBox.Text;

        ModelKeySecret.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        ModelKeyBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>key 在哪一边取，取决于它现在是显示着还是藏着。</summary>
    private string CurrentKey() =>
        ModelKeyToggle.IsChecked == true ? ModelKeyBox.Text.Trim() : ModelKeySecret.Password.Trim();

    /// <summary>
    /// 把官网链接交给默认浏览器。
    ///
    /// <para>
    /// 有这个按钮的理由很实在："key 到期了去哪里续"这件事总是在焦急的时候发生，
    /// 而一个只能看不能点的地址等于没有。
    /// </para>
    /// </summary>
    private void OpenWebsite_Click(object sender, RoutedEventArgs e)
    {
        var url = ModelWebsiteBox.Text.Trim();

        if (url.Length == 0)
        {
            MessageBox.Show(this, "还没填官网链接。", "打开官网",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, $"打不开「{url}」—— 它不是一个完整的 http/https 地址。",
                "打开官网", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开浏览器：{ex.Message}", "打开官网",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- 地址与模型列表

    /// <summary>
    /// 请求地址失焦时，按格式自动补全路径。
    ///
    /// <para>
    /// 起因：配服务商时最常见的错是"地址少了后半段" —— 只写到 base（比如
    /// <c>https://api.deepseek.com/anthropic</c>），而没加 <c>/v1/messages</c>。
    /// 结果是 404，界面提示"地址不对"，人得回来改一遍。
    /// </para>
    /// <para>
    /// 自动补全的逻辑：地址看起来**像是**只写了 base（末尾没有那个格式该有的关键词）、
    /// 而且末尾不是斜杠（斜杠表示"我写完了"），就按格式补上标准路径。
    /// </para>
    /// </summary>
    private void ModelEndpoint_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = ModelEndpointBox.Text.Trim();
        var format = (ModelFormatBox.SelectedItem as Choice<ApiFormat>)?.Value;

        if (url.Length == 0 || format is null)
            return;

        // 人明确关掉了补全 —— **原样使用，一个字符都不动**。
        //
        // 这里以前还有一条"末尾是斜杠就跳过"的猜测（把斜杠当成"我写完了"）—— 撤了：
        // 那是个没有提示、也没有任何人知道的暗号，而用户粘贴过来的地址常常自带一个尾巴斜杠
        // （…/api/paas/v4/），于是他勾着"自动补全"却什么也没发生，只能自己手动补。
        // "别动我写的"现在有一个看得见的说法：把下面那个开关取消勾选。
        if (ModelAutoCompleteCheck.IsChecked != true)
            return;

        // 规则只有一份，在 ModelClient.CompleteEndpoint 里（测试连接失败时也用同一条规则）。
        if (ModelClient.CompleteEndpoint(url, format.Value) is { } completed && completed != url)
        {
            ModelEndpointBox.Text = completed;
            ShowEndpointHint();  // 刷一遍提示，因为地址变了
        }
    }

    /// <summary>
    /// 地址要的是完整 URL，因为各家路径不一样、中转服务还常常自己改路径 —— 但"完整 URL"这件事
    /// 光说没用，得给个例子。
    /// </summary>
    private void ShowEndpointHint()
    {
        var format = (ModelFormatBox.SelectedItem as Choice<ApiFormat>)?.Value;
        var url = ModelEndpointBox.Text.Trim();

        var example = format switch
        {
            ApiFormat.Anthropic => "https://api.deepseek.com/anthropic/v1/messages",
            ApiFormat.Responses => "https://api.openai.com/v1/responses",
            _ => "https://api.openai.com/v1/chat/completions",
        };

        var complaint = AddressLooksOff(format, url);

        ModelEndpointHint.Text = complaint is null
            ? $"例：{example}"
            : $"例：{example}\n{complaint}";

        ModelEndpointHint.Foreground = (System.Windows.Media.Brush)FindResource(
            complaint is null ? "TextTertiary" : "Warning");
    }

    /// <summary>
    /// 地址和格式对不上的时候提醒一句。
    ///
    /// <para>
    /// 这是配服务商时最常见的错：格式选了 OpenAI，地址却是 Anthropic 那条路径（或者反过来）。
    /// 两边单独看都没问题，合在一起就是一个 404 或者 400 —— 而服务端的报错只说"请求不对"，
    /// 不会告诉你"你选错格式了"。
    /// </para>
    /// <para>
    /// 说得很轻：中转站的路径是自己定的，看不出来是正常的。所以这是提醒，不是判错。
    /// </para>
    /// </summary>
    private static string? AddressLooksOff(ApiFormat? format, string url)
    {
        if (url.Length == 0 || format is null)
            return null;

        var lower = url.ToLowerInvariant();

        // 只看"尾巴上有没有那个词"。中转站爱改前缀，但末段一般还是照抄上游的。
        return format switch
        {
            ApiFormat.Anthropic when !lower.Contains("message") =>
                "（这条地址里没有 messages —— Anthropic 那种接口的地址末尾是 /v1/messages。）",

            ApiFormat.Responses when !lower.Contains("response") =>
                "（这条地址里没有 responses —— OpenAI Responses 那种接口的地址末尾是 /v1/responses。）",

            ApiFormat.OpenAi when !lower.Contains("completion") =>
                "（这条地址里没有 completions —— OpenAI 那种接口的地址末尾是 /chat/completions。"
                + "中转站自己定的路径可以忽略这句。）",

            _ => null,
        };
    }

    private void ModelFormat_Changed(object sender, RoutedEventArgs e) => ShowEndpointHint();

    private void ModelAutoComplete_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            _modelForm.Reload();   // 改了就算这一页有未保存的改动

        ShowEndpointHint();
    }

    private void ModelEndpoint_Changed(object sender, TextChangedEventArgs e)
    {
        var suggestion = ModelClient.SuggestListEndpoint(ModelEndpointBox.Text);
        var current = ModelListEndpointBox.Text.Trim();

        // 只在"这个框还是空的、或者装的就是上次自动填进去那个"的时候才动它 ——
        // 人自己填过的东西，程序不该改。
        if (current.Length == 0 || current == _autoFilledListEndpoint)
        {
            _autoFilledListEndpoint = suggestion;
            ModelListEndpointBox.Text = suggestion;
        }

        ShowListEndpointHint();

        // 也要刷上面那一行：地址改了，"这条地址和选的格式对不对得上"的结论就变了。
        ShowEndpointHint();
    }

    private void ModelListEndpoint_Changed(object sender, TextChangedEventArgs e) => ShowListEndpointHint();

    private void ShowListEndpointHint()
    {
        var url = ModelListUrl();

        ModelListHint.Text = url.Length == 0
            ? "把请求地址填上，这里会自动带出一个（同域名下的 /v1/models）。"
            : $"「获取模型列表」会去取：{url}";
    }

    private string ModelListUrl()
    {
        var explicitUrl = ModelListEndpointBox.Text.Trim();
        return explicitUrl.Length > 0 ? explicitUrl : ModelClient.SuggestListEndpoint(ModelEndpointBox.Text);
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        var probe = CurrentProbe();
        if (probe is null)
            return;

        var url = ModelListUrl();
        FetchModelsButton.IsEnabled = false;
        ModelListHint.Text = $"正在取：{url}…";

        try
        {
            var result = await new ModelClient().ListModelsAsync(probe, CancellationToken.None);
            ModelListHint.Text = result.Message ?? "";

            if (result.Ok)
            {
                // 已经填着的模型名不能被列表冲掉 —— 人手写的那个可能正是列表里没有的。
                var typed = ModelCodePicker.Text;
                ModelCodePicker.ItemsSource = result.Models;

                if (result.Models.Contains(typed))
                    ModelCodePicker.SelectedItem = typed;
                else
                    ModelCodePicker.Text = typed;
            }
        }
        catch (Exception ex)
        {
            ModelListHint.Text = $"取的时候出错了：{ex.Message}";
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
        }
    }

    private void ApplyModel_Click(object sender, RoutedEventArgs e) => ApplyModelForm();

    private bool ApplyModelForm()
    {
        ReattachSelection();
        if (_selectedModel is null)
        {
            SayNothingSelected("服务商");
            return false;
        }

        // 如果这个条目还不在列表里（新建的），第一次保存时加进去。
        var isNew = !_state.Config.Models.Contains(_selectedModel);

        _selectedModel.Name = ModelNameBox.Text.Trim();
        _selectedModel.Note = ModelNoteBox.Text.Trim();
        _selectedModel.Website = ModelWebsiteBox.Text.Trim();
        _selectedModel.ModelListEndpoint = ModelListEndpointBox.Text.Trim();
        _selectedModel.Endpoint = ModelEndpointBox.Text.Trim();
        _selectedModel.AutoCompleteEndpoint = ModelAutoCompleteCheck.IsChecked == true;
        _selectedModel.Model = ModelCodePicker.Text.Trim();
        _selectedModel.ApiKey = CurrentKey();

        if (ModelFormatBox.SelectedItem is Choice<ApiFormat> format)
            _selectedModel.Format = format.Value;

        // 是否参与判断、先后顺序，都在「判断链」页里管；这里只剩"能够稳定提供服务"。
        //
        // 取消它 = 放弃「每页必判」那条保证 = 放宽，所以走和其他编辑同一条保存路径：
        // 保护开着的时候会被拒绝，然后弹令牌。
        _selectedModel.Stable = ModelStableCheck.IsChecked == true;

        if (isNew)
            _state.Config.Models.Add(_selectedModel);

        var saved = SaveAndReport();

        RefreshModelList();
        RefreshModelStatus();
        ShowValidationResult();

        if (saved)
        {
            CloseModelEditor(force: true);
            return true;
        }

        // 被拒了（要令牌之类）：配置已经被重新读了一遍，手上这个对象已经不在列表里。
        // 不把它换成新读出来的那个，下一次点保存会写进一个没人看的对象里。
        var id = _selectedModel.Id;
        _selectedModel = _state.Config.Models.FirstOrDefault(m => m.Id == id);

        if (_selectedModel is null)
            CloseModelEditor(force: true);
        else
            OpenModelEditor(_selectedModel);   // 按配置重填，不是只重置基线

        return false;
    }

    /// <summary>
    /// 真发一次请求问一句话，看这条路通不通。
    ///
    /// <para>
    /// 用的是**表单里现在填的**那份，不是存下来的那份 —— 你要测的正是"我这么填对不对"，
    /// 先存再测等于把一次可能的错配置写进文件里。
    /// </para>
    /// </summary>
    private async void TestModel_Click(object sender, RoutedEventArgs e)
    {
        var probe = CurrentProbe();
        if (probe is null)
            return;

        ModelTestResult.Text = "正在测试…";
        ModelTestResult.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");

        var (text, tone) = await ProbeAsync(probe);

        // 没通、而且这个地址"补一下就不一样" —— 那就拿补好的再试一次。
        //
        // 为什么要这样：「测试连接」用的是框里的原样地址，而用户最常见的错就是只写到 base
        // （甚至带个尾巴斜杠，于是连自动补全都跳过了）—— 那种情况 404 一弹，人只知道"地址不对"，
        // 却不知道对的长什么样。补一次、通了就把地址改对，比让他自己悟快得多。
        if (tone == "Error" && ModelClient.CompleteEndpoint(probe.Endpoint, probe.Format) is { } completed)
        {
            var retry = new ModelProvider
            {
                Id = probe.Id,
                Name = probe.Name,
                Format = probe.Format,
                Endpoint = completed,
                ApiKey = probe.ApiKey,
                Model = probe.Model,
                Note = probe.Note,
                Website = probe.Website,
                ModelListEndpoint = probe.ModelListEndpoint,
                AutoCompleteEndpoint = probe.AutoCompleteEndpoint,
            };
            var (retryText, retryTone) = await ProbeAsync(retry);

            if (retryTone != "Error")
            {
                ModelEndpointBox.Text = completed;
                ShowEndpointHint();
                _modelForm.Reload();

                text = $"你填的地址没通；补成「{completed}」之后通了 —— 已经帮你把地址改过来了。\n\n{retryText}";
                tone = retryTone;
            }
        }

        ModelTestResult.Text = text;
        ModelTestResult.Foreground = (System.Windows.Media.Brush)FindResource(tone);
    }

    /// <summary>把表单里现在填的东西拼成一个服务商，不动存下来的配置。</summary>
    private ModelProvider? CurrentProbe()
    {
        if (_selectedModel is null)
            return null;

        return new ModelProvider
        {
            Id = _selectedModel.Id,
            Name = ModelNameBox.Text.Trim(),
            Format = (ModelFormatBox.SelectedItem as Choice<ApiFormat>)?.Value ?? ApiFormat.OpenAi,
            Endpoint = ModelEndpointBox.Text.Trim(),
            Model = ModelCodePicker.Text.Trim(),
            ApiKey = CurrentKey(),
            ModelListEndpoint = ModelListEndpointBox.Text.Trim(),
        };
    }

    /// <summary>
    /// 试一次往返，把结果分三类说清楚。和 <see cref="ModelClient"/> 的失败分类一致：
    /// 通了 / 你得去处理（钥匙、欠费、限流）/ 连不上。
    /// </summary>
    private static async Task<(string Text, string Tone)> ProbeAsync(ModelProvider probe)
    {
        try
        {
            // 一段刻意平淡的内容。断言的是"能往返一趟"，不是模型判得准不准 ——
            // 判准不准要拿真实页面去验，这里只验管道。
            var snapshot = new ContentSnapshot(
                "https://example.com/", "连通性测试",
                "这是一段普通的测试文本，用于确认接口能正常往返。", DateTimeOffset.Now);

            var result = await new ModelClient()
                .JudgeAsync(probe, "禁止浏览与工作无关的娱乐内容", snapshot, CancellationToken.None);

            return result.Outcome switch
            {
                ModelCallOutcome.Answered => (
                    $"通了。接口返回了一个能读懂的判定（{(result.Violates ? "违规" : "不违规")}"
                    + $"{(result.Reason is null ? "" : "：" + result.Reason)}）。", "Success"),

                ModelCallOutcome.NeedsAttention => (result.Message ?? "需要处理。", "Danger"),

                // 把原因带出来。只说"连不上"的话，人只能去查网络，
                // 而"No such host is known"、"timed out"、"回了 200 但没有文字"是三件不同的事。
                _ => ($"连不上：{result.Message ?? "没网、超时、或者服务端出错了。"}"
                    + "\n这不影响别的功能 —— 模型判不出来时会先放行，等网络好了自己恢复。", "Warning"),
            };
        }
        catch (Exception ex)
        {
            return ($"测试时出错了：{ex.Message}", "Danger");
        }
    }

    private string UniqueModelId(string baseId)
    {
        var taken = _state.Config.Models.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        if (!taken.Contains(baseId))
            return baseId;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseId}-{i}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    // ------------------------------------------------------------------ 时间表

    private void RefreshScheduleList()
    {
        var wanted = _selectedSchedule;

        Programmatic(() =>
        {
            ScheduleList.ItemsSource = null;
            ScheduleList.ItemsSource = _state.Config.Schedules;

            if (wanted is not null && _state.Config.Schedules.Contains(wanted))
                ScheduleList.SelectedItem = wanted;
        });
    }

    private void ScheduleList_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // 换时间表 = 离开现在这个表单。
        if (IgnoreRevert())
            return;

        if (!ConfirmLeaveDirtyForm())
        {
            RevertSelection(ScheduleList, _selectedSchedule);
            return;
        }

        _selectedSchedule = ScheduleList.SelectedItem as Schedule;
        ScheduleForm.IsEnabled = _selectedSchedule is not null;

        if (_selectedSchedule is null)
        {
            _scheduleForm.Clear();
            return;
        }

        ScheduleIdBox.Text = _selectedSchedule.Id;

        var day = _selectedSchedule.DaysOfWeek;
        DayMonCheck.IsChecked = day.Contains(DayOfWeek.Monday);
        DayTueCheck.IsChecked = day.Contains(DayOfWeek.Tuesday);
        DayWedCheck.IsChecked = day.Contains(DayOfWeek.Wednesday);
        DayThuCheck.IsChecked = day.Contains(DayOfWeek.Thursday);
        DayFriCheck.IsChecked = day.Contains(DayOfWeek.Friday);
        DaySatCheck.IsChecked = day.Contains(DayOfWeek.Saturday);
        DaySunCheck.IsChecked = day.Contains(DayOfWeek.Sunday);

        ScheduleWindowsBox.Text = string.Join(Environment.NewLine,
            _selectedSchedule.TimeWindows.Select(w => $"{w.Start}-{w.End}"));

        _scheduleForm.Reload();
    }

    private void NewSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveDirtyForm())
            return;

        var id = "schedule";
        if (_state.Config.Schedules.Any(s => s.Id == id))
        {
            for (var i = 2; ; i++)
            {
                if (_state.Config.Schedules.All(s => s.Id != $"schedule-{i}"))
                {
                    id = $"schedule-{i}";
                    break;
                }
            }
        }

        var schedule = new Schedule { Id = id };
        schedule.TimeWindows.Add(new TimeWindow("09:00", "18:00"));

        _state.Config.Schedules.Add(schedule);
        SaveAndReport();
        RefreshScheduleList();
        ScheduleList.SelectedItem = schedule;
    }

    private void DeleteSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveDirtyForm())
            return;
        ReattachSelection();

        if (_selectedSchedule is null)
        {
            SayNothingSelected("计划");
            return;
        }

        var inUse = _state.Config.Targets
            .SelectMany(t => t.Rules)
            .Any(r => r.ScheduleId == _selectedSchedule.Id);

        if (inUse && MessageBox.Show(
                $"时间表「{_selectedSchedule.Id}」还有规则在用，删除后那些规则会变成始终生效。继续？", "Sentinel",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _state.Config.Schedules.Remove(_selectedSchedule);
        SaveAndReport();
        _selectedSchedule = null;
        ScheduleForm.IsEnabled = false;
        RefreshScheduleList();
    }

    private void ApplySchedule_Click(object sender, RoutedEventArgs e) => ApplyScheduleForm();

    private bool ApplyScheduleForm()
    {
        ReattachSelection();
        if (_selectedSchedule is null)
        {
            SayNothingSelected("计划");
            return false;
        }

        var windows = ParseWindows(ScheduleWindowsBox.Text, out var error);
        if (error is not null)
        {
            MessageBox.Show(error, "Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var id = ScheduleIdBox.Text.Trim();
        if (string.IsNullOrEmpty(id))
        {
            MessageBox.Show("名称不能为空。", "Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (_state.Config.Schedules.Any(s => s != _selectedSchedule && s.Id == id))
        {
            MessageBox.Show($"已经有一个叫「{id}」的时间表了。", "Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _selectedSchedule.Id = id;
        _selectedSchedule.TimeWindows = windows;
        _selectedSchedule.DaysOfWeek = SelectedDays();

        var saved = SaveAndReport();

        RefreshScheduleList();
        ShowValidationResult();
        _scheduleForm.Reload();

        return saved;
    }

    private List<DayOfWeek> SelectedDays()
    {
        var days = new List<DayOfWeek>();
        if (DayMonCheck.IsChecked == true) days.Add(DayOfWeek.Monday);
        if (DayTueCheck.IsChecked == true) days.Add(DayOfWeek.Tuesday);
        if (DayWedCheck.IsChecked == true) days.Add(DayOfWeek.Wednesday);
        if (DayThuCheck.IsChecked == true) days.Add(DayOfWeek.Thursday);
        if (DayFriCheck.IsChecked == true) days.Add(DayOfWeek.Friday);
        if (DaySatCheck.IsChecked == true) days.Add(DayOfWeek.Saturday);
        if (DaySunCheck.IsChecked == true) days.Add(DayOfWeek.Sunday);
        return days;
    }

    /// <summary>Parses one "HH:mm-HH:mm" window per line.</summary>
    private static List<TimeWindow> ParseWindows(string text, out string? error)
    {
        error = null;
        var windows = new List<TimeWindow>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !IsTimeOfDay(parts[0]) || !IsTimeOfDay(parts[1]))
            {
                error = $"看不懂这一行：{line}\n\n应该像这样：09:00-18:00（跨夜也支持，比如 22:00-06:00）";
                return windows;
            }

            windows.Add(new TimeWindow(parts[0], parts[1]));
        }

        return windows;

        static bool IsTimeOfDay(string value) =>
            TimeSpan.TryParse(value, out var parsed)
            && parsed >= TimeSpan.Zero
            && parsed < TimeSpan.FromDays(1);
    }

    // ------------------------------------------------------------------ 日志

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        LogPathText.Text = _state.LogDirectory;
        LogList.Items.Clear();

        var file = NewestAuditFile();
        if (file is null)
        {
            LogList.Items.Add("（还没有日志。规则命中、读取失败或动作执行时才会写入。）");
            return;
        }

        try
        {
            // 走 AuditFile.Tail 而不是 File.ReadLines：写的人（监控循环）正持有那个文件，
            // 而默认方式去读会在"监控正在跑"这个最常见的情况下失败 —— 恰恰是你要看日志的时候。
            foreach (var line in AuditFile.Tail(file.FullName, 300).Reverse())
                LogList.Items.Add(line);
        }
        catch (Exception ex)
        {
            LogList.Items.Add($"读取日志失败: {ex.Message}");
        }
    }

    private FileInfo? NewestAuditFile()
    {
        var directory = new DirectoryInfo(_state.LogDirectory);
        return directory.Exists
            ? directory.GetFiles("audit-*.jsonl").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
            : null;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_state.LogDirectory);

    // ------------------------------------------------------------------ 诊断

    private void RefreshWindows()
    {
        using var enumerator = new WindowEnumerator();

        _windows = enumerator.Enumerate()
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(w => new Choice<WindowRef>(w, $"{w.ProcessName}  —  {w.MainWindowTitle}"))
            .ToList();

        var previous = WindowPicker.SelectedItem as Choice<WindowRef>;
        WindowPicker.ItemsSource = null;
        WindowPicker.ItemsSource = _windows;
        WindowPicker.SelectedItem = previous is null
            ? null
            : _windows.FirstOrDefault(w => w.Value.ProcessId == previous.Value.ProcessId
                                           && w.Value.NativeHandle == previous.Value.NativeHandle);

        var previousDiag = DiagWindowPicker.SelectedItem as Choice<WindowRef>;
        DiagWindowPicker.ItemsSource = null;
        DiagWindowPicker.ItemsSource = _windows;

        // Pre-select something so the diagnose page is usable the moment it is opened.
        var keepDiag = previousDiag is null
            ? null
            : _windows.FirstOrDefault(w => w.Value.ProcessId == previousDiag.Value.ProcessId
                                           && w.Value.NativeHandle == previousDiag.Value.NativeHandle);
        DiagWindowPicker.SelectedItem = keepDiag ?? _windows.FirstOrDefault();
    }

    private void RefreshWindowPicker_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshDiagWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void UsePickedProcess_Click(object sender, RoutedEventArgs e)
    {
        if (WindowPicker.SelectedItem is not Choice<WindowRef> choice)
            return;

        var existing = TargetProcessBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!existing.Contains(choice.Value.ProcessName, StringComparer.OrdinalIgnoreCase))
            existing.Add(choice.Value.ProcessName);

        TargetProcessBox.Text = string.Join(", ", existing);
    }

    private void RunDiagnose_Click(object sender, RoutedEventArgs e)
    {
        if (DiagWindowPicker.SelectedItem is not Choice<WindowRef> choice)
        {
            DiagVerdictText.Text = "先选一个窗口。";
            DiagVerdictText.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        var window = choice.Value;
        var wantBody = DiagBodyCheck.IsChecked == true;

        DiagVerdictText.Text = "正在读取…";
        DiagVerdictText.Foreground = System.Windows.Media.Brushes.Gray;
        DiagDetailText.Text = "";
        DiagBodyBox.Text = "";

        try
        {
            using var reader = new UiaContentReader();
            var snapshot = reader.Read(window, new ReaderOptions { ScanBody = wantBody });
            ShowDiagnoseResult(window, snapshot, wantBody);
        }
        catch (Exception ex)
        {
            DiagVerdictText.Text = $"✗ 读取失败：{ex.Message}";
            DiagVerdictText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private void ShowDiagnoseResult(WindowRef window, ContentSnapshot? snapshot, bool wantBody)
    {
        if (snapshot is null)
        {
            DiagVerdictText.Text = "✗ 读不到内容 — 这个应用目前监控不了。";
            DiagVerdictText.Foreground = System.Windows.Media.Brushes.Firebrick;
            DiagDetailText.Text = $"进程: {window.ProcessName} ({window.ProcessId})";
            return;
        }

        var hasUrl = !string.IsNullOrWhiteSpace(snapshot.Url);
        var hasTitle = !string.IsNullOrWhiteSpace(snapshot.Title);
        var bodyLength = snapshot.BodyText?.Length ?? 0;

        var usable = hasUrl || hasTitle || bodyLength > 0;
        DiagVerdictText.Text = usable
            ? "✓ 可以监控 — 下面的内容都能被规则匹配。"
            : "✗ 读不到有效内容。";
        DiagVerdictText.Foreground = usable
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.Firebrick;

        DiagDetailText.Text =
            $"进程: {window.ProcessName} ({window.ProcessId})\n" +
            $"标题: {snapshot.Title ?? "（无）"}\n" +
            $"网址: {snapshot.Url ?? "（无）"}\n" +
            $"正文: {(wantBody ? $"{bodyLength} 字符" : "（未读取，勾选上面的「读取正文」）")}";

        DiagBodyBox.Text = snapshot.BodyText ?? "";
    }

    // ---- 模型监督自检

    private void RefreshDiagTargets()
    {
        var picked = DiagModelTargetPicker.SelectedItem as Target;

        DiagModelTargetPicker.ItemsSource = _state.Config.Targets;
        DiagModelTargetPicker.SelectedItem =
            picked is not null && _state.Config.Targets.Contains(picked)
                ? picked
                : _state.Config.Targets.FirstOrDefault();
    }

    private async void RunModelDiagnose_Click(object sender, RoutedEventArgs e)
    {
        if (DiagModelTargetPicker.SelectedItem is not Target target)
        {
            DiagModelBox.Text = "先在左边选一个目标（去「目标」页建一个）。";
            return;
        }

        DiagModelButton.IsEnabled = false;
        DiagModelBox.Text = "正在查…";

        try
        {
            // 窗口列表可能还是空的（刚打开这一页）—— 那就现枚举一次。
            if (_windows.Count == 0)
                RefreshWindows();

            DiagModelBox.Text = await DiagnoseModelAsync(target);
        }
        catch (Exception ex)
        {
            DiagModelBox.Text = $"自检出错了：{ex.GetType().Name}：{ex.Message}";
        }
        finally
        {
            DiagModelButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 把"模型监督在这份内容上会不会生效"拆成五步，每一步都把看到的东西写出来。
    ///
    /// <para>
    /// 为什么需要它：这条链上任何一处错位，**表现完全一样** —— 打开那个网页，什么事也没发生。
    /// 没有异常、没有日志、界面上看不出区别，所以只能一步一步看。有了这个，"卡在哪一步"
    /// 是读出来的，不是猜出来的：窗口没匹配上 / 读不到内容 / 规则类型不对 / 字段选错 /
    /// 没启用服务商 / 模型判的是不违规 / 判了违规但监控没拿到。
    /// </para>
    /// <para>
    /// 它**直接问模型**，绕开监控那层缓存 —— 测的就是"模型会怎么说"，而不是"缓存里现在是什么"。
    /// 缓存里那份在第 5 步单独列出来，两者对不上本身就说明问题。
    /// </para>
    /// </summary>
    private async Task<string> DiagnoseModelAsync(Target target)
    {
        var steps = new List<string>();
        var report = new List<string>();

        // ---- 1. 窗口
        var windows = _windows.Select(c => c.Value).ToList();
        var matches = windows.Where(w => TargetMatcher.Matches(target, w)).ToList();

        report.Add($"1. 目标「{target.DisplayName}」→ 进程名 [{string.Join(", ", target.ProcessNames)}]");

        if (matches.Count == 0)
        {
            steps.Add("屏幕上没有任何窗口匹配这个目标");

            report.Add($"   屏幕上一共 {windows.Count} 个窗口，一个都对不上。");
            report.Add("   现在屏幕上有这些进程（挑一个填进目标的「进程名」，不要带 .exe）：");
            report.Add("     " + string.Join("、", windows.Select(w => w.ProcessName).Distinct().OrderBy(x => x)));

            return Summarize(steps, report);
        }

        var window = matches.FirstOrDefault(w => w.IsForeground) ?? matches[0];
        report.Add($"   {matches.Count} 个窗口匹配，挑了这个：PID {window.ProcessId}「{window.MainWindowTitle}」");
        report.Add($"   前台={Yes(window.IsForeground)}  露在屏幕上={Yes(window.IsExposed)}  最小化={Yes(window.IsMinimized)}");

        if (window.IsMinimized)
            steps.Add("挑中的那个窗口是最小化的 —— 最小化的不读也不判");

        if (!window.IsExposed)
        {
            report.Add("   ⚠ 它现在**没露在屏幕上**（被别的窗口完全盖住，或者刚被最小化）。"
                       + "监控的判据是「露在屏幕上就得管」，一点都看不见的不读 —— "
                       + "把手头的窗口挪开一点，让它露出来，再测一次。");
            steps.Add("挑中的那个窗口没露在屏幕上 —— 监控现在不会看它");
        }
        else if (!window.IsForeground)
        {
            report.Add("   · 它不在前台，但**露在屏幕上** —— 监控照样会判它"
                       + "（每轮最多读 4 个，前台优先，其余按最久没读过的轮着来，最坏等几轮）。");
        }

        // ---- 2. 读内容
        ContentSnapshot? snapshot;
        using (var reader = new UiaContentReader())
            snapshot = reader.Read(window, target.Reader);

        if (snapshot is null)
        {
            steps.Add("这个窗口读不出内容");
            report.Add("2. 读不到内容 —— 这个应用现在监控不了。");
            return Summarize(steps, report);
        }

        var url = snapshot.Url ?? "";
        var title = snapshot.Title ?? "";
        var body = snapshot.BodyText ?? "";

        report.Add($"2. 读到的内容：网址 {url.Length} 字，标题 {title.Length} 字，正文 {body.Length} 字");
        report.Add($"   网址：{(url.Length == 0 ? "(没有)" : url)}");
        report.Add($"   标题：{(title.Length == 0 ? "(没有)" : title)}");

        if (!target.Reader.ScanBody)
            report.Add("   ⚠ 这个目标没读「正文」—— 模型只拿得到网址和标题。"
                       + "（**规则里勾的字段是「拿什么来判断」，目标页的「读取正文」是「去不去读」，"
                       + "两个都要有。**）想让它看页面内容，去「目标」页勾上「读取正文」再测一次。");

        // ---- 3. 规则
        var rules = target.Rules.Where(r => r.Enabled && r.Match.Type == MatchKind.Model).ToList();
        report.Add($"3. 启用的模型监督规则：{rules.Count} 条");

        if (rules.Count == 0)
        {
            steps.Add("这个目标上一条「模型监督」类型的规则都没有");
            report.Add("   别的类型（关键词 / 正则 / 白名单）不会去问模型。");
            report.Add("   去「规则」页：类型选「模型监督」，下面那个框里写人话，"
                       + "比如「和游戏有关的页面，尤其是攻略和排行榜」。");
            return Summarize(steps, report);
        }

        var rule = rules.OrderByDescending(r => r.Priority).First();
        var instruction = string.Join("\n", rule.Match.Patterns);

        report.Add($"   用优先级最高的那条：禁令「{instruction}」");
        report.Add($"      匹配字段 [{string.Join(", ", rule.Match.Fields)}]，策略 {rule.PolicyId}");

        if (rule.Match.Fields.Contains(MatchField.Body) && body.Length == 0)
        {
            steps.Add("规则要匹配「正文」，但这个目标没读正文 —— 它永远不会命中");
            report.Add("   ✗ 规则要匹配「正文」，而这个目标没读正文。");
        }

        if (rule.Match.Fields.Contains(MatchField.Url) && url.Length == 0)
        {
            steps.Add("规则要匹配「网址」，但这个窗口读不到网址");
            report.Add("   ✗ 规则要匹配「网址」，而这个窗口读不到网址。");
        }

        if (rule.ScheduleId is not null
            && _state.Config.Schedules.FirstOrDefault(s => s.Id == rule.ScheduleId) is { } schedule
            && !ScheduleEvaluator.IsActive(schedule, DateTimeOffset.Now))
        {
            steps.Add($"这条规则挂着的时间表「{schedule.Id}」现在不在生效时段");
            report.Add($"   ✗ 时间表「{schedule.Id}」现在不在生效时段。");
        }

        var policy = _state.Config.Policies.FirstOrDefault(p => p.Id == rule.PolicyId);

        if (policy is null)
        {
            steps.Add("这条规则没有绑到任何策略 —— 命中之后什么也不会执行");
            report.Add("   ✗ 没绑策略（或者绑的那个已经不在了）。");
        }
        else if (policy.Actions.Count == 0)
        {
            steps.Add($"策略「{policy.Name}」一个动作都没有 —— 只记录，不拦");
            report.Add($"   ✗ 策略「{policy.Name}」的动作列表是空的。");
        }

        // ---- 4. 问模型（走**判断链**，和监控同一条路径）
        //
        // 优先用监控手上那个监督器；它没建起来（监控没跑）就临时造一个 ——
        // "监控没在跑"恰恰是最需要自检的时候，不能因此把这一步跳过。
        var chainTester = _state.Monitor.ModelSupervisor ?? new ModelSupervisor(() => _state.Config);

        report.Add("4. 问模型（按判断链依次问，这一步会真的发请求）："
                   + (_state.Monitor.ModelSupervisor is null ? "（监控没在跑，这里用一个临时监督器问）" : ""));

        var watch = Stopwatch.StartNew();
        var asked = await chainTester.DiagnoseAsync(instruction, snapshot, CancellationToken.None);
        watch.Stop();

        if (asked.Count == 0)
        {
            steps.Add("判断链是空的 —— 模型监督现在什么都不判");
            report.Add("   判断链是空的。去「大语言模型」→「判断链」把要用的供应商加进来。");
            return Summarize(steps, report);
        }

        foreach (var (model, answer) in asked)
        {
            var line = $"   {model.Name} / {model.Model} → {DescribeOutcome(answer)}";

            if (answer.Outcome == ModelCallOutcome.Answered)
            {
                line += answer.Violates
                    ? "    判「违规」" + (answer.Reason is null ? "" : $"（{answer.Reason}）")
                    : "    判「不违规」";

                line += answer.StrictViolates is { } strict
                    ? strict == answer.Violates ? "    · 从严一致，定论" : "    · 从严不一致，继续往下问"
                    : "    · 没给从严值，继续往下问";
            }
            else if (answer.Message is { Length: > 0 })
            {
                line += $"：{answer.Message}";
            }

            report.Add(line);
        }

        report.Add($"   总用时 {watch.Elapsed.TotalSeconds:0.0} 秒");

        var chainAnswer = asked
            .Select(a => a.Result)
            .LastOrDefault(r => r.Outcome == ModelCallOutcome.Answered);

        if (chainAnswer is null)
            steps.Add("链上一个模型都没给出判定");
        else if (!chainAnswer.Violates)
            steps.Add("链上所有模型都判「不违规」—— 该改的是那句禁令，写具体一点");

        // ---- 5. 监控那边现在记着什么
        if (!_state.Monitor.IsRunning)
        {
            steps.Add("监控没有在跑 —— 判定做得再对也没人执行");
            report.Add("5. 监控没在跑（「概览」页看一眼）。");
        }
        else if (_state.Monitor.ModelSupervisor is { } supervisor)
        {
            // 第 5 步说的是**监控手上那条**，不是刚才问出来的那条 —— 它可能是另一个时刻、
            // 甚至另一份内容的判定（同一个地址 60 秒内不重问）。所以要把它是哪儿来的说清楚，
            // 否则"测试说不违规、监控说违规"看起来就像这套东西在乱判。
            var held = supervisor.Explain(rule.Id, snapshot);

            report.Add($"5. 监控现在记着的判定：{DescribeVerdict(held.Verdict)}"
                       + $"（{held.How}"
                       + (held.Age is { } age ? $"，那是 {age.TotalSeconds:0} 秒前的事" : "")
                       + "）");

            if (chainAnswer is not null
                && held.Verdict.Kind != ModelVerdictKind.Unknown
                && chainAnswer.Violates != held.Verdict.IsBlocked)
            {
                report.Add("   ⚠ 这一次问的结论，和监控手上那条不一样。两件事凑在一起：监控是"
                           + "**异步**判的（上一轮问的，这一轮才用上），而且**同一个地址 60 秒内"
                           + "不重复问** —— 上面那条可能是几秒前、乃至另一个时刻留下的判定。"
                           + "两三次都这样再看我。");
            }

            if (supervisor.BlockingIssue is { } issue)
                report.Add($"   监控那边报的问题：{issue}");
        }
        else
        {
            report.Add("5. 监控还没建起来（服务商那边没接上）。");
        }

        return Summarize(steps, report);
    }

    /// <summary>结论放最上面 —— 第一步要知道的是"有没有问题"，明细是给要修的时候看的。</summary>
    private static string Summarize(List<string> problems, List<string> report)
    {
        var lines = new List<string>();

        if (problems.Count == 0)
        {
            lines.Add("没有发现断点：这条链现在每一环都是通的。");
        }
        else
        {
            lines.Add("发现的问题：");
            lines.AddRange(problems.Select(p => "  ✗ " + p));
        }

        lines.Add("");
        lines.Add("—— 下面是一步步看到的东西 ——");
        lines.AddRange(report);

        return string.Join(Environment.NewLine, lines);
    }

    private static string Yes(bool value) => value ? "是" : "否";

    private static string DescribeOutcome(ModelCallResult result) => result.Outcome switch
    {
        ModelCallOutcome.Answered => "拿到判定",
        ModelCallOutcome.NeedsAttention => "需要你去处理（钥匙 / 欠费 / 限流）",
        _ => "没拿到（连不上、超时，或者回来的东西读不懂）",
    };

    private static string DescribeVerdict(ModelVerdict verdict) => verdict.Kind switch
    {
        ModelVerdictKind.Blocked => $"违规（{verdict.Reason ?? "模型没给原因"}）",
        ModelVerdictKind.Allowed => "不违规",
        _ => "还没判过这份内容",
    };

    // ------------------------------------------------------------------ 工具

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), out var value) ? Math.Clamp(value, min, max) : fallback;

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开目录：{ex.Message}", "Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
