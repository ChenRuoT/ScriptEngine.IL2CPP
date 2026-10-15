<#
.SYNOPSIS
  把打包好的 zip + SHA256 作为 GitHub Release 发布（先建 draft，附件传完再公开）。

.DESCRIPTION
  版本号唯一来源是 Version.props；Release 正文由 tools/release/notes.ps1 从 CHANGELOG.md 的同名小节抽取。
  同版本已有正式 Release 时直接跳过，绝不覆盖已发布的版本。
  已有的同名 tag 必须指向参数给的 commit，否则拒绝发布（避免二进制与 tag 对不上）。

  由 CI 用 GitHub 自带的 GITHUB_TOKEN（contents: write）调用；本地手跑需要 gh 已登录且对目标仓库有写权限。

.PARAMETER Commit
  要发布（或建 tag 指向）的 commit SHA。

.PARAMETER RefName
  触发本次运行的 ref 名。以 v 开头（即 tag 触发）时，必须与 v<版本号> 一致。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Commit,
    [string]$RefName = '',
    [string]$OutputDirectory = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Version.props'))).Project.PropertyGroup.Version
$tag = "v$version"
if ($RefName.StartsWith('v') -and $RefName -ne $tag) { throw "tag $RefName 与 Version.props 的 $tag 不一致" }

$repo = $env:GITHUB_REPOSITORY
if ([string]::IsNullOrWhiteSpace($repo)) {
    $repo = (gh repo view --json nameWithOwner --jq .nameWithOwner)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repo)) { throw '拿不到仓库名：设 GITHUB_REPOSITORY，或在仓库目录里跑' }
}

$releasesJson = ((gh api --paginate --slurp "repos/$repo/releases?per_page=100") -join "`n").Trim()
if ($LASTEXITCODE -ne 0) { throw '列不出已有的 release' }
$existing = @()
if ($releasesJson) {
    $existing = @($releasesJson | ConvertFrom-Json | ForEach-Object { $_ } | Where-Object tag_name -eq $tag)
}
if ($existing.Count -gt 0 -and -not $existing[0].draft) { Write-Host "$tag 已经发布过，跳过。" ; exit 0 }

$tagRefsJson = ((gh api "repos/$repo/git/matching-refs/tags/$tag") -join "`n").Trim()
if ($LASTEXITCODE -ne 0) { throw '查不到 release tag 的状态' }
$tagRef = @()
if ($tagRefsJson) {
    $tagRef = @(($tagRefsJson | ConvertFrom-Json) | Where-Object ref -eq "refs/tags/$tag")
}
if ($tagRef.Count -gt 0) {
    $object = $tagRef[0].object
    while ($object.type -eq 'tag') {
        $object = (gh api "repos/$repo/git/tags/$($object.sha)" | ConvertFrom-Json).object
        if ($LASTEXITCODE -ne 0) { throw '解不开 annotated tag' }
    }
    if ($object.type -ne 'commit' -or $object.sha -ne $Commit) { throw "已存在的 $tag 指向别的 commit；拒绝把不匹配的二进制发上去。" }
}

$out = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$zip = Join-Path $out "ScriptEngine.IL2CPP-v$version.zip"
$sha = "$zip.sha256"
foreach ($file in @($zip, $sha)) { if (-not (Test-Path -LiteralPath $file)) { throw "缺少打包产物：$file（先跑 tools/release/package.ps1）" } }

$notes = Join-Path $out "notes-$version.md"
& (Join-Path $PSScriptRoot 'notes.ps1') -Version $version -OutFile $notes -Repository $repo

if ($existing.Count -eq 0) {
    $arguments = @('release', 'create', $tag, '--repo', $repo, '--target', $Commit,
                   '--title', "ScriptEngine.IL2CPP $tag", '--notes-file', $notes, '--draft')
    if ($version.Contains('-')) { $arguments += '--prerelease' }
    & gh @arguments
    if ($LASTEXITCODE -ne 0) { throw '创建 draft release 失败' }
}

gh release upload $tag $zip $sha --repo $repo --clobber
if ($LASTEXITCODE -ne 0) { throw '上传 release 附件失败' }

gh release edit $tag --repo $repo --draft=false
if ($LASTEXITCODE -ne 0) { throw '公开发布失败' }

Write-Host "published $tag -> $repo"
