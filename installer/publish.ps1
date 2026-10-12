<#
    发布 Sentinel 到 staging 目录 —— 用普通用户权限运行，**不要提权**。

    为什么单独拆出来：dotnet publish 会执行 MSBuild、NuGet 包的构建脚本和仓库里的自定义 task。
    以管理员身份跑，等于把整条构建链（以及仓库里可能被改过的任何东西）提升到管理员权限。
    提权只应发生在"拷贝文件 + 注册服务"这种看得见的事情上，那一步在 install.ps1。

    这个脚本的参数是 -OutDir（发布到哪），**不是** -InstallDir。
    两步的完整用法：

        .\publish.ps1 -OutDir E:\Sentinel-stage
        .\install.ps1 -Source E:\Sentinel-stage -InstallDir E:\Sentinel

    或者 staging 就留在仓库里，只给 install 指定最终位置：

        .\publish.ps1
        .\install.ps1 -Source .\publish -InstallDir E:\Sentinel
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Assert-SafeDirectory {
    param([string]$Path, [string]$What)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$What 不能为空。" }

    $full = [System.IO.Path]::GetFullPath($Path)

    # 下面会递归删除这个目录，和 install/uninstall 一样要挡住传空或传成盘根
    if ($full -eq [System.IO.Path]::GetPathRoot($full)) { throw "拒绝在驱动器根目录上操作：$full" }
    if ($full.TrimEnd('\').Length -lt 4) { throw "路径太短，像是误传：$full" }

    return $full
}

# 在正文里取默认值：$PSScriptRoot 在 param() 的默认值表达式里可能还没赋值
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'publish' }

$root = Split-Path -Parent $PSScriptRoot
$OutDir = Assert-SafeDirectory -Path $OutDir -What '发布目录'

Write-Host "发布到 $OutDir"

if (Test-Path $OutDir) {
    # 路径长度挡不住"指错了目录"。只在能确认这是上一次的发布产物时才递归删除 ——
    # 否则一个 -OutDir 打错就会清空你某个真实目录。
    if (-not (Test-Path (Join-Path $OutDir 'publish-manifest.json')) -and -not $Force) {
        throw "$OutDir 已存在，但里面没有 publish-manifest.json，不像是发布产物。`n拒绝递归删除它。确认要清空请加 -Force。"
    }

    Remove-Item -Recurse -Force $OutDir
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# DebugType=None：不要 .pdb。它们会带上源码的绝对路径，而且对拿到这个包的人来说没有用处
# ——发布产物是给"装上去用"的，不是给"在这台机器上调试"的。
dotnet publish (Join-Path $root 'src\Sentinel.Service') -c Release -o $OutDir --nologo `
    -p:DebugType=None -p:DebugSymbols=false
dotnet publish (Join-Path $root 'src\Sentinel.Agent') -c Release -o $OutDir --nologo `
    -p:DebugType=None -p:DebugSymbols=false

$expected = @('Sentinel.Service.exe', 'Sentinel.Agent.exe')
foreach ($name in $expected) {
    if (-not (Test-Path (Join-Path $OutDir $name))) { throw "发布失败：找不到 $name" }
}

# 两个解锁工具页（生成密钥对 / 签名换令牌）也随安装走。
#
# 它们以前只存在于仓库的 tools/ 下 —— 仓库一挪或者一删，用户就没有签名工具了，而那正是
# "想放宽限制时唯一要走的一步"。放进安装目录之后，程序能直接打开它们，用户也能从这里
# 拷一份到手机里（私钥始终留在手机上）。
$pageDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\unlock-page'
foreach ($page in @('sentinel-keygen.html', 'sentinel-sign.html')) {
    $source = Join-Path $pageDir $page
    if (-not (Test-Path $source)) { throw "发布失败：找不到 $source" }
    Copy-Item -Path $source -Destination (Join-Path $OutDir $page) -Force
}

# 哈希全部产物，不只是那两个 exe。exe 只是 apphost —— 一个一百多 KB 的启动器，
# 真正的代码在 Sentinel.*.dll 里。只哈希 exe 的话，这份清单里没有一个字节是有意义的：
# 把任意一个 dll 换掉，清单照样对得上。
# 记下哈希：安装时核对，并留一份凭据说明"装进去的到底是哪一版二进制"。
# 注意这不是防篡改 —— 能改二进制的人同样能改这份清单；真正的防护需要代码签名。
$files = Get-ChildItem -Path $OutDir -File -Recurse |
    Where-Object { $_.Name -ne 'publish-manifest.json' } |
    Sort-Object FullName

$manifest = foreach ($file in $files) {
    [ordered]@{
        Name   = $file.FullName.Substring($OutDir.Length).TrimStart('\')
        Sha256 = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
        Bytes  = $file.Length
    }
}

$manifestPath = Join-Path $OutDir 'publish-manifest.json'
@{
    BuiltAt  = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Machine  = $env:COMPUTERNAME
    Files    = $manifest
} | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host ''
Write-Host "已发布 $($manifest.Count) 个文件，全部记入清单："
$manifest | Where-Object { $_.Name -in $expected } |
    ForEach-Object { Write-Host ("  {0,-24} {1}" -f $_.Name, $_.Sha256) }
Write-Host ("  …… 另有 {0} 个依赖文件也已记入" -f ($manifest.Count - $expected.Count))
Write-Host ''
Write-Host "清单：$manifestPath"
Write-Host '下一步（这一步才需要管理员）：'
Write-Host "    .\install.ps1 -Source '$OutDir'"
