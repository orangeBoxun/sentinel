#Requires -RunAsAdministrator
<#
    卸载 Sentinel：停止并删除服务，结束后台 Agent。
    默认保留配置和日志；加 -Purge 连 %ProgramData%\Sentinel 一起删掉。

    参数要和安装时一致：

        .\uninstall.ps1 -InstallDir 'E:\Sentinel' -Purge
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramData 'Sentinel'),
    [switch]$Purge,
    [switch]$PurgeVault,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# 和 install.ps1 一样写死，理由见那边：装成别的名字，看门狗会找不到服务并且封印保险箱。
$ServiceName = 'Sentinel'

function Assert-SafeDirectory {
    param([string]$Path, [string]$What)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$What 不能为空。" }

    $full = [System.IO.Path]::GetFullPath($Path)

    # -Purge 会对这个路径做递归删除，传空或传成盘根就是把系统交出去
    if ($full -eq [System.IO.Path]::GetPathRoot($full)) { throw "拒绝在驱动器根目录上操作：$full" }
    if ($full.TrimEnd('\').Length -lt 4) { throw "路径太短，像是误传：$full" }

    return $full
}

$InstallDir = Assert-SafeDirectory -Path $InstallDir -What '安装目录'
$marker = Join-Path $InstallDir '.sentinel-install'

Write-Host '[1/6] 封印保险箱'
# 直接写文件，而不是调用已安装的 exe：旧版本的 exe 不认识 --seal-vault，会把参数忽略掉、
# 直接当服务主机跑起来，把脚本挂死 —— 和下面 sc sdset 那里是同一个理由。
# 格式要和 Sentinel.Core 的 VaultGuard 对上（就是两个字段的 JSON）。
$vaultKeys = Join-Path $InstallDir 'vault\keys.json'
$seal = Join-Path $InstallDir '.vault-seal'
if (-not (Test-Path $vaultKeys)) {
    Write-Host '      （没有保险箱，无需封印）'
} elseif (Test-Path $seal) {
    Write-Host '      （此前已被封印，保留最初的原因）'
} else {
    $record = @{
        Reason = '保险箱：Sentinel 被卸载了'
        At     = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    } | ConvertTo-Json

    [System.IO.File]::WriteAllText($seal, $record, (New-Object System.Text.UTF8Encoding $false))

    # 两条一起做，才叫真的封印：
    #
    #   1. 把"密码那道门"从钥匙文件里删掉 —— 光写个标记不算，标记只是给本软件看的；
    #      钥匙文件里那道门还在的话，谁把 vault 目录拷走、再写个小程序读它，密码照样能开。
    #   2. 删掉"本机秘密"—— 那道门绑着它。这一步之后，**之前拷出去的 vault 副本也作废了**，
    #      因为解开它还需要一份已经不存在的秘密。
    #
    # 字段名要和 Sentinel.Core 的 VaultKeys 对上。
    try {
        $keys = [System.IO.File]::ReadAllText($vaultKeys) | ConvertFrom-Json
        $keys.WrappedByUser = $null
        $keys.UserSalt = $null
        $keys.State = 'SealedToGuardian'
        $keys.SealedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        $keys.SealedReason = '保险箱：Sentinel 被卸载了'

        $json = $keys | ConvertTo-Json -Depth 6
        [System.IO.File]::WriteAllText($vaultKeys, $json, (New-Object System.Text.UTF8Encoding $false))
        Write-Host '      已把密码那道门从钥匙文件里删掉'
    } catch {
        Write-Host "      （钥匙文件没能改写：$($_.Exception.Message)）"
    }

    $machineSecret = Join-Path $InstallDir '.vault-machine'
    if (Test-Path $machineSecret) {
        Remove-Item -Force $machineSecret
        Write-Host '      已销毁本机秘密：此前拷出去的 vault 副本也打不开了'
    }

    Write-Host '      已封印：你的密码不再能打开保险箱，只有监护码可以'
}

Write-Host '[2/6] 停止并删除服务、注销卸载项'
$service = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    $pathName = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).PathName
    if ($pathName -notlike '*Sentinel*' -and -not $Force) {
        throw "「$ServiceName」这个服务不像是 Sentinel 装的：`n    $pathName`n确认要删除请加 -Force。"
    }

    # 安装时把"停止"权限从管理员那里收走了，所以先改回默认，否则下面 Stop-Service 会被拒绝。
    # 用 sc sdset 而不是调用已安装的 exe：旧版本的 exe 不认识 --relax，会把参数忽略掉、
    # 直接当服务主机跑起来，把脚本挂死。
    sc.exe sdset $ServiceName 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' | Out-Null
    Write-Host '      已恢复服务权限（否则停不下来）'

    # 必须先关掉失败恢复，否则停下来的服务会被自动拉起
    sc.exe failure $ServiceName reset= 0 actions= '' | Out-Null
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
} else {
    Write-Host '      （没有这个服务）'
}

# 「应用和功能」里那一条。放在这里而不是最后：卸载条目留着的话，用户会以为没卸干净，
# 而后面任何一步出错都不该让它留在那儿。
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Sentinel'
if (Test-Path $uninstallKey) {
    Remove-Item $uninstallKey -Recurse -Force
    Write-Host '      已注销「应用和功能」里的条目'
} else {
    Write-Host '      （「应用和功能」里没有这一条）'
}

Write-Host '[3/6] 注销看门狗计划任务'
if (Get-ScheduledTask -TaskName 'SentinelWatchdog' -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName 'SentinelWatchdog' -Confirm:$false
    Write-Host '      已注销'
} else {
    Write-Host '      （没有这个任务）'
}

Write-Host '[4/6] 结束后台 Agent'
$agents = Get-Process Sentinel.Agent -ErrorAction SilentlyContinue
if ($agents) {
    $agents | Stop-Process -Force
    Write-Host "      已结束 $($agents.Count) 个"
} else {
    Write-Host '      （没有在跑）'
}
Start-Sleep -Seconds 1

Write-Host '[5/6] 清理数据'
if (-not $Purge) {
    Write-Host "      保留了 $InstallDir（配置和日志）。要一并删除请加 -Purge。"
} elseif (-not (Test-Path $InstallDir)) {
    Write-Host "      $InstallDir 不存在，无需删除。"
} elseif (-not (Test-Path $marker)) {
    # 没有安装标记就说明这目录不是 Sentinel 装的（或者被换过）——
    # 递归删除只对确认属于我们的目录做。
    throw "$InstallDir 里没有 Sentinel 的安装标记（.sentinel-install），拒绝递归删除。`n确认无误请手动处理：$InstallDir"
} else {
    # 保险箱就在这个目录里，递归删除会连它一起端掉 —— 而"删程序就丢文件"正是这套东西存在的
    # 全部意义。所以它需要自己的一次确认，不能借 -Purge 或 -Force 顺带过去：那两个是"我知道
    # 这是 Sentinel 的目录"，跟"我同意丢掉保险箱里的东西"不是一回事。
    $blobs = Join-Path $InstallDir 'vault\blobs'

    if (Test-Path $blobs) {
        $files = @(Get-ChildItem -Path $blobs -File -ErrorAction SilentlyContinue)

        if ($files.Count -gt 0 -and -not $PurgeVault) {
            $mb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
            throw @"
保险箱里还有 $($files.Count) 个文件（约 $mb MB），递归删除会连它们一起删掉。
删掉之后这些文件就找不回来了 —— 除非你导出过备份。

同意一并删除请加 -PurgeVault：

    .\uninstall.ps1 -InstallDir '$InstallDir' -Purge -PurgeVault

或者先打开 Sentinel 把里面的东西导出来。
"@
        }

        if ($files.Count -gt 0) {
            Write-Host "      ⚠ 正在删除保险箱里的 $($files.Count) 个文件"
        }
    }

    try {
        Remove-Item -Recurse -Force $InstallDir -ErrorAction Stop
        Write-Host "      已删除 $InstallDir"
    } catch {
        # 多半是因为 Uninstall.exe 正从这个目录里跑着 —— 运行中的 exe 锁住自己的文件，
        # 整个目录就删不掉。这是卸载程序的老问题，通用的解法是把自己交给一个等几秒的命令，
        # 让它在进程退出之后再收尾。
        #
        # 写成临时批处理而不是 `cmd /c "timeout ... & rmdir ..."`：后者要在已经引号包起来的
        # 参数里再嵌一层引号，cmd 的解析会错位。批处理里没有这个嵌套问题。
        $cleanup = Join-Path $env:TEMP "sentinel-cleanup-$([guid]::NewGuid().ToString('N').Substring(0,8)).cmd"

        @"
@echo off
timeout /t 3 /nobreak >nul
rmdir /s /q "$InstallDir"
del "%~f0"
"@ | Set-Content -Path $cleanup -Encoding ASCII

        Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cleanup -WindowStyle Hidden

        Write-Host '      剩下的部分几秒后自动删除（卸载程序自己还在那个目录里跑，删不掉自己）'
    }
}

Write-Host '[6/6] 完成'
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Get-Service $ServiceName | Format-Table -AutoSize
} else {
    Write-Host '      服务已移除。'
}
