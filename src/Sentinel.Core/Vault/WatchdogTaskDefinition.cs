using System.Xml.Linq;

namespace Sentinel.Core.Vault;

/// <summary>
/// 读任务计划程序存下来的那份 XML，回答一个窄问题：**这个任务被停用了吗。**
///
/// <para>
/// 为什么要看这个：看门狗被"停用"和"删掉"是一回事 —— 都是把它关掉了，而停用只需要在任务
/// 计划程序里点一下。以前判"防护被拆了"靠的是心跳文件变旧，而心跳变旧有一堆无害的原因
/// （合盖待机最典型，2026-10-12 就这样误封过一次真实的保险箱）。停用是一个**明确的状态**，
/// 不需要靠猜。
/// </para>
/// <para>
/// 只认显式的 <c>&lt;Enabled&gt;false&lt;/Enabled&gt;</c>：任务计划程序只在停用时才写这一项，
/// 正常任务是启用状态、这一项要么是 true 要么干脆没有。看不懂的 XML 一律当作"没停用" ——
/// 这一头是"封印保险箱"，宁可漏报也不误报。
/// </para>
/// </summary>
public static class WatchdogTaskDefinition
{
    public static bool IsDisabled(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return false;

        try
        {
            var enabled = XDocument.Parse(xml)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Enabled");

            return enabled is not null
                   && string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // 看不懂不是"被停用了"
        }
    }
}
