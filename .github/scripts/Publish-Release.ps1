param(
    [Parameter(Mandatory=$true)][ValidateSet('development','stable')][string]$Channel,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][long]$BuildNumber
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $BuildNumber -lt 1) { throw 'Invalid release identity.' }
$repository = $env:GITHUB_REPOSITORY
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$asset = Join-Path $projectRoot 'dist/questyle-windows-x64.zip'
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
$development = $Channel -eq 'development'
$tag = if ($development) { "dev-build-$BuildNumber" } else { "v$Version" }
$title = if ($development) { "Dev build $BuildNumber" } else { "Questyle Windows v$Version" }
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
$assetInfo = & gh api "repos/$repository/releases/tags/$tag" | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Reading the uploaded release failed.' }
$uploaded = @($assetInfo.assets | Where-Object name -eq 'questyle-windows-x64.zip')
if ($uploaded.Count -ne 1 -or $uploaded[0].state -ne 'uploaded' -or $uploaded[0].size -ne (Get-Item -LiteralPath $asset).Length -or $uploaded[0].digest -ne "sha256:$hash") { throw 'Release asset integrity verification failed.' }
if ($development) { & gh release edit $tag --repo $repository --draft=false --latest=false }
else { & gh release edit $tag --repo $repository --draft=false --latest }
if ($LASTEXITCODE) { throw 'Publishing the release failed.' }

if ($development) {
    # Only after the new ZIP is verified and public, replace all previous prereleases.
    # Stable releases and their tags are never removed.
    $pages = & gh api "repos/$repository/releases?per_page=100" --paginate --slurp | ConvertFrom-Json
    if ($LASTEXITCODE) { throw 'Listing older prereleases failed.' }
    foreach ($page in $pages) {
        foreach ($release in $page) {
            if ($release.prerelease -and !$release.draft -and $release.tag_name -ne $tag) {
                $deleteArguments = @('release','delete',$release.tag_name,'--repo',$repository,'--yes')
                if ($release.tag_name -match '^dev-build-[1-9][0-9]*$') { $deleteArguments += '--cleanup-tag' }
                & gh @deleteArguments
                if ($LASTEXITCODE) { throw ('Removing an older prerelease failed: ' + $release.tag_name) }
            }
        }
    }
}
Write-Output "Published: https://github.com/$repository/releases/tag/$tag"
