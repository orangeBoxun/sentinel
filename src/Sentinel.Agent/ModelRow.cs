using Sentinel.Core.Models;

namespace Sentinel.Agent;

/// <summary>
/// 服务商列表里的一行。
///
/// <para>
/// 单独做一个行对象，而不是把 <see cref="ModelProvider"/> 直接绑到界面上：那一行要显示"是不是正在
/// 用的那个"，而这是 <c>config.ActiveModelId</c> 决定的、不属于服务商自己 —— 把它塞进被序列化的
/// 模型里，两份真话早晚会不一致。
/// </para>
/// </summary>
public sealed class ModelRow
{
    public ModelRow(ModelProvider provider, bool isActive)
    {
        Provider = provider;
        IsActive = isActive;
        Initial = InitialOf(provider.Name);
        Name = string.IsNullOrWhiteSpace(provider.Name) ? "(没名字)" : provider.Name;
        Endpoint = string.IsNullOrWhiteSpace(provider.Endpoint) ? "(还没填请求地址)" : provider.Endpoint;
    }

    public ModelProvider Provider { get; }

    public bool IsActive { get; }

    public string Name { get; }

    public string Endpoint { get; }

    /// <summary>头像块里的那一个字。中文取第一个字，英文取首字母大写。</summary>
    public string Initial { get; }

    private static string InitialOf(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "?";

        var first = trimmed[0];
        return char.IsAsciiLetter(first)
            ? char.ToUpperInvariant(first).ToString()
            : first.ToString();
    }
}

/// <summary>
/// 「判断链」页里的一行：顺序、名字、是否稳定、连通性测试结果。
///
/// <para>
/// 单独一个对象（而不是直接绑 <see cref="ModelProvider"/>）：位置和测试结果都不属于配置，
/// 而且都会在界面上变，需要通知。
/// </para>
/// </summary>
public sealed class ChainRow : System.ComponentModel.INotifyPropertyChanged
{
    private int _position;
    private string _testResult = "";
    private System.Windows.Visibility _resultVisibility = System.Windows.Visibility.Collapsed;

    public ChainRow(ModelProvider provider, int position)
    {
        Provider = provider;
        _position = position;
        Name = string.IsNullOrWhiteSpace(provider.Name) ? "(没名字)" : provider.Name;
        Endpoint = string.IsNullOrWhiteSpace(provider.Endpoint) ? "(还没填请求地址)" : provider.Endpoint;
        Initial = InitialOf(provider.Name);
    }

    public ModelProvider Provider { get; }

    public string Name { get; }

    public string Endpoint { get; }

    public string Initial { get; }

    /// <summary>从 1 开始的判断顺序。</summary>
    public string Position => (_position + 1).ToString();

    public System.Windows.Visibility StableVisibility =>
        Provider.Stable ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string TestResult
    {
        get => _testResult;
        set
        {
            _testResult = value;
            ResultVisibility = string.IsNullOrEmpty(value)
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;

            Raise(nameof(TestResult));
        }
    }

    public System.Windows.Visibility ResultVisibility
    {
        get => _resultVisibility;
        private set
        {
            _resultVisibility = value;
            Raise(nameof(ResultVisibility));
        }
    }

    public void SetPosition(int index)
    {
        _position = index;
        Raise(nameof(Position));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    private static string InitialOf(string name)
    {
        var trimmed = name.Trim();

        if (trimmed.Length == 0)
            return "?";

        var first = trimmed[0];
        return char.IsAsciiLetter(first)
            ? char.ToUpperInvariant(first).ToString()
            : first.ToString();
    }
}

/// <summary>「用量」页里的一行。只是显示用的快照，每次刷新重建。</summary>
public sealed class UsageRow
{
    public UsageRow(string name, string today, string week, string month)
    {
        Name = name;
        Today = today;
        Week = week;
        Month = month;
    }

    public string Name { get; }

    public string Today { get; }

    public string Week { get; }

    public string Month { get; }
}
