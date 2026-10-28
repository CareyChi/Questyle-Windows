param([Parameter(Mandatory=$true)][string]$DataDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$compiler = Join-Path $root 'reverse/tools/llvm-mingw-20261020-ucrt-x86_64/bin/x86_64-w64-mingw32-clang++.exe'
$headers = Join-Path $root 'reverse/analysis/equalizer-apo'
$output = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force $output | Out-Null
$exe = Join-Path $output 'verify.exe'
& $compiler -std=c++20 -O2 -Wall -Wextra -Werror -static -municode -I $headers (Join-Path $PSScriptRoot 'Verify.cpp') -lole32 -luuid -ladvapi32 -o $exe
if ($LASTEXITCODE -ne 0) { throw 'Native verification build failed.' }
& $exe ([IO.Path]::GetFullPath($DataDirectory))
if ($LASTEXITCODE -ne 0) { throw 'Native verification failed.' }
