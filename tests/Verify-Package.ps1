param([Parameter(Mandatory=$true)][string]$PublishDirectory)
$ErrorActionPreference='Stop'
$publishRoot=(Resolve-Path -LiteralPath $PublishDirectory).ProviderPath
$projectRoot=Split-Path -Parent $PSScriptRoot
$verificationDirectory=Join-Path $projectRoot 'reverse/analysis/package-verification'
New-Item -ItemType Directory -Path $verificationDirectory -Force | Out-Null
$reportPath=Join-Path $verificationDirectory 'startup.json'
if(Test-Path -LiteralPath $reportPath){Remove-Item -LiteralPath $reportPath}
if(@(Get-ChildItem -LiteralPath $publishRoot -File -Filter '*.dll').Count){throw 'DLL files remain in the release root.'}
foreach($unrelated in @('LICENSES','THIRD-PARTY-NOTICES.md','README.md')) {
    if(Test-Path -LiteralPath (Join-Path $publishRoot $unrelated)){throw ('Unrelated release file remains: ' + $unrelated)}
}
foreach($folder in (@(Get-Item -LiteralPath $publishRoot) + @(Get-ChildItem -LiteralPath $publishRoot -Directory -Recurse))) {
    if($folder.Name -cne $folder.Name.ToLowerInvariant()){throw ('Package folder is not lowercase: ' + $folder.FullName)}
}
foreach($language in @('zh-CN','zh-TW','en-US')) {
    $catalog=Get-Content -LiteralPath (Join-Path $publishRoot ('language/' + $language + '.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
    if(@($catalog.PSObject.Properties).Count -lt 50){throw ('Missing application translations: ' + $language)}
}
foreach($unused in @('Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll','assets/logo.png','assets/qms12.png')) {
    if(Test-Path -LiteralPath (Join-Path (Join-Path $publishRoot 'runtime') $unused)){throw ('Unused dependency or asset remains in Runtime: ' + $unused)}
}
Add-Type -AssemblyName System.Drawing
$executableIcon=[Drawing.Icon]::ExtractAssociatedIcon((Join-Path $publishRoot 'Questyle.exe'))
# The ICO frames are PNG; Windows PowerShell's System.Drawing.Icon.ToBitmap misdecodes them, so read the 32 px frame directly.
$iconBytes=[IO.File]::ReadAllBytes((Join-Path $publishRoot 'runtime/assets/Questyle.ico'))
$frame=@(0..([BitConverter]::ToUInt16($iconBytes,4)-1) | ForEach-Object {6+16*$_} | Where-Object {$iconBytes[$_] -eq 32 -and $iconBytes[$_+1] -eq 32})
if($frame.Count -ne 1){throw 'The supplied icon has no single 32 px frame.'}
$frameOffset=[BitConverter]::ToUInt32($iconBytes,$frame[0]+12); $frameLength=[BitConverter]::ToUInt32($iconBytes,$frame[0]+8)
$sourceIcon=New-Object IO.MemoryStream(,$iconBytes[$frameOffset..($frameOffset+$frameLength-1)])
$executableBitmap=$executableIcon.ToBitmap(); $sourceBitmap=[Drawing.Bitmap]::new($sourceIcon)
if($sourceBitmap.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid){throw 'The supplied 32 px icon frame is not PNG.'}
try {
    if($executableBitmap.Size -ne $sourceBitmap.Size){throw 'Executable icon size does not match the supplied icon.'}
    for($y=0;$y -lt $sourceBitmap.Height;$y++) {for($x=0;$x -lt $sourceBitmap.Width;$x++) {
        if($executableBitmap.GetPixel($x,$y).ToArgb() -ne $sourceBitmap.GetPixel($x,$y).ToArgb()){throw 'The root executable did not embed the supplied icon.'}
    }}
} finally {$executableBitmap.Dispose();$sourceBitmap.Dispose();$executableIcon.Dispose();$sourceIcon.Dispose()}
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $publishRoot 'Questyle.exe'))
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardError=$true
$start.Arguments='--verify-package "' + $reportPath + '"'
$start.EnvironmentVariables['QUESTYLE_DATA_DIR']=$verificationDirectory
$start.EnvironmentVariables['DOTNET_DISABLE_GUI_ERRORS']='1'
$start.EnvironmentVariables['DOTNET_ROOT']=Join-Path $verificationDirectory 'no-global-runtime'
$start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP']='0'
$process=[Diagnostics.Process]::Start($start)
try {
    $stderr=$process.StandardError.ReadToEndAsync()
    if(!$process.WaitForExit(10000)){$process.Kill(); throw 'Package startup verification timed out.'}
    if($process.ExitCode){throw ('Package startup failed: ' + $stderr.GetAwaiter().GetResult())}
    $report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $runtimeDirectory=Join-Path $publishRoot 'runtime'
    if($report.ApplicationDirectory -ne $publishRoot -or $report.RuntimeDirectory.TrimEnd('\','/') -ne $runtimeDirectory){throw 'The launcher did not resolve the packaged application paths.'}
    foreach($assembly in @($report.WpfAssembly,$report.FormsAssembly,$report.NativeRuntime)) {
        if((Split-Path -Parent $assembly) -ne $runtimeDirectory){throw ('Runtime was loaded outside the Runtime folder: ' + $assembly)}
    }
    if(!$report.HelperFound -or $report.WindowVisible){throw 'Package resources or headless startup verification failed.'}
    if((Split-Path -Parent $report.PeqNativeModule) -ne $runtimeDirectory -or [Math]::Abs($report.PeqResponseDb) -gt 0.001){throw 'Packaged independent PEQ native component did not load or calculate its response correctly.'}
    if(!$report.PerMonitorDpiV2){throw 'The executable did not enable PerMonitorV2 DPI awareness.'}
    if($report.ResizeEnabled -or !$report.AutoHeightEnabled -or $report.WindowWidth -le 0){throw 'The packaged window did not use measured dimensions and non-resizable chrome.'}
    if($report.DataDirectory -ne $verificationDirectory -or $report.TemporaryDirectory.TrimEnd('\','/') -ne (Join-Path $verificationDirectory 'temp')){throw 'Runtime data or temp files were not redirected to the selected directory.'}
    if($report.DefaultDataDirectory -ne (Join-Path $publishRoot 'data') -or !$report.FontFamily.StartsWith('Microsoft YaHei, SimHei, ')){throw 'Default installation data or font configuration is incorrect.'}
    $cultures=@(Get-ChildItem -LiteralPath (Join-Path $publishRoot 'language') -Directory)
    if(@(Compare-Object @('zh-hans','zh-hant') @($cultures.Name)).Count){throw 'Package contains missing or unused language resource folders.'}
    if($report.LanguageAssemblies.Count -ne $cultures.Count -or $cultures.Count -eq 0){throw 'Language resource verification is incomplete.'}
    foreach($culture in $report.LanguageAssemblies) {
        $expected=Join-Path (Join-Path $publishRoot 'language') $culture.Culture.ToLowerInvariant()
        if((Split-Path -Parent $culture.Wpf) -ne $expected -or (Split-Path -Parent $culture.Forms) -ne $expected){throw ('Satellite assembly loaded from the wrong directory: ' + $culture.Culture)}
        if(Test-Path -LiteralPath (Join-Path $runtimeDirectory $culture.Culture)){throw ('Culture directory was not moved from Runtime: ' + $culture.Culture)}
    }
    Write-Output ('PASS packaged runtime, data/temp paths and ' + $cultures.Count + ' language folders; no window was shown.')
} finally {$process.Dispose()}
