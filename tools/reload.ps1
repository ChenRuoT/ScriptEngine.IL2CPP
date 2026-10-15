<#
.SYNOPSIS
  通过命令通道让运行中的游戏重载（或卸载）脚本，并打印结果 JSON。

.DESCRIPTION
  往 <游戏>\BepInEx\ScriptEngine.IL2CPP\commands\ 放一个 <id>.<命令> 文件，等引擎写出 <id>.result.json。
  退出码：0 = 成功，1 = 引擎报告失败，2 = 超时（游戏没开 / 引擎没装 / 命令通道关了）。

.EXAMPLE
  .\reload.ps1                 # 强制重载全部脚本
  .\reload.ps1 -Command status # 只看当前状态
  .\reload.ps1 -Command unload # 卸载全部脚本
#>
param(
    [ValidateSet('reload', 'unload', 'status')][string]$Command = 'reload',
    [string]$GameDir = 'D:\EFTdev\Client.1.2.0.0.47965',
    [int]$TimeoutSec = 60
)

$ErrorActionPreference = 'Stop'

$dir = Join-Path $GameDir 'BepInEx\ScriptEngine.IL2CPP\commands'
New-Item -ItemType Directory -Force $dir | Out-Null

$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
$resultPath = Join-Path $dir "$id.result.json"
Set-Content -Path (Join-Path $dir "$id.$Command") -Value $Command -NoNewline

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
while ([DateTime]::UtcNow -lt $deadline) {
    if (Test-Path $resultPath) {
        $json = Get-Content $resultPath -Raw -Encoding UTF8
        Remove-Item $resultPath -ErrorAction SilentlyContinue
        $json
        if (($json | ConvertFrom-Json).ok) { exit 0 } else { exit 1 }
    }
    Start-Sleep -Milliseconds 200
}

Write-Error "超时：$TimeoutSec 秒内没有收到 $id 的结果（游戏在运行吗？引擎装在 BepInEx\plugins 里了吗？）"
exit 2
