<#
.SYNOPSIS
  Release 构建并打包成可直接解压到游戏根目录的 zip。

.DESCRIPTION
  产物：artifacts\ScriptEngine.IL2CPP-v<版本>.zip，内容为
    BepInEx/plugins/ScriptEngine.IL2CPP/ScriptEngine.IL2CPP.dll (+ .pdb)
    BepInEx/scripts/（空目录占位）
    README.md、README.zh-CN.md、CHANGELOG.md、LICENSE
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'Version.props'))).Project.PropertyGroup.Version

& dotnet build (Join-Path $root 'src\ScriptEngine.IL2CPP') -c Release -p:DeployToGame=false
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$bin = Join-Path $root 'src\ScriptEngine.IL2CPP\bin\Release'
$stage = Join-Path $root "artifacts\stage\ScriptEngine.IL2CPP-v$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$pluginDir = Join-Path $stage 'BepInEx\plugins\ScriptEngine.IL2CPP'
New-Item -ItemType Directory -Force $pluginDir | Out-Null
New-Item -ItemType Directory -Force (Join-Path $stage 'BepInEx\scripts') | Out-Null
Copy-Item (Join-Path $bin 'ScriptEngine.IL2CPP.dll') $pluginDir
Copy-Item (Join-Path $bin 'ScriptEngine.IL2CPP.pdb') $pluginDir
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'README.zh-CN.md') $stage
Copy-Item (Join-Path $root 'CHANGELOG.md') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage
Set-Content (Join-Path $stage 'BepInEx\scripts\README.txt') 'Put BasePlugin DLLs here to hot-reload them. See README.md.'

$zip = Join-Path $root "artifacts\ScriptEngine.IL2CPP-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "packed -> $zip"
