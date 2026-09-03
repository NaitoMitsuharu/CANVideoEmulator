[CmdletBinding()]
param([switch]$WithExample, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Windows x64 is required.' }
if ($WithExample) {
    . (Join-Path $PSScriptRoot 'builder_tools.ps1')
    $null = Resolve-BuilderTools -RepoRoot $RepoRoot
}
$ToolsDir = Join-Path $RepoRoot '.tools' 
$SdkDir = Join-Path $ToolsDir 'dotnet'
New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
$candidates = @(
    (Join-Path $SdkDir 'dotnet.exe'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
)
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }
$Dotnet = $null
foreach ($candidate in $candidates | Select-Object -Unique) {
    if (Test-Path -LiteralPath $candidate) {
        $versions = & $candidate --list-sdks
        if ($versions -match '^10\.0\.') { $Dotnet = $candidate; break }
    }
}
if (-not $Dotnet) {
    Write-Host 'Installing .NET 10 SDK locally in .tools/dotnet (no administrator rights needed)...'
    $installer = Join-Path $ToolsDir 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & $installer -Channel '10.0' -Quality GA -Architecture x64 -InstallDir $SdkDir -NoPath
    $Dotnet = Join-Path $SdkDir 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $Dotnet)) { throw '.NET SDK installation failed.' }
}
$env:DOTNET_ROOT = Split-Path -Parent $Dotnet
Push-Location (Join-Path $RepoRoot 'windows')
try {
    & $Dotnet publish 'src\CANVideoEmulator.Wpf\CANVideoEmulator.Wpf.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $RepoRoot 'bin') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Player build failed.' }
} finally { Pop-Location }
Set-Content -LiteralPath (Join-Path $RepoRoot 'bin\portable.txt') -Value 'Keep settings and logs beside the player.'
New-Item -ItemType Directory -Path (Join-Path $RepoRoot 'Scenarios') -Force | Out-Null
if ($WithExample) { & (Join-Path $PSScriptRoot 'prepare_scenarios.ps1') }
Write-Host "Ready: $RepoRoot\bin\CANVideoEmulator.exe"
Write-Host 'For hardware output, install the PEAK driver including PCAN-Basic: https://www.peak-system.com/quick/DrvSetup'
if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $RepoRoot 'bin\CANVideoEmulator.exe') -WorkingDirectory (Join-Path $RepoRoot 'bin')
}
