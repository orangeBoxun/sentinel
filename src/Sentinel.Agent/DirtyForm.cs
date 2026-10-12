using System.Windows;
using System.Windows.Controls;

namespace Sentinel.Agent;

/// <summary>
/// 一个表单"改了但还没点应用修改"的判定。
///
/// <para>
/// <b>它解决的是什么：</b>所有表单都是**必须点「应用修改」才写下去**的。在那之前切页签、或者在列表里
/// 换一个条目，改动就静悄悄地没了 —— 而界面上看起来你已经改好了。这不是保存失败，是**根本没触发保存**，
/// 而且不告诉你。要解决它，先得能知道"现在有没有没应用的东西"。
/// </para>
/// <para>
/// <b>怎么判断：</b>把表单里现在填的东西拼成一个字符串，和刚载入时那份比。不比较实体对象，因为编辑落在
/// 控件里、实体要按了「应用修改」才变 —— 比实体永远相等，等于什么也没比。
/// </para>
/// </summary>
internal sealed class DirtyForm
{
    private readonly Func<string> _signature;
    private readonly Func<bool> _apply;
    private readonly TextBlock _mark;
    private readonly Button _button;
    private readonly Style _loud;
    private readonly Style _quiet;

    /// <summary>表单刚载入时的样子。null = 现在没有选中任何条目，谈不上脏。</summary>
    private string? _baseline;

    public DirtyForm(
        int tabIndex,
        string name,
        Func<string> signature,
        Func<bool> apply,
        TextBlock mark,
        Button button,
        Style loud,
        Style quiet)
    {
        TabIndex = tabIndex;
        Name = name;
        _signature = signature;
        _apply = apply;
        _mark = mark;
        _button = button;
        _loud = loud;
        _quiet = quiet;
    }

    /// <summary>在哪个页签上。</summary>
    public int TabIndex { get; }

    /// <summary>给用户看的名字（弹窗里说"哪一页"）。</summary>
    public string Name { get; }

    public bool IsDirty => _baseline is not null && _baseline != _signature();

    /// <summary>表单载入了内容（换了选中项，或者刚存过一次）—— 以现在的样子作为新的基线。</summary>
    public void Reload()
    {
        _baseline = _signature();
        Refresh();
    }

    /// <summary>表单空了（没选中任何条目）。</summary>
    public void Clear()
    {
        _baseline = null;
        Refresh();
    }

    /// <summary>应用修改。返回 false 表示没存下去（比如要令牌被拒了）—— 那就等于没应用。</summary>
    public bool Apply() => _apply();

    public void Refresh()
    {
        var dirty = IsDirty;

        _mark.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;

        // 按钮**不**因为"没有改动"而变灰：签名万一漏了某个字段，变灰就会让人连点都点不了，
        // 那比没有这个提示糟得多。改成换一身样式 —— 干净时是安静的中性按钮，脏了才是那个响的绿按钮。
        _button.Style = dirty ? _loud : _quiet;
    }
}
