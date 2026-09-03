<#
.SYNOPSIS
    Fetch a ready-to-run CanReplayPlayer.exe from the repository's GitHub releases.

.DESCRIPTION
    A clone already contains bin\CanReplayPlayer.exe, but that is the
    framework-dependent build: it needs the .NET 10 Desktop Runtime installed.
    This script fetches a build from the repository's GitHub Releases instead --
    by default the self-contained one, which runs on any Windows 10/11 x64
    machine with no prerequisites at all.

    The large builds are not committed because a self-contained WPF build is
    about 59 MB and cannot be trimmed, and git cannot delta-compress a
    compressed single-file executable, so every version would be stored in full
    and stay in history permanently. Release assets have no size or bandwidth
    limit, are not part of a clone, and do not count towards repository size.

    The download is written over bin\CanReplayPlayer.exe, so git will report
    that file as modified afterwards. That is expected.

.PARAMETER Variant
    'self-contained' (default) runs on any Windows 10/11 x64 machine with no
    prerequisites. 'netdesktop' is under a megabyte but needs the .NET 10 Desktop
    Runtime installed.

.PARAMETER Destination
    Where to put the executable. Defaults to a 'bin' folder beside this repository.

.EXAMPLE
    pwsh -File scripts/get_player.ps1
    pwsh -File scripts/get_player.ps1 -Variant netdesktop
#>
[CmdletBinding()]
param(
    [ValidateSet('self-contained', 'netdesktop')]
    [string]$Variant = 'self-contained',
    [string]$Destination = '',
    [string]$Tag = 'latest'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $RepoRoot 'bin' }

# Read the repository from the git remote rather than hard-coding it, so a fork
# fetches its own builds.
$slug = $null
try {
    $remote = (& git -C $RepoRoot remote get-url origin 2>$null)
    if ($remote -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)') {
        $slug = "$($Matches.owner)/$($Matches.repo)"
    }
} catch {
    # No git, or no origin: fall through to the error below.
}

if (-not $slug) {
    throw "Could not work out the GitHub repository from 'git remote get-url origin'. " +
          "Run this from a clone, or download the EXE from the project's Releases page."
}

Write-Host "Repository : $slug" -ForegroundColor Cyan
Write-Host "Variant    : $Variant"

$api = if ($Tag -eq 'latest') {
    "https://api.github.com/repos/$slug/releases/latest"
} else {
    "https://api.github.com/repos/$slug/releases/tags/$Tag"
}

try {
    $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'CanReplayPlayer' }
} catch {
    throw "Could not read releases from $api. " +
          "If the repository is private, run 'gh auth login' and use " +
          "'gh release download' instead. ($($_.Exception.Message))"
}

# The self-contained asset is the one WITHOUT the -netdesktop suffix, so match
# on the suffix rather than on a substring that appears in both.
$asset = $release.assets | Where-Object {
    $_.name -like 'CanReplayPlayer-*-win-x64*.exe' -and
    (($Variant -eq 'netdesktop') -eq ($_.name -like '*-netdesktop.exe'))
} | Select-Object -First 1

if (-not $asset) {
    $available = ($release.assets | ForEach-Object { $_.name }) -join ', '
    throw "Release '$($release.tag_name)' has no $Variant player asset. Found: $available"
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$target = Join-Path $Destination 'CanReplayPlayer.exe'

Write-Host "Release    : $($release.tag_name)"
Write-Host ("Downloading: {0}  ({1:N1} MB)" -f $asset.name, ($asset.size / 1MB))

Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $target -UseBasicParsing

$actual = (Get-Item $target).Length
if ($actual -ne $asset.size) {
    Remove-Item $target -Force
    throw "Download is $actual bytes but $($asset.size) were expected; the file was incomplete."
}

Write-Host ""
Write-Host "Ready: $target" -ForegroundColor Green
Write-Host ""
Write-Host "Next:"
if ($Variant -eq 'netdesktop') {
    Write-Host "  1. Install the .NET 10 Desktop Runtime (x64) if it is not already present:"
    Write-Host "     https://dotnet.microsoft.com/download/dotnet/10.0"
    Write-Host "  2. Install the PEAK driver: https://www.peak-system.com/quick/DrvSetup"
} else {
    Write-Host "  1. Install the PEAK driver: https://www.peak-system.com/quick/DrvSetup"
}
Write-Host "  - Start the player and use 'Get Scenarios...' to fetch driving data."
