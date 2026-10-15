<#
.SYNOPSIS
  从 CHANGELOG.md 里抽出某个版本的条目，作为 GitHub Release 的正文。

.PARAMETER Version
  版本号，必须与 Version.props / CHANGELOG.md 里的一致（例如 0.1.0）。

.PARAMETER OutFile
  写到哪里（UTF-8 无 BOM —— 带 BOM 会让 GitHub 把正文开头显示成乱码）。

.PARAMETER Repository
  owner/repo，用于把正文里的相对链接改成绝对地址。默认取 $env:GITHUB_REPOSITORY；传空字符串就不改链接。

.EXAMPLE
  pwsh -File tools/release/notes.ps1 -Version 0.1.0 -OutFile notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutFile,
    [string]$Repository = $env:GITHUB_REPOSITORY
)

$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$lines = @(Get-Content -LiteralPath (Join-Path $root 'CHANGELOG.md'))

$heading = "## [$Version]"
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith($heading)) { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md 里找不到 `"$heading`" 这一节；发版前必须补上同名版本说明。" }

$body = [System.Collections.Generic.List[string]]::new()
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith('## ')) { break }
    # 尾部的链接定义行（[0.1.0]: https://...）不属于正文
    if ($lines[$i] -match '^\[[^\]]+\]:\s') { continue }
    $body.Add($lines[$i])
}

$text = (($body -join "`n").Trim() + "`n")
if ([string]::IsNullOrWhiteSpace($text)) { throw "CHANGELOG.md 里 $heading 是空的。" }

if (-not [string]::IsNullOrWhiteSpace($Repository)) {
    $base = "https://github.com/$Repository/blob/main"
    # 用 .Replace（字面替换），避免把 # 之类当成正则
    $text = $text.Replace('](README.md#', "]($base/README.md#")
    $text = $text.Replace('](CHANGELOG.md', "]($base/CHANGELOG.md")
}

$OutFile = [IO.Path]::GetFullPath($OutFile)
$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[IO.File]::WriteAllText($OutFile, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "release notes -> $OutFile ($($text.Length) chars)"
