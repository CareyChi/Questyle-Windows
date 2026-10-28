param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$projectDirectory=$PSScriptRoot
Push-Location $projectDirectory
try {
    $localSdk=Join-Path $projectDirectory 'reverse/tools/dotnet/dotnet.exe'
    $sdk=if(Test-Path -LiteralPath $localSdk){$localSdk}else{(Get-Command dotnet -ErrorAction Stop).Source}
    if(!$SkipTests) {
        & $sdk run --project tests/Questyle.Verification -c Release
        if($LASTEXITCODE){throw 'USB protocol verification failed.'}
        $previousDataDirectory=$env:QUESTYLE_DATA_DIR
        $env:QUESTYLE_DATA_DIR=Join-Path $projectDirectory 'reverse/analysis/ui-verification'
        try {
            & $sdk run --project tests/Questyle.UiVerification -c Release
            if($LASTEXITCODE){throw 'USB UI verification failed.'}
        } finally {
            if($null -eq $previousDataDirectory){Remove-Item Env:QUESTYLE_DATA_DIR -ErrorAction SilentlyContinue}
            else{$env:QUESTYLE_DATA_DIR=$previousDataDirectory}
        }
    }
    $distDirectory=[IO.Path]::GetFullPath((Join-Path $projectDirectory 'dist'))
    $publishedExecutable=[IO.Path]::GetFullPath((Join-Path $distDirectory 'questyle-windows-x64/Questyle.exe'))
    Get-Process -Name Questyle -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $publishedExecutable } | ForEach-Object { Stop-Process -Id $_.Id -Force; $_.WaitForExit() }
    $pairingFile=Join-Path $distDirectory 'questyle-windows-x64/paired-headphones.json'
    $savedPairings=if(Test-Path -LiteralPath $pairingFile){[IO.File]::ReadAllBytes($pairingFile)}else{$null}
    $previousDataDirectory=Join-Path $distDirectory 'questyle-windows-x64/data'
    $savedData=@(if(Test-Path -LiteralPath $previousDataDirectory) {
        foreach($file in (Get-ChildItem -LiteralPath $previousDataDirectory -File -Recurse)) {
            $relativePath=$file.FullName.Substring($previousDataDirectory.Length + 1)
            if(!$relativePath.StartsWith('temp\',[StringComparison]::OrdinalIgnoreCase)) {
                [pscustomobject]@{Path=$relativePath; Bytes=[IO.File]::ReadAllBytes($file.FullName)}
            }
        }
    })
    $previousEqDirectory=Join-Path $distDirectory 'questyle-windows-x64/eq-config'
    $savedEqConfigs=@(if(Test-Path -LiteralPath $previousEqDirectory) {
        foreach($file in (Get-ChildItem -LiteralPath $previousEqDirectory -File)) {
            [pscustomobject]@{Name=$file.Name; Bytes=[IO.File]::ReadAllBytes($file.FullName)}
        }
    })
    $publishDirectory=Join-Path $distDirectory 'questyle-windows-x64'
    # Local data is held only in memory while dist is rebuilt; write it back even when a later step fails.
    try {
        if(Test-Path -LiteralPath $distDirectory) {
            $resolvedDist=(Resolve-Path -LiteralPath $distDirectory).ProviderPath
            if($resolvedDist -ne $distDirectory -or (Split-Path -Parent $resolvedDist) -ne [IO.Path]::GetFullPath($projectDirectory)) {throw 'Publish cleanup target is outside the project dist directory.'}
            if((Get-Item -LiteralPath $resolvedDist).Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Publish cleanup does not support a linked dist directory.'}
            Remove-Item -LiteralPath $resolvedDist -Recurse -Force
        }
        $runtimeDirectory=Join-Path $publishDirectory 'runtime'
        & $sdk publish src/Questyle.Desktop -c Release -r win-x64 --self-contained true -p:PortableLayout=true -p:DebugType=None -p:DebugSymbols=false -o $runtimeDirectory
        if($LASTEXITCODE){throw 'Publish failed.'}
        $languageDirectory=Join-Path $publishDirectory 'language'
        New-Item -ItemType Directory -Path $languageDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $runtimeDirectory 'language') -File | Move-Item -Destination $languageDirectory
        $runtimeLanguages=[IO.Path]::GetFullPath((Join-Path $runtimeDirectory 'language'))
        if((Split-Path -Parent $runtimeLanguages) -ne [IO.Path]::GetFullPath($runtimeDirectory)){throw 'Invalid application language directory.'}
        Remove-Item -LiteralPath $runtimeLanguages
        foreach($culture in (Get-ChildItem -LiteralPath $runtimeDirectory -Directory | Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.resources.dll').Count -gt 0 })) {
            $source=(Resolve-Path -LiteralPath $culture.FullName).ProviderPath
            $destination=[IO.Path]::GetFullPath((Join-Path $languageDirectory $culture.Name.ToLowerInvariant()))
            if((Split-Path -Parent $source) -ne [IO.Path]::GetFullPath($runtimeDirectory) -or (Split-Path -Parent $destination) -ne [IO.Path]::GetFullPath($languageDirectory)){throw 'Language move target is outside the release directories.'}
            Move-Item -LiteralPath $source -Destination $destination
        }
        & (Join-Path $projectDirectory 'tests/Verify-Package.ps1') -PublishDirectory $publishDirectory
        New-Item -ItemType Directory -Path (Join-Path $publishDirectory 'eq-config') -Force | Out-Null
        Compress-Archive -LiteralPath $publishDirectory -DestinationPath (Join-Path $projectDirectory 'dist/questyle-windows-x64.zip') -Force
    } finally {
        if($null -ne $savedPairings) {
            New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
            [IO.File]::WriteAllBytes((Join-Path $publishDirectory 'paired-headphones.json'), [byte[]]$savedPairings)
        }
        foreach($file in $savedData) {
            $target=Join-Path (Join-Path $publishDirectory 'data') $file.Path
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            [IO.File]::WriteAllBytes($target,[byte[]]$file.Bytes)
        }
        foreach($file in $savedEqConfigs) {
            $target=Join-Path (Join-Path $publishDirectory 'eq-config') $file.Name
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            [IO.File]::WriteAllBytes($target,[byte[]]$file.Bytes)
        }
    }
    Write-Output "Ready: $publishDirectory/Questyle.exe"
} finally {Pop-Location}
