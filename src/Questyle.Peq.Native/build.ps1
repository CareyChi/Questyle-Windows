param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$compiler = Join-Path $root 'reverse/tools/llvm-mingw-20261020-ucrt-x86_64/bin/x86_64-w64-mingw32-clang++.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'PEQ native build requires llvm-mingw 20261020 under reverse/tools. See docs/PEQ.md.' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
& $compiler -std=c++20 -O2 -Wall -Wextra -Werror -shared -static -static-libstdc++ -static-libgcc (Join-Path $PSScriptRoot 'PeqApo.cpp') (Join-Path $PSScriptRoot 'PeqApo.def') -lole32 -luuid -ladvapi32 -o (Join-Path $OutputDirectory 'Questyle.Peq.Native.dll')
if ($LASTEXITCODE -ne 0) { throw 'PEQ native build failed.' }
