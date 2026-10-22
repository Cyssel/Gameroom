# Builds and runs Gameroom unit tests (Tests\Gameroom.Tests).

param(
    [string]$DotnetRoot = "D:\dotnet"
)

$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "dev-build.ps1") -Project "Tests\Gameroom.Tests\Gameroom.Tests.csproj" -DotnetRoot $DotnetRoot
if ($LASTEXITCODE -ne 0)
{
    exit $LASTEXITCODE
}

$testDll = Join-Path $PSScriptRoot "..\source\Tests\Gameroom.Tests\bin\x86\Debug\net462\win-x86\Gameroom.Tests.dll"
& (Join-Path $DotnetRoot "dotnet.exe") vstest (Resolve-Path $testDll).Path "/Platform:x86"
exit $LASTEXITCODE
