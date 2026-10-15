<#
.SYNOPSIS
  Release 构建并打包成可直接解压到游戏根目录的 zip（附 SHA256 文件）。

.DESCRIPTION
  产物（默认 artifacts/release/，该目录不入库）：
    ScriptEngine.IL2CPP-v<版本>.zip         内容：BepInEx/plugins/ScriptEngine.IL2CPP/（dll + pdb）、
                                            BepInEx/scripts/README.txt、README.md、README.zh-CN.md、
                                            CHANGELOG.md、LICENSE
    ScriptEngine.IL2CPP-v<版本>.zip.sha256  "<小写 hash>  <文件名>"

  只使用新编译出来的 bin\<Configuration>\ 产物，**不**从游戏安装目录捞文件。
  DLL 的 ProductVersion 必须与 Version.props 一致，防止把陈旧产物打进去。

  构建需要 GameDir 指向游戏根目录：环境变量 SE_GAME_DIR、Directory.Build.local.props 或 -p:GameDir=...

.EXAMPLE
  $env:SE_GAME_DIR = '<游戏根目录>'
  pwsh -File tools/release/package.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Version.props'))).Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Version.props 里没有版本号' }

& dotnet build (Join-Path $root 'src/ScriptEngine.IL2CPP') -c $Configuration -p:DeployToGame=false
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$bin = Join-Path $root "src/ScriptEngine.IL2CPP/bin/$Configuration"
$dll = Join-Path $bin 'ScriptEngine.IL2CPP.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "缺少 $dll" }
$actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion.Split('+')[0]
if ($actual -ne $version) { throw "DLL 版本 $actual 与 Version.props 的 $version 不一致（先重新构建）" }

$out = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Path $out -Force | Out-Null

# 唯一暂存目录：不会把上一次的陈旧文件带进 zip，也不会删调用方给的输出目录
$stage = Join-Path $out ('stage-' + [Guid]::NewGuid().ToString('N'))
try {
    $pluginDir = Join-Path $stage 'BepInEx/plugins/ScriptEngine.IL2CPP'
    New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stage 'BepInEx/scripts') -Force | Out-Null

    Copy-Item -LiteralPath $dll -Destination $pluginDir
    $pdb = Join-Path $bin 'ScriptEngine.IL2CPP.pdb'
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $pluginDir }
    foreach ($file in @('README.md', 'README.zh-CN.md', 'CHANGELOG.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage
    }
    Set-Content -LiteralPath (Join-Path $stage 'BepInEx/scripts/README.txt') `
        -Value 'Put BasePlugin DLLs here to hot-reload them. See README.md.' -Encoding utf8NoBOM

    $zip = Join-Path $out "ScriptEngine.IL2CPP-v$version.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding utf8NoBOM

    Write-Host "packed -> $zip"
    Write-Host "sha256 -> $hash"
} finally {
    # 递归删除前确认目标确实是本函数建在输出目录里的暂存目录
    $resolved = [IO.Path]::GetFullPath($stage)
    if ($resolved.StartsWith($out.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -match '^stage-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
