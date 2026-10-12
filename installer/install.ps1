#Requires -RunAsAdministrator
<#
    安装 Sentinel。这一步只做三件事：校验、拷贝、注册服务 —— 不执行 dotnet publish。
    构建（会跑 MSBuild / NuGet 构建脚本 / 仓库里的自定义 task）必须先在普通权限下完成。

    注意两个参数的分工：publish 写到 staging（-OutDir），install 从 staging 读（-Source）
    并写到最终位置（-InstallDir）。

        .\publish.ps1                                          # 不提权
        .\install.ps1 -Source .\publish                        # 提权，默认装到 %ProgramData%\Sentinel

    装到别处（比如避免占用 C 盘）—— 两个脚本各有各的参数，不要互相传错：

        .\publish.ps1 -OutDir E:\Sentinel-stage
        .\install.ps1 -Source E:\Sentinel-stage -InstallDir E:\Sentinel

    程序、配置、日志都在安装目录下：

        <InstallDir>\bin\            程序和依赖（普通用户只读，防止被替换成 SYSTEM 代码）
        <InstallDir>\config.json     配置（普通用户只读）
        <InstallDir>\logs\           审计日志（普通用户可写 —— 见文件末尾的说明）
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$Source,
    [switch]$Force,
    [switch]$NoWatchdog,

    # 图形安装程序自己的路径。给了它，就把自己复制成安装目录里的 Uninstall.exe、
    # 并把 uninstall.ps1 一起放过去 —— 之后「应用和功能」里的卸载项指向的就是那个文件。
    # 不给也没关系（命令行安装没有这一步），只是那样就没法从系统里卸载。
    [string]$SetupExe
)

$ErrorActionPreference = 'Stop'

# 服务名故意不可配置。程序里到处写死了 Sentinel（AddWindowsService、Watchdog.ServiceName、
# --harden / --relax），装成别的名字会得到一个"看门狗找不到服务"的安装 —— 而看门狗找不到服务
# 会直接封印保险箱。一个能造成数据丢失的参数，不如没有这个参数。
$ServiceName = 'Sentinel'

# 在正文里取默认值：$PSScriptRoot 在 param() 的默认值表达式里可能还没赋值
if (-not $InstallDir) { $InstallDir = Join-Path $env:ProgramData 'Sentinel' }
if (-not $Source) { $Source = Join-Path $PSScriptRoot 'publish' }

function Assert-SafeDirectory {
    param([string]$Path, [string]$What)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$What 不能为空。" }

    $full = [System.IO.Path]::GetFullPath($Path)

    # 递归删除和整目录改权限都发生在下面，路径传错就是把系统盘交出去
    if ($full -eq [System.IO.Path]::GetPathRoot($full)) { throw "拒绝在驱动器根目录上操作：$full" }
    if ($full.TrimEnd('\').Length -lt 4) { throw "路径太短，像是误传：$full" }

    return $full
}

$InstallDir = Assert-SafeDirectory -Path $InstallDir -What '安装目录'
$Source = Assert-SafeDirectory -Path $Source -What '发布目录'

$bin = Join-Path $InstallDir 'bin'
$config = Join-Path $InstallDir 'config.json'
$logs = Join-Path $InstallDir 'logs'
$exe = Join-Path $bin 'Sentinel.Service.exe'
$marker = Join-Path $InstallDir '.sentinel-install'

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Start-Transcript -Path (Join-Path $InstallDir 'install.log') -Append | Out-Null   # 输出留档，不用靠 -NoExit 挂着管理员窗口

$maintenance = Join-Path $InstallDir '.maintenance'

try {
    # 升级时服务会被故意停掉几秒，看门狗会把它读成"防护被拆"并且封印保险箱。
    # 放在 try 里、finally 里删 —— 中途失败也不会让防护永久挂起。
    New-Item -ItemType File -Force -Path $maintenance | Out-Null

    Write-Host "[1/8] 校验发布产物"
    if (-not (Test-Path $Source)) { throw "找不到发布目录 $Source，先跑 .\publish.ps1" }

    $manifestPath = Join-Path $Source 'publish-manifest.json'
    if (Test-Path $manifestPath) {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        foreach ($file in $manifest.Files) {
            $path = Join-Path $Source $file.Name
            if (-not (Test-Path $path)) { throw "发布目录里缺少 $($file.Name)" }

            $actual = (Get-FileHash -Path $path -Algorithm SHA256).Hash
            if ($actual -ne $file.Sha256) {
                throw "$($file.Name) 的哈希与发布清单不符 —— 发布之后被改动过，拒绝安装。"
            }
        }
        Write-Host "      清单核对通过（由 $($manifest.Machine) 于 $($manifest.BuiltAt) 发布）"
        Write-Host "      但请注意：这份清单和二进制在同一个目录里，而该目录对普通用户可写 ——"
        Write-Host "      能替换二进制的人同样能替换清单。这一步只防意外损坏，不防篡改。"
        Write-Host "      真要对本机攻击者设防，需要 Authenticode 代码签名并在安装时验签，"
        Write-Host "      或把 staging 目录放到仅管理员可写的位置。"
    } else {
        Write-Host "      （没有发布清单，无法核对哈希 —— 建议用 publish.ps1 重新发布）"
    }

    Write-Host '[2/8] 结束 Agent，停止并删除同名服务'

    # 必须在动 bin 之前。Agent 是从 bin 里加载 FlaUI.Core.dll 这些依赖的，只要它还在跑，
    # 那些 dll 就锁着，下一步的 Remove-Item 会失败 —— 而失败发生在服务已经被删掉之后，
    # 留下的是一台没有服务的机器。新服务起来之后会自己把 Agent 重新送进会话，杀掉它是安全的。
    $agents = Get-Process Sentinel.Agent -ErrorAction SilentlyContinue
    if ($agents) {
        $agents | Stop-Process -Force -ErrorAction SilentlyContinue
        Write-Host "      已结束 $($agents.Count) 个 Agent（它们锁着 bin 里的 dll）"
        Start-Sleep -Seconds 2
    } else {
        Write-Host '      （没有 Agent 在跑）'
    }

    $existing = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        $pathName = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).PathName
        if ($pathName -notlike '*Sentinel*' -and -not $Force) {
            throw "已有一个名为「$ServiceName」的服务，但它不是 Sentinel 装的：`n    $pathName`n确认要覆盖请加 -Force。"
        }

        # 上一次安装把"停止"权限从管理员那里收走了，先改回默认，否则下面停不掉。
        # 这里用 sc sdset 而不是调用已安装的 exe：旧版本的 exe 不认识 --relax，
        # 会把参数忽略掉、直接当服务主机跑起来，把脚本挂死。
        sc.exe sdset $ServiceName 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' | Out-Null

        # 先解除失败恢复，否则停下来的服务会被自动拉起，导致删除失败
        sc.exe failure $ServiceName reset= 0 actions= '' | Out-Null
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }

    Write-Host "[3/8] 拷贝程序到 $bin"
    if (Test-Path $bin) {
        # 只在能确认这是上一次装进去的程序时才删 —— 否则 -InstallDir 打错就会清空你某个 bin 目录
        if (-not (Test-Path (Join-Path $bin 'Sentinel.Service.exe')) -and -not $Force) {
            throw "$bin 已存在，但里面没有 Sentinel.Service.exe，不像是 Sentinel 装出来的目录。`n拒绝清空它。确认要覆盖请加 -Force。"
        }

        Remove-Item -Recurse -Force $bin
    }

    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $bin -Recurse -Force

    if (-not (Test-Path $exe)) { throw "拷贝后找不到 $exe" }
    if (-not (Test-Path (Join-Path $bin 'Sentinel.Agent.exe'))) { throw "拷贝后找不到 Sentinel.Agent.exe" }

    Write-Host "[4/8] 写入配置和标记文件"
    New-Item -ItemType Directory -Force -Path $logs | Out-Null

    if (-not (Test-Path $config)) {
        $seed = [ordered]@{
            Version = 1
            Targets = @()
            Policies = @()
            Schedules = @()
            Protection = [ordered]@{ Enabled = $false; Mode = 'None'; SecretHash = $null; DelaySeconds = 0 }
            Logging = [ordered]@{ LogDirectory = $logs; LogBodyText = $false; LogUrlHash = $true }
            Ui = [ordered]@{ Locale = 'zh-CN'; DryRun = $true }
        }
        $seed | ConvertTo-Json -Depth 8 | Set-Content -Path $config -Encoding UTF8
        Write-Host '      （新建了一份空白配置，默认安全模式）'
    } else {
        Write-Host '      （已存在配置，保留不动）'
    }

    # 卸载脚本靠它判断"这确实是 Sentinel 装的目录"，才敢递归删除
    @{
        InstalledAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        ServiceName = $ServiceName
        Source      = $Source
    } | ConvertTo-Json | Set-Content -Path $marker -Encoding UTF8

    # 「应用和功能」里的卸载项。没有这一步，用户想卸载就只能翻出当初那个安装包 ——
    # 而那个包早就不知道放哪了。
    #
    # 卸载程序就是图形安装程序本身，复制成 <InstallDir>\Uninstall.exe。同一个二进制，
    # 靠自己的文件名决定走安装还是卸载 —— 省掉第二份窗口、第二份提权、第二份跑脚本的代码。
    if ($SetupExe) {
        if (-not (Test-Path $SetupExe)) { throw "找不到安装程序自己：$SetupExe" }

        $uninstallExe = Join-Path $InstallDir 'Uninstall.exe'
        Copy-Item $SetupExe $uninstallExe -Force

        # 卸载脚本也要在那边 —— 卸载程序靠相对位置找它。
        $uninstallScript = Join-Path (Split-Path $SetupExe -Parent) 'uninstall.ps1'
        if (-not (Test-Path $uninstallScript)) { throw "找不到卸载脚本：$uninstallScript" }
        Copy-Item $uninstallScript (Join-Path $InstallDir 'uninstall.ps1') -Force

        $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Sentinel'
        New-Item -Path $key -Force | Out-Null

        Set-ItemProperty -Path $key -Name 'DisplayName'     -Value 'Sentinel'
        Set-ItemProperty -Path $key -Name 'DisplayVersion'  -Value '1.0'
        Set-ItemProperty -Path $key -Name 'Publisher'       -Value 'Sentinel'
        Set-ItemProperty -Path $key -Name 'InstallLocation' -Value $InstallDir
        Set-ItemProperty -Path $key -Name 'DisplayIcon'     -Value $exe
        Set-ItemProperty -Path $key -Name 'UninstallString' -Value "`"$uninstallExe`""
        Set-ItemProperty -Path $key -Name 'NoModify'        -Value 1 -Type DWord
        Set-ItemProperty -Path $key -Name 'NoRepair'        -Value 1 -Type DWord

        # 「应用和功能」会显示这个大小；算的是 bin 目录，不含日志和保险箱。
        $kb = [int](((Get-ChildItem $bin -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1KB)
        Set-ItemProperty -Path $key -Name 'EstimatedSize' -Value $kb -Type DWord

        Write-Host "      已放好卸载程序，并登记到「应用和功能」"
    } else {
        Write-Host '      （没给 -SetupExe，跳过卸载程序与卸载项 —— 只装了命令行那部分）'
    }

    Write-Host "[5/8] 注册服务（开机自启），并告诉它配置在哪里"
    # 显式传配置路径：否则服务会去 %ProgramData% 找，装了自定义目录就对不上
    $binaryPath = "`"$exe`" --config `"$config`""
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -StartupType Automatic `
        -DisplayName 'Sentinel' -Description 'Sentinel 内容拦截：常驻并把 Agent 送进用户会话' | Out-Null

    Write-Host "[6/8] 失败自动恢复（崩溃 5 秒后重启，无限次）"
    sc.exe failure $ServiceName reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null

    Write-Host '      收紧服务权限：管理员不能"停止"它'
    # 这一条让"点一下就停掉"彻底不可行 —— 服务控制台、sc stop、任务管理器的服务页全部拒绝访问。
    # 剩下的只有硬杀进程，而那属于崩溃，失败恢复 5 秒拉回来；看门狗每分钟还会把权限重新收紧一次。
    # 想走正规流程停它，得先用 sc sdset 把权限改回去 —— 两条刻意命令，不是一次点击。
    & $exe --harden
    if ($LASTEXITCODE -ne 0) { throw '收紧服务权限失败' }

    Write-Host '[7/8] 收紧权限'
    # 安装目录整体锁定。这一步不能省：服务以 SYSTEM 运行，若 bin 目录对普通用户可写，
    # 任何人（包括用户自己）把 Sentinel.Service.exe 换掉，配合失败自动恢复，下一次重启
    # 就会以 SYSTEM 权限执行替换后的代码。
    icacls $InstallDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)RX' | Out-Null

    # 配置：普通用户只读 —— 这是延迟解锁能成为"锁"而不是"摩擦"的前提
    icacls $config /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' 'Users:R' | Out-Null

    # 日志：Agent 以用户身份运行，审计日志由它写，所以这个子目录必须可写。
    # 代价是这里的记录**改得动** —— 它是本地的便利副本，不是权威的那份（见下面 $audit）。
    icacls $logs /grant:r 'Users:(OI)(CI)M' | Out-Null

    # 权威的那份审计：由服务来写（Agent 把条目经管道交给它），普通用户**只读**。
    # 这就是它和上面 logs\ 那份的区别：那份 Agent 以用户身份写，所以用户改得动；这份改不动。
    # 为什么在意：这套东西的全部意义是"约束自己"，而凌晨一点会去改的恰恰是这份记录。
    $audit = Join-Path $InstallDir 'audit'
    New-Item -ItemType Directory -Force -Path $audit | Out-Null
    icacls $audit /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)RX' | Out-Null

    # 保险箱的密文目录。托盘程序要往里面放文件，所以普通用户必须能写 —— 能写不等于能读：
    # 里面每个文件都是加密且带认证的。权威的封印状态放上一层（.vault-seal），
    # 那个文件在安装目录的权限下，普通用户写不了，所以"拷回一份旧备份"没有用。
    $vault = Join-Path $InstallDir 'vault'
    New-Item -ItemType Directory -Force -Path $vault | Out-Null
    icacls $vault /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)M' | Out-Null

    Write-Host '      已锁定安装目录、配置、日志和保险箱的权限'

    if ($NoWatchdog) {
        Write-Host '[8/8] 跳过看门狗（-NoWatchdog）'
    } else {
        Write-Host '[8/8] 注册看门狗计划任务（每分钟检查一次）'
        # 服务的失败恢复只管"崩溃"：任务管理器结束任务、sc stop、服务控制台停止，都算"正常停止"，
        # 停了就再也不起来了。看门狗补的就是这一块，它连被正常停止也能拉起来。
        #
        # 交给程序自己注册，而不是在这里拼 schtasks 命令行：/tr 的值本身是一条带引号的命令行，
        # 路径里有空格时手工转义很容易出错。这条路径服务自己也在用，只测一次就够。
        # --config 必须在这里传：不传的话 exe 会退回到 %ProgramData%\Sentinel\config.json，
        # 于是计划任务注册的是错的路径 —— 而看门狗每分钟都会拿这个路径去重写服务的 binPath，
        # 结果是服务被指到一个不存在的配置上，起来之后读的是空白配置，监控静默失效。
        & $exe --ensure-watchdog --config $config
        if ($LASTEXITCODE -ne 0) { throw '注册看门狗失败' }
    }

    Start-Service $ServiceName
    Start-Sleep -Seconds 3

    Write-Host ''
    Get-Service $ServiceName | Format-Table -AutoSize
    Write-Host "安装目录 : $InstallDir"
    Write-Host "配置文件 : $config"
    Write-Host "日志目录 : $logs"
    Write-Host "安装日志 : $(Join-Path $InstallDir 'install.log')"
    Write-Host ''
    Write-Host '服务只负责常驻、权限与自启；监控本身跑在用户会话里的 Sentinel.Agent（托盘图标）。'
    Write-Host '托盘图标应当自动出现 —— 没出现说明服务没能把 Agent 送进会话，看安装日志。'
    Write-Host ''
    Write-Host '卸载：'
    Write-Host "    .\uninstall.ps1 -InstallDir '$InstallDir'            # 保留配置和日志"
    Write-Host "    .\uninstall.ps1 -InstallDir '$InstallDir' -Purge     # 连数据一起删"
}
finally {
    Remove-Item -Force -ErrorAction SilentlyContinue $maintenance | Out-Null
    Stop-Transcript | Out-Null
}
