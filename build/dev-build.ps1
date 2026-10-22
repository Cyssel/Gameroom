# Local development build for Gameroom.
# Uses a portable .NET SDK together with the .NET Framework MSBuild from VS Build Tools,
# so no full Visual Studio installation is needed.
#
#   .\dev-build.ps1                     builds the runnable app into build\<Configuration>
#   .\dev-build.ps1 -Project <csproj>   builds a single project (relative to source\) into its own bin folder

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$Project,

    [string]$OutputDir,

    [string]$DotnetRoot = "D:\dotnet",

    [string]$NugetCache = "D:\nuget-cache",

    [string]$MSBuildPath = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",

    [switch]$Restore
)

$ErrorActionPreference = "Stop"

$sdkDir = Get-ChildItem (Join-Path $DotnetRoot "sdk") -Directory | Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
if (!$sdkDir)
{
    throw "No .NET SDK found in $DotnetRoot"
}

$env:DOTNET_ROOT = $DotnetRoot
$env:PATH = "$DotnetRoot;$env:PATH"
$env:NUGET_PACKAGES = $NugetCache
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_HOST_PATH = Join-Path $DotnetRoot "dotnet.exe"
$env:MSBuildSDKsPath = Join-Path $sdkDir.FullName "Sdks"
$env:MSBuildEnableWorkloadResolver = "false"

$sourceDir = (Resolve-Path (Join-Path $PSScriptRoot "..\source")).Path
if ($Restore)
{
    dotnet restore (Join-Path $sourceDir "Playnite.slnx")
}

# Build Tools without the ".NET SDK" component can't expand netstandard facades for net462 projects
# (XAML compiler then fails with MC1000), so we do it with our own targets file.
$facadesDir = Join-Path $sdkDir.FullName "Microsoft\Microsoft.NET.Build.Extensions"
$facadesTargets = Join-Path $PSScriptRoot "NetStandardFacades.targets"

$commonArgs = @(
    "/p:Configuration=$Configuration",
    "/p:Platform=x86",
    "/p:SolutionDir=$sourceDir\",
    "/p:CustomAfterMicrosoftCommonTargets=$facadesTargets",
    "/p:GameroomFacadesDir=$facadesDir",
    "/m", "/v:minimal", "/nologo", "/clp:ErrorsOnly"
)

if ($Project)
{
    & $MSBuildPath (Join-Path $sourceDir $Project) @commonArgs
}
else
{
    if (!$OutputDir)
    {
        $OutputDir = Join-Path $PSScriptRoot $Configuration
    }

    & $MSBuildPath (Join-Path $PSScriptRoot "dev-build.xml") "/p:OutputPath=$OutputDir\" @commonArgs
    if ($LASTEXITCODE -eq 0)
    {
        Write-Host "Gameroom built to $OutputDir"
    }
}

exit $LASTEXITCODE
