param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$projectDirectory=$PSScriptRoot
Push-Location $projectDirectory
try {
    $localSdk=Join-Path $projectDirectory 'tools/dotnet/dotnet.exe'
    $sdk=if(Test-Path -LiteralPath $localSdk){$localSdk}else{(Get-Command dotnet -ErrorAction Stop).Source}
    if(!$SkipTests) {
        & $sdk run --project tests/Questyle.Verification -c Release
        if($LASTEXITCODE){throw 'USB protocol verification failed.'}
        $previousDataDirectory=$env:QUESTYLE_DATA_DIR
        $env:QUESTYLE_DATA_DIR=Join-Path $projectDirectory 'analysis/ui-verification'
        try {
            & $sdk run --project tests/Questyle.UiVerification -c Release
            if($LASTEXITCODE){throw 'USB UI verification failed.'}
        } finally {
            if($null -eq $previousDataDirectory){Remove-Item Env:QUESTYLE_DATA_DIR -ErrorAction SilentlyContinue}
            else{$env:QUESTYLE_DATA_DIR=$previousDataDirectory}
        }
    }
    $publishDirectory=Join-Path $projectDirectory 'dist/Questyle-Windows-x64'
    & $sdk publish src/Questyle.Desktop -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
    if($LASTEXITCODE){throw 'Publish failed.'}
    Copy-Item -LiteralPath (Join-Path $projectDirectory 'README.md') -Destination (Join-Path $publishDirectory 'README.md')
    Copy-Item -LiteralPath (Join-Path $projectDirectory 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.md')
    $publishedLicenses=Join-Path $publishDirectory 'LICENSES'
    New-Item -ItemType Directory -Path $publishedLicenses -Force | Out-Null
    foreach($licenseFile in (Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'LICENSES') -File)) {
        Copy-Item -LiteralPath $licenseFile.FullName -Destination (Join-Path $publishedLicenses $licenseFile.Name) -Force
    }
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath (Join-Path $projectDirectory 'dist/Questyle-Windows-x64.zip') -Force
    Write-Output "Ready: $publishDirectory/Questyle.exe"
} finally {Pop-Location}
