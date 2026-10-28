$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tools = Join-Path $projectRoot 'reverse/tools'
$headers = Join-Path $projectRoot 'reverse/analysis/equalizer-apo'
New-Item -ItemType Directory -Force -Path $tools,$headers | Out-Null
$archive = Join-Path $tools 'llvm-mingw-20261020-ucrt-x86_64.zip'
Invoke-WebRequest -Uri 'https://github.com/mstorsjo/llvm-mingw/releases/download/20261020/llvm-mingw-20261020-ucrt-x86_64.zip' -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'fd5462b7f5d93b2ff981fcd3f9c7e427e47f3203c64ea91c27bcc444eebd74cd') { throw 'llvm-mingw archive SHA-256 mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath $tools
$headerHashes = @{
    'audioenginebaseapo.h' = '1859398a91a4498f23ad1e06755f9520138308268f4ac4927a3526c3de1d6382'
    'audiomediatype.h' = '20d550d6fe21fed26bee3e1313791b859984cef0bd7d1a143a1acf92fa9481cc'
    'AudioAPOTypes.h' = 'a93f40f7e672974fd868598c3ab6c19b88bba7a08dd14b3f3af8ae13046865e3'
}
foreach ($name in $headerHashes.Keys) {
    $path = Join-Path $headers $name
    Invoke-WebRequest -Uri ('https://raw.githubusercontent.com/microsoft/win32metadata/6d6617a8eee57d7c4f6c55d8c23e35064ea69b9e/generation/WinSDK/RecompiledIdlHeaders/um/' + $name) -OutFile $path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $headerHashes[$name]) { throw ('Windows SDK header SHA-256 mismatch: ' + $name) }
}
