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
