<#
.SYNOPSIS
  启动 SPT 服务端（SPT_Runtime\SPT.Server.exe，监听 6969），等到端口可连，把进程号写到 %TEMP%\se-server.pid。

.DESCRIPTION
  只在需要进主菜单做真机验证时用（不需要服务端也能验证「插件有没有被加载」）。
  - 6969 已经有人在监听时什么都不做（那是你自己起的服务端，脚本不会碰它，stop-server.ps1 也不会杀它）。
  - 想关掉自己起的这个：.\stop-server.ps1（只杀 %TEMP%\se-server.pid 里记的那个）。
#>
param(
    [string]$GameDir = 'D:\EFTdev\Client.1.2.0.0.47965',
    [int]$TimeoutSec = 90
)

$ErrorActionPreference = 'Stop'

if (Get-NetTCPConnection -LocalPort 6969 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host '6969 已经在监听：沿用现有的服务端，不再启动'
    exit 0
}

$runtime = Join-Path $GameDir 'SPT_Runtime'
$p = Start-Process -FilePath (Join-Path $runtime 'SPT.Server.exe') -WorkingDirectory $runtime -WindowStyle Hidden -PassThru
Set-Content -Path (Join-Path $env:TEMP 'se-server.pid') -Value $p.Id -NoNewline
Write-Host "server pid=$($p.Id)，等待 6969 …"

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
while ([DateTime]::UtcNow -lt $deadline) {
    if ($p.HasExited) { throw "服务端提前退出了（exit code $($p.ExitCode)）" }
    if (Get-NetTCPConnection -LocalPort 6969 -State Listen -ErrorAction SilentlyContinue) {
        Write-Host '服务端已就绪'
        exit 0
    }
    Start-Sleep -Milliseconds 500
}

throw "$TimeoutSec 秒内 6969 没有开始监听"
