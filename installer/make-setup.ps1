#Requires -Version 5.1
<#
    打一个"能直接发给别人"的安装包目录。

    产出的东西长这样（Sentinel.Setup.exe 靠这个布局找脚本和程序，别再挪）：

        Sentinel-Setup\
            Sentinel.Setup.exe      双击这个
            install.ps1             它驱动的两个脚本
            uninstall.ps1
            payload\                程序和依赖（publish 的输出）
            安装说明.txt

    为什么让图形安装程序去驱动脚本，而不是把安装逻辑写成 C#：那两个脚本里有一堆不能出错的
    细节（安装目录/配置/日志/保险箱各自的 ACL、服务的 DACL、失败恢复策略）。重写等于把一份
    跑通过的东西换成一份没跑过的，而装错了的代价是"看着装上了、其实保护是假的"。

        .\make-setup.ps1 -OutDir E:\Sentinel-Setup
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutDir,

    # 已经 publish 好的 staging。不给就现跑一次 publish.ps1（要几秒）。
    [string]$Payload
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent

if (-not [System.IO.Path]::IsPathRooted($OutDir)) {
    throw "OutDir 要是完整路径，比如 E:\Sentinel-Setup"
}

$OutDir = [System.IO.Path]::GetFullPath($OutDir)

if ($OutDir -eq [System.IO.Path]::GetPathRoot($OutDir)) {
    throw "别往盘根上打。"
}

Write-Host "打安装包到 $OutDir"

# ---- 1. 程序本体
if (-not $Payload) {
    $Payload = Join-Path $env:TEMP ("sentinel-setup-payload-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    & (Join-Path $PSScriptRoot 'publish.ps1') -OutDir $Payload | Out-Null
}

if (-not (Test-Path (Join-Path $Payload 'Sentinel.Agent.exe'))) {
    throw "在 $Payload 里找不到 Sentinel.Agent.exe —— 那个目录不是 publish 的输出。"
}

# ---- 2. 安装程序自己（单文件，框架依赖 —— 和主程序要的东西一样，不额外加前提）
$setupOut = Join-Path $env:TEMP ("sentinel-setup-app-" + [guid]::NewGuid().ToString('N').Substring(0, 8))

dotnet publish (Join-Path $root 'src\Sentinel.Setup') -c Release -o $setupOut --nologo `
    -p:PublishSingleFile=true --self-contained false | Out-Null

if (-not (Test-Path (Join-Path $setupOut 'Sentinel.Setup.exe'))) {
    throw "安装程序没构建出来。"
}

# ---- 3. 组装
if (Test-Path $OutDir) {
    try {
        Remove-Item $OutDir -Recurse -Force -ErrorAction Stop
    } catch {
        # 几乎总是同一个原因：那个目录里的 Sentinel.Setup.exe 正开着（运行中的 exe 锁住自己
        # 的文件）。与其抛一句 PermissionDenied 让人猜，不如直接说清楚该怎么办。
        throw @"
清不掉 $OutDir —— 里面的 Sentinel.Setup.exe 多半正开着。

先把它关掉再打一次。如果窗口已经关了却还是清不掉，那就还有一个**没有窗口**的旧进程在跑
（早期版本漏了启动钩子，双击之后进程活着但什么都不显示）：任务管理器 →「详细信息」→
找 Sentinel.Setup.exe → 结束任务。旧进程是提权的，任务管理器要"以管理员身份运行"才杀得掉。

或者换一个 -OutDir。
"@
    }
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

Copy-Item (Join-Path $setupOut 'Sentinel.Setup.exe') $OutDir
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $OutDir
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $OutDir

# 保护模型那份文档也放进去：装之前该看的是它（谁拦谁、什么情况下会封印、哪些拦不住），
# 而不是安装说明里那几行。
$model = Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\保护模型.md'
if (Test-Path $model) {
    Copy-Item $model $OutDir
}

$payloadDir = Join-Path $OutDir 'payload'
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
Copy-Item (Join-Path $Payload '*') $payloadDir -Recurse -Force

@'
Sentinel 安装说明

怎么装
    双击 Sentinel.Setup.exe，选一个安装位置，点「安装 / 更新」。
    会弹一次 UAC —— 装 Windows 服务必须管理员。

    默认装在 %ProgramData%\Sentinel。想装到别处（比如避免占用 C 盘），
    在那个框里改，或者在「浏览…」里挑。

装完
    托盘里会出现 Sentinel 的图标（后台服务负责把它拉起来）。
    没看到就注销一次再登录。

前提
    这台机器要装 .NET 8 Desktop Runtime（x64）。
    没有的话服务起不来，事件查看器里会记一条 —— 去这里下：
    https://dotnet.microsoft.com/download/dotnet/8.0

卸载
    还是双击 Sentinel.Setup.exe，点「卸载…」。
    默认保留配置和日志；勾上那个勾就一起删掉。
    保险箱（保险箱目录）不会被删 —— 它的权限是安装时特意设的，删了那台机器上就再也建不回来。
    真要连它也清掉，用命令行：uninstall.ps1 -InstallDir <安装位置> -Purge -PurgeVault

这个目录里的每个文件都要在一起
    安装程序靠相对位置找 install.ps1 / uninstall.ps1 / payload\，
    只把 exe 单独拷出来的话是装不了的。
'@ | Set-Content -Path (Join-Path $OutDir '安装说明.txt') -Encoding UTF8

# ---- 4. 收尾
if (-not $PSBoundParameters.ContainsKey('Payload')) {
    Remove-Item $Payload -Recurse -Force -ErrorAction SilentlyContinue
}

Remove-Item $setupOut -Recurse -Force -ErrorAction SilentlyContinue

$size = (Get-ChildItem $OutDir -Recurse -File | Measure-Object -Property Length -Sum).Sum

# 顺带打一个 zip：GitHub 的 Release 页传的是这个，不是源码仓库里的目录。
# （安装包是构建产物，不该进源码仓库 —— 源码那边 clone 下来自己 build 就有。）
$zip = "$OutDir.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $OutDir -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host ("装好了：{0}" -f $OutDir)
Write-Host ("一共 {0:N1} MB，{1} 个文件" -f ($size / 1MB), (Get-ChildItem $OutDir -Recurse -File).Count)
Write-Host ("发 GitHub Release 用的压缩包：{0}（{1:N1} MB）" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host "双击 Sentinel.Setup.exe 就能装。"
