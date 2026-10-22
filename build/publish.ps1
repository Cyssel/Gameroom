# Builds Gameroom, creates its installer and publishes it as a GitHub release, where the app looks for updates.
#
#   .\publish.ps1 -Notes "- Novedad 1`n- Novedad 2"      build, create the installer and publish the release
#   .\publish.ps1 -SkipUpload                            only create the installer (build\installer\out\<version>)
#   .\publish.ps1 -Prerelease                            publish for the beta channel only
#
# The version comes from GameroomVersion in source\Directory.Build.props, raise it before publishing.
# Publishing uses GitHub CLI (https://cli.github.com, run "gh auth login" once). The repository is the one
# Gameroom checks for updates: ReleaseChannel.GitHubRepository in source\Playnite\Gameroom\Updates\GameroomReleases.cs.

param(
    [string]$Notes = "",

    [string]$NotesFile,

    [switch]$Prerelease,

    # Use the existing build\Release output.
    [switch]$SkipBuild,

    # Only create the installer.
    [switch]$SkipUpload,

    # "owner/repo", defaults to the repository the app checks for updates.
    [string]$Repository,

    [string]$InnoCompiler = (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
[xml]$props = Get-Content (Join-Path $root "source\Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.GameroomVersion } | Select-Object -First 1).GameroomVersion
if ($version -notmatch '^\d+\.\d+\.\d+$')
{
    throw "Invalid GameroomVersion '$version' in source\Directory.Build.props"
}

if (!$Repository)
{
    $releasesCode = Get-Content (Join-Path $root "source\Playnite\Gameroom\Updates\GameroomReleases.cs") -Raw
    $Repository = [regex]::Match($releasesCode, 'GitHubRepository\s*=\s*"([^"]+)"').Groups[1].Value
}

if ($NotesFile)
{
    $Notes = Get-Content $NotesFile -Raw -Encoding UTF8
}

Write-Host "Gameroom $version" -ForegroundColor Cyan

# -------------------------------------------
#  Build
# -------------------------------------------
$releaseDir = Join-Path $PSScriptRoot "Release"
if (!$SkipBuild)
{
    if (Test-Path $releaseDir)
    {
        Remove-Item $releaseDir -Recurse -Force
    }

    & (Join-Path $PSScriptRoot "dev-build.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0)
    {
        throw "Build failed."
    }
}

# -------------------------------------------
#  Installer
# -------------------------------------------
if (!(Test-Path $InnoCompiler))
{
    throw "Inno Setup compiler not found: $InnoCompiler (https://jrsoftware.org/isdl.php)"
}

$outputDir = Join-Path $PSScriptRoot "installer\out\$version"
$iss = Join-Path $PSScriptRoot "installer\Gameroom.iss"
Write-Host "Creating installer..." -ForegroundColor Cyan
& $InnoCompiler /Q "/DAppVersion=$version" "/DSourceDir=$releaseDir" "/DOutputDir=$outputDir" $iss
if ($LASTEXITCODE -ne 0)
{
    throw "Inno Setup failed."
}

$setup = Join-Path $outputDir "GameroomSetup-$version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ("Installer: {0}  {1:N1} MB" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
Write-Host "SHA-256:   $hash"

if ($SkipUpload)
{
    return
}

# -------------------------------------------
#  GitHub release
# -------------------------------------------
if (!(Get-Command gh -ErrorAction SilentlyContinue))
{
    throw "GitHub CLI (gh) not found. Install it from https://cli.github.com and run 'gh auth login', or upload $setup to a new release of $Repository by hand."
}

$tag = "v$version"
& gh release view $tag --repo $Repository *> $null
if ($LASTEXITCODE -eq 0)
{
    throw "Release $tag already exists in $Repository. Raise GameroomVersion in source\Directory.Build.props."
}

$notesPath = Join-Path ([IO.Path]::GetTempPath()) "gameroom-notes-$version.md"
[IO.File]::WriteAllText($notesPath, $Notes, [Text.UTF8Encoding]::new($false))
$arguments = @("release", "create", $tag, $setup, "--repo", $Repository, "--title", "Gameroom $version", "--notes-file", $notesPath)
if ($Prerelease)
{
    $arguments += "--prerelease"
}

& gh @arguments
if ($LASTEXITCODE -ne 0)
{
    throw "Publishing the release failed."
}

Remove-Item $notesPath -ErrorAction SilentlyContinue
Write-Host "Gameroom $version published: https://github.com/$Repository/releases/tag/$tag" -ForegroundColor Green
