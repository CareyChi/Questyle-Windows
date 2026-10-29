param(
    [Parameter(Mandatory=$true)][ValidateSet('development','stable')][string]$Channel,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Commit
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Commit -cnotmatch '^[0-9a-f]{7}$') { throw 'Invalid release identity.' }
$repository = $env:GITHUB_REPOSITORY
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$asset = Join-Path $projectRoot 'dist/questyle-windows-x64.zip'
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
$development = $Channel -eq 'development'
# A development tag targets the next patch so same-day builds sort above the base stable version.
$baseVersion = [version]$Version
$tag = if ($development) { "v$($baseVersion.Major).$($baseVersion.Minor).$($baseVersion.Build + 1)+$Commit" } else { "v$Version" }
$title = if ($development) { "Dev build $Commit" } else { "Questyle Windows v$Version" }
# Never attach new binaries to a tag that points at a different source revision.
$existingTag = & git ls-remote --tags origin "refs/tags/$tag"
if ($LASTEXITCODE) { throw 'Unable to inspect the release tag.' }
if ($existingTag) { throw ('Release tag already exists; use a new build or version: ' + $tag) }
$notes = @"
$title

App-Version: $Version

Windows x64，要求 Windows 10 2004（19041）或更新版本。完整解压后运行 Questyle.exe。USB 控制需要系统已有 UsbDk 驱动，控制期间会短暂中断音频。

本构建已通过 Release 编译、无 USB 协议模拟、无窗口 UI、原生 PEQ 离线检查及发布程序无窗口启动检查。CI 不进行 USB 实机验证或安装 APO。

Source: $env:GITHUB_SHA
Build: https://github.com/$repository/actions/runs/$env:GITHUB_RUN_ID
SHA-256 (questyle-windows-x64.zip): $hash
"@
$notesFile = Join-Path $env:RUNNER_TEMP 'questyle-release-notes.md'
[IO.File]::WriteAllText($notesFile, $notes, [Text.UTF8Encoding]::new($false))
$arguments = @('release','create',$tag,'--repo',$repository,'--target',$env:GITHUB_SHA,'--title',$title,'--notes-file',$notesFile,'--draft')
if ($development) { $arguments += @('--prerelease','--latest=false') }
else { $arguments += '--latest' }
& gh @arguments
if ($LASTEXITCODE) { throw 'Creating the draft release failed.' }
& gh release upload $tag $asset --repo $repository
if ($LASTEXITCODE) { throw 'Uploading the release asset failed; the release remains a draft.' }
# A draft has no Git tag yet, so it is found in the release list rather than by tag.
$draftPages = & gh api "repos/$repository/releases?per_page=100" --paginate --slurp | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Reading the uploaded release failed.' }
$drafts = @(foreach ($page in $draftPages) { foreach ($release in $page) { if ($release.draft -and $release.tag_name -eq $tag) { $release } } })
if ($drafts.Count -ne 1) { throw ('Expected exactly one draft release for ' + $tag) }
$assetInfo = $drafts[0]
$uploaded = @($assetInfo.assets | Where-Object name -eq 'questyle-windows-x64.zip')
if ($uploaded.Count -ne 1 -or $uploaded[0].state -ne 'uploaded' -or $uploaded[0].size -ne (Get-Item -LiteralPath $asset).Length -or $uploaded[0].digest -ne "sha256:$hash") { throw 'Release asset integrity verification failed.' }
if ($development) { & gh release edit $tag --repo $repository --draft=false --latest=false }
else { & gh release edit $tag --repo $repository --draft=false --latest }
if ($LASTEXITCODE) { throw 'Publishing the release failed.' }

if ($development) {
    # Only after the new ZIP is verified and public, replace previous prereleases.
    # Keep stable releases and the migration entries needed by already-published 7/8-character clients.
    $pages = & gh api "repos/$repository/releases?per_page=100" --paginate --slurp | ConvertFrom-Json
    if ($LASTEXITCODE) { throw 'Listing older prereleases failed.' }
    foreach ($page in $pages) {
        foreach ($release in $page) {
            if ($release.prerelease -and !$release.draft -and $release.tag_name -ne $tag -and $release.body -notmatch '(?m)^Update-Bridge: true\r?$') {
                $deleteArguments = @('release','delete',$release.tag_name,'--repo',$repository,'--yes')
                # Covers versioned development tags and the earlier commit/numbered tags.
                if ($release.tag_name -match '^(?:v[0-9]+\.[0-9]+\.[0-9]+\+[0-9a-f]{7}|dev-build-[0-9a-f]+)$') { $deleteArguments += '--cleanup-tag' }
                & gh @deleteArguments
                if ($LASTEXITCODE) { throw ('Removing an older prerelease failed: ' + $release.tag_name) }
            }
        }
    }
}
Write-Output "Published: https://github.com/$repository/releases/tag/$tag"
