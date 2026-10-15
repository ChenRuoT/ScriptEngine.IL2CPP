<#
.SYNOPSIS
  启动游戏（不跑 DebugHarness），把进程号写到 %TEMP%\se-game.pid 并打印。

.DESCRIPTION
  启动参数取自 AyaHitmarker 的 run_game.ps1：token 来自 SPT Launcher 最近一次启动写下的 Launcher.log。
  - 不需要 SPT 服务端就能验证「插件有没有被加载」（会有一堆 127.0.0.1:6969 连接被拒，无关）。
  - 游戏已经在运行时直接报错退出：脚本不会去动正在玩的那一局。
  - 想关掉自己起的这个进程：.\stop-game.ps1（只杀 %TEMP%\se-game.pid 里记的那个）。
#>
param(
    [string]$GameDir = 'D:\EFTdev\Client.1.2.0.0.47965'
)

$ErrorActionPreference = 'Stop'

if (Get-Process EscapeFromTarkov -ErrorAction SilentlyContinue) {
    throw '游戏已经在运行：先退出它（脚本不会去动正在玩的那一局）'
}

$launcherLog = Join-Path $GameDir 'SPT_Runtime\user\logs\Launcher.log'
$line = Select-String -Path $launcherLog -Pattern 'args: .*-token=([0-9a-fA-F]+)' | Select-Object -Last 1
if (-not $line) { throw "在 $launcherLog 里找不到 -token=…（先用 SPT Launcher 启动过一次游戏）" }
$token = $line.Matches[0].Groups[1].Value

# 注意：-config 里的 JSON 用单引号，双引号会被 Windows 命令行吃掉
$gameArgs = "-force-gfx-jobs native -token=$token -config={'BackendUrl':'https://127.0.0.1:6969','Version':'live','MatchingVersion':'live'}"
$p = Start-Process -FilePath (Join-Path $GameDir 'EscapeFromTarkov.exe') -WorkingDirectory $GameDir -ArgumentList $gameArgs -PassThru
Set-Content -Path (Join-Path $env:TEMP 'se-game.pid') -Value $p.Id -NoNewline
Write-Host "launched pid=$($p.Id)"
