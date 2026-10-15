<#
.SYNOPSIS
  只结束 start-server.ps1 启动的那个 SPT 服务端进程（%TEMP%\se-server.pid）。
#>
$pidFile = Join-Path $env:TEMP 'se-server.pid'
if (-not (Test-Path $pidFile)) { Write-Host '没有记录的进程号：不是用 start-server.ps1 启动的，不动它'; exit 0 }

$serverPid = [int](Get-Content $pidFile)
$p = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
if ($p -and $p.ProcessName -like 'SPT.Server*') {
    Stop-Process -Id $serverPid -Force
    Write-Host "stopped server pid=$serverPid"
} else {
    Write-Host "pid=$serverPid 已经不在了"
}
Remove-Item $pidFile -ErrorAction SilentlyContinue
