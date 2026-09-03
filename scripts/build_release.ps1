<#
.SYNOPSIS
    Build every release artifact for CAN Vehicle Replay (requirement 73).

.DESCRIPTION
    Runs the test suites, publishes a self-contained single-file EXE, assembles a
    portable folder and ZIP, builds the Setup.exe, and writes checksums.

    Output layout (requirement 73):
        release/
          portable/
            CanReplayPlayer.exe
            portable.txt
            Scenarios/
          CanReplayPlayer-portable-win-x64.zip
          installer/
            CanReplayPlayer-Setup-<version>-win-x64.exe
          checksums.txt

.PARAMETER Version
    Version stamped into the assemblies and the installer file name.

.PARAMETER ScenarioDir
    Scenario packages to ship. Defaults to ./Scenarios. Pass an empty string to
    build the application without any scenario data.

.PARAMETER SkipTests
    Skip the test suites. Intended for iterating on packaging only.

.PARAMETER SkipInstaller
    Skip the Setup.exe even if Inno Setup is installed.

.EXAMPLE
    pwsh -File scripts/build_release.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$ScenarioDir = "",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$SkipAndroid
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$WindowsRoot = Join-Path $RepoRoot "windows"
$ReleaseRoot = Join-Path $RepoRoot "release"
$PortableDir = Join-Path $ReleaseRoot "portable"
$InstallerDir = Join-Path $ReleaseRoot "installer"

if (-not $ScenarioDir) { $ScenarioDir = Join-Path $RepoRoot "Scenarios" }

function Write-Step($text) {
    Write-Host ""
    Write-Host "=== $text" -ForegroundColor Cyan
}

function Write-Note($text) {
    Write-Host "    $text" -ForegroundColor DarkGray
}

# --------------------------------------------------------------------------
# Locate a dotnet with the .NET 10 SDK.
#
# .NET 10 may be installed under the user profile rather than Program Files, and
# the muxer on PATH only sees SDKs under its own root -- so the right dotnet has
# to be found rather than assumed.
# --------------------------------------------------------------------------
function Resolve-Dotnet {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -and ($sdks | Where-Object { $_ -match '^10\.' })) {
            return $candidate
        }
    }

    throw "No .NET 10 SDK found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 " +
          "(checked: $($candidates -join ', '))."
}

$Dotnet = Resolve-Dotnet
$env:DOTNET_ROOT = Split-Path -Parent $Dotnet
Write-Step "Toolchain"
Write-Note "dotnet      : $Dotnet"
Write-Note "SDK         : $((& $Dotnet --version).Trim())"
Write-Note "version     : $Version"
Write-Note "scenarios   : $ScenarioDir"

# --------------------------------------------------------------------------
# Tests
# --------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step "Windows tests"
    & $Dotnet test (Join-Path $WindowsRoot "CanReplayPlayer.slnx") `
        -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Windows tests failed." }

    Write-Step "Scenario Builder tests"
    $python = (Get-Command python -ErrorAction SilentlyContinue)
    if ($python) {
        Push-Location (Join-Path $RepoRoot "scenario_builder")
        try {
            & python -m pytest -q
            if ($LASTEXITCODE -ne 0) { throw "Scenario Builder tests failed." }
        } finally { Pop-Location }
    } else {
        Write-Note "python not found; skipping the Scenario Builder tests."
    }

    if (-not $SkipAndroid) {
        Write-Step "Android decoder tests"
        $androidRoot = Join-Path $RepoRoot "android"
        $gradlew = Join-Path $androidRoot "gradlew.bat"
        if (Test-Path $gradlew) {
            Push-Location $androidRoot
            try {
                $env:CANREPLAY_SKIP_ANDROID_APP = "1"
                $gradleOutput = & $gradlew ":candecoder:test" "--rerun-tasks" --console=plain 2>&1
                if ($LASTEXITCODE -ne 0) {
                    $gradleOutput | Select-Object -Last 30 | ForEach-Object { Write-Note $_ }
                    throw "Android decoder tests failed."
                }
                $passed = @($gradleOutput | Select-String -Pattern " PASSED").Count
                Write-Note "$passed decoder test(s) passed."
            } finally {
                $env:CANREPLAY_SKIP_ANDROID_APP = $null
                Pop-Location
            }
        } else {
            Write-Note "android/gradlew.bat not found; skipping."
        }
    }
}

# --------------------------------------------------------------------------
# Publish
#
# Self-contained so the exhibition PC needs no .NET runtime, single-file so the
# portable build is one EXE plus a Scenarios folder (requirements 56-58).
# IncludeNativeLibrariesForSelfExtract is required because WPF's own native
# libraries cannot be loaded from inside the bundle.
# --------------------------------------------------------------------------
Write-Step "Publishing self-contained single-file EXE"
if (Test-Path $ReleaseRoot) { Remove-Item $ReleaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path $PortableDir -Force | Out-Null

& $Dotnet publish (Join-Path $WindowsRoot "src\CanReplayPlayer.Wpf\CanReplayPlayer.Wpf.csproj") `
    -c Release `
    -r win-x64 `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -o $PortableDir `
    --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $PortableDir "CanReplayPlayer.exe"
if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }
Write-Note ("self-contained EXE: {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))

# --------------------------------------------------------------------------
# A second, framework-dependent build.
#
# The self-contained EXE carries the whole .NET runtime and WPF, which is why
# it is ~60 MB; WPF cannot be trimmed, so that floor cannot be lowered. Dropping
# the runtime brings the same application under a megabyte, at the cost of
# requiring the .NET Desktop Runtime on the machine. Both are shipped so the
# choice is the operator's: no prerequisites, or a small download.
# --------------------------------------------------------------------------
Write-Step "Publishing framework-dependent EXE"
$FrameworkDir = Join-Path $ReleaseRoot "framework-dependent"
& $Dotnet publish (Join-Path $WindowsRoot "src\CanReplayPlayer.Wpf\CanReplayPlayer.Wpf.csproj") `
    -c Release `
    -r win-x64 `
    -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -o $FrameworkDir `
    --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "framework-dependent publish failed." }

$smallExe = Join-Path $FrameworkDir "CanReplayPlayer.exe"
Write-Note ("framework-dependent EXE: {0:N2} MB (needs the .NET 10 Desktop Runtime)" -f `
    ((Get-Item $smallExe).Length / 1MB))

# The portable build keeps its settings and logs beside the EXE so it can run
# from a USB stick without leaving anything in the user profile.
"This file makes CAN Vehicle Replay run in portable mode: settings and logs are" +
"`r`nstored beside the executable instead of in %APPDATA%. Delete it to use the" +
"`r`nnormal per-user location." | Set-Content (Join-Path $PortableDir "portable.txt")

# --------------------------------------------------------------------------
# Scenarios
# --------------------------------------------------------------------------
Write-Step "Staging scenarios"
$stagedScenarios = Join-Path $PortableDir "Scenarios"
if (Test-Path $ScenarioDir) {
    Copy-Item $ScenarioDir $stagedScenarios -Recurse -Force
    # Debug JSONL exports are for bring-up, not for the exhibition build; they
    # are several times the size of the binary timeline they duplicate.
    Get-ChildItem $stagedScenarios -Directory -Filter "debug" -Recurse |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force }

    $count = @(Get-ChildItem $stagedScenarios -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName "scenario.json") }).Count
    $measured = Get-ChildItem $stagedScenarios -Recurse -File |
        Measure-Object -Property Length -Sum
    $size = if ($measured.Sum) { $measured.Sum } else { 0 }
    Write-Note ("{0} scenario(s), {1:N1} MB" -f $count, ($size / 1MB))
} else {
    New-Item -ItemType Directory -Path $stagedScenarios -Force | Out-Null
    Write-Note "no scenario directory at $ScenarioDir; shipping an empty folder."
}

# --------------------------------------------------------------------------
# Release assets
#
# Laid out so a GitHub release can offer a bare .exe that runs on download --
# release assets do not have to be archives, and wrapping a single executable in
# a ZIP only adds a step for whoever downloads it. The ZIP still exists for the
# case where the scenarios should travel with the player.
# --------------------------------------------------------------------------
Write-Step "Staging release assets"
$AssetDir = Join-Path $ReleaseRoot "assets"
New-Item -ItemType Directory -Path $AssetDir -Force | Out-Null

# Bare, self-contained: download one file, double-click, no prerequisites.
Copy-Item $exe (Join-Path $AssetDir "CanReplayPlayer-$Version-win-x64.exe")
# Bare, framework-dependent: under a megabyte, needs the .NET Desktop Runtime.
Copy-Item $smallExe (Join-Path $AssetDir "CanReplayPlayer-$Version-win-x64-netdesktop.exe")

# Scenarios are a folder, so this one genuinely needs an archive.
$scenarioZip = Join-Path $AssetDir "Scenarios-$Version.zip"

Write-Step "Building the portable ZIP"
$zip = Join-Path $ReleaseRoot "CanReplayPlayer-portable-win-x64.zip"
Compress-Archive -Path (Join-Path $PortableDir "*") -DestinationPath $zip -CompressionLevel Optimal
Write-Note ("{0}  ({1:N1} MB)" -f (Split-Path -Leaf $zip), ((Get-Item $zip).Length / 1MB))

if (@(Get-ChildItem $stagedScenarios -Directory -ErrorAction SilentlyContinue).Count -gt 0) {
    Compress-Archive -Path (Join-Path $stagedScenarios "*") -DestinationPath $scenarioZip `
        -CompressionLevel Optimal
    Write-Note ("{0}  ({1:N1} MB)" -f (Split-Path -Leaf $scenarioZip),
        ((Get-Item $scenarioZip).Length / 1MB))
}

# --------------------------------------------------------------------------
# Installer
# --------------------------------------------------------------------------
function Resolve-Iscc {
    @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

if ($SkipInstaller) {
    Write-Step "Installer skipped (-SkipInstaller)"
} else {
    $iscc = Resolve-Iscc
    if (-not $iscc) {
        Write-Step "Installer skipped"
        Write-Note "Inno Setup 6 not found. Install it with:"
        Write-Note "  winget install --id JRSoftware.InnoSetup --exact"
    } else {
        Write-Step "Building the installer"
        New-Item -ItemType Directory -Path $InstallerDir -Force | Out-Null

        # The scenarios are already inside the portable folder, so the installer
        # takes the whole folder and needs no separate scenario source.
        & $iscc `
            "/DAppVersion=$Version" `
            "/DPublishDir=$PortableDir" `
            "/DOutputDir=$InstallerDir" `
            (Join-Path $RepoRoot "installer\CanReplayPlayer.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

        Get-ChildItem $InstallerDir -Filter "*.exe" | ForEach-Object {
            Write-Note ("{0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
        }
    }
}

# --------------------------------------------------------------------------
# Checksums
# --------------------------------------------------------------------------
Write-Step "Writing checksums"
$checksums = Join-Path $ReleaseRoot "checksums.txt"
$lines = @("# CAN Vehicle Replay $Version", "# SHA256", "")
Get-ChildItem $ReleaseRoot -Recurse -File |
    Where-Object { $_.Extension -in ".exe", ".zip" } |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        $relative = $_.FullName.Substring($ReleaseRoot.Length + 1)
        $lines += "$hash  $relative"
    }
$lines | Set-Content $checksums -Encoding utf8
Get-Content $checksums | ForEach-Object { Write-Note $_ }

Write-Step "Done"
Write-Host "Artifacts are in $ReleaseRoot" -ForegroundColor Green
