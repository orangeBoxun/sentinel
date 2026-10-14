using System.Security.AccessControl;
using System.Security.Principal;

namespace Sentinel.Platform;

/// <summary>
/// 把一个文件收成"只有 SYSTEM 和管理员能读"。
///
/// <para>
/// 给本机秘密用的（<c>&lt;配置目录&gt;\.vault-machine</c>）：影子里的保险箱密码门绑着那份秘密，
/// 所以谁能读到那个文件，谁就拿到了绕开"绑定机器"这一层的一半。它防的正是最省事的那条路 ——
/// 顺手把 vault 目录拷一份出去。
/// </para>
/// <para>
/// 放在 Platform 而不是 Core：这套 ACL API 只有 Windows 目标框架上有。
/// </para>
/// </summary>
public static class FileAcl
{
    /// <summary>收不成功只是变弱、不是变坏，所以返回值交给调用方去记一笔，不抛。</summary>
    public static bool RestrictToSystemAndAdmins(string path)
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var sid in new[]
                     {
                         WellKnownSidType.LocalSystemSid,
                         WellKnownSidType.BuiltinAdministratorsSid,
                     })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            new FileInfo(path).SetAccessControl(security);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
