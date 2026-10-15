<#
.SYNOPSIS
  只结束 start-game.ps1 启动的那个游戏进程（%TEMP%\se-game.pid）。
#>
$pidFile = Join-Path $env:TEMP 'se-game.pid'
if (-not (Test-Path $pidFile)) { Write-Host '没有记录的进程号：不是用 start-game.ps1 启动的，不动它'; exit 0 }

$gamePid = [int](Get-Content $pidFile)
$p = Get-Process -Id $gamePid -ErrorAction SilentlyContinue
if ($p -and $p.ProcessName -eq 'EscapeFromTarkov') {
    Stop-Process -Id $gamePid -Force
    Write-Host "stopped pid=$gamePid"
} else {
    Write-Host "pid=$gamePid 已经不在了"
}
Remove-Item $pidFile -ErrorAction SilentlyContinue
