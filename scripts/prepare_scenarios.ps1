[CmdletBinding()]
param(
    [string]$InputPath = '',
    [string]$OutputPath = '',
    [ValidateRange(0, 2019)][int]$Count = 0,
    [ValidateRange(0, 51)][int]$Crf = 28,
    [ValidateRange(0, 8192)][int]$MaxWidth = 1280,
    [switch]$Rebuild,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Set-StrictMode -Version Latest
Write-Output 'CANVIDEO_PROGRESS {"phase":"prepare","completed":0,"total":1,"detail":"Preparing build tools"}'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $RepoRoot 'Scenarios' }
. (Join-Path $PSScriptRoot 'builder_tools.ps1')
$BasePython = Resolve-BuilderTools -RepoRoot $RepoRoot
if ($CheckOnly) { Write-Host "Conversion tools ready: $BasePython; Git; FFmpeg/libx264; ffprobe"; exit 0 }
$VenvDir = Join-Path $RepoRoot '.venv'
$PythonExe = Join-Path $VenvDir 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $PythonExe)) {
    & $BasePython -m venv $VenvDir
    if ($LASTEXITCODE -ne 0) { throw 'Could not create Python venv.' }
}
& $PythonExe -m pip install --only-binary=:all: -r (Join-Path $RepoRoot 'scenario_builder\requirements-windows.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not install builder wheels. Use Python 3.12 x64 and check internet access to pypi.org.' }
& $PythonExe -m pip install --no-deps -e (Join-Path $RepoRoot 'scenario_builder')
if ($LASTEXITCODE -ne 0) { throw 'Builder dependencies could not be installed.' }
$DbcRoot = Join-Path $RepoRoot '.tools\opendbc'
$DbcRevision = '3e92d112129507debe45364891954db70238997a'
if (-not (Test-Path -LiteralPath (Join-Path $DbcRoot '.git'))) {
    New-Item -ItemType Directory -Path $DbcRoot -Force | Out-Null
    & git -C $DbcRoot init
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize DBC checkout.' }
    & git -C $DbcRoot fetch --depth 1 'https://github.com/commaai/opendbc.git' $DbcRevision
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch opendbc.' }
    & git -C $DbcRoot checkout --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not check out opendbc.' }
}
$actualRevision = & git -C $DbcRoot rev-parse HEAD
if ($actualRevision -ne $DbcRevision) { throw "Expected opendbc $DbcRevision; found $actualRevision in $DbcRoot. Preserve any local edits before replacing that checkout." }
Push-Location $DbcRoot
$oldPythonPath = $env:PYTHONPATH
try {
    $env:PYTHONPATH = $DbcRoot
    & $PythonExe 'opendbc\dbc\generator\generator.py'
    if ($LASTEXITCODE -ne 0) { throw 'DBC generation failed.' }
} finally { $env:PYTHONPATH = $oldPythonPath; Pop-Location }
Write-Output 'CANVIDEO_PROGRESS {"phase":"prepare","completed":1,"total":1,"detail":"Build tools ready"}'
if (-not $InputPath) {
    $dataRoot = Join-Path $RepoRoot 'data'
    & $PythonExe (Join-Path $PSScriptRoot 'dataset_assets.py') --output $dataRoot
    if ($LASTEXITCODE -ne 0) { throw 'Example download failed; rerun to skip completed files.' }
    $InputPath = Join-Path $dataRoot 'Example_1\b0c9d2329ad1606b_2018-08-02--08-34-47\40'
} elseif ([IO.Path]::GetExtension($InputPath) -eq '.zip') {
    $archive = (Resolve-Path -LiteralPath $InputPath).Path
    $InputPath = Join-Path (Split-Path -Parent $archive) ([IO.Path]::GetFileNameWithoutExtension($archive) + '_extracted')
    & $PythonExe (Join-Path $PSScriptRoot 'dataset_assets.py') --archive $archive --output $InputPath
    if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed.' }
}
Write-Output 'CANVIDEO_PROGRESS {"phase":"extract","completed":1,"total":1,"detail":"Input files ready"}'
# A source-derived prefix keeps separate chunks from overwriting rav4_001.
$inputName = Split-Path -Leaf $InputPath
if ($inputName -match '^\d+$') { $inputName = (Split-Path -Leaf (Split-Path -Parent $InputPath)) + '_' + $inputName }
$prefix = 'comma2k19_' + ($inputName -replace '[^a-zA-Z0-9_-]', '_')
$reuseArguments = @()
if (-not $Rebuild) { $reuseArguments += '--skip-existing' }
& $PythonExe -u -m scenario_builder build @reuseArguments --input $InputPath --output $OutputPath --dbc-dir (Join-Path $DbcRoot 'opendbc\dbc') --limit $Count --crf $Crf --max-width $MaxWidth --prefix $prefix
if ($LASTEXITCODE -ne 0) { throw 'Scenario conversion failed.' }
& $PythonExe -m scenario_builder verify --input $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'Scenario verification failed.' }
Write-Host "Scenarios ready: $OutputPath. Press Reload in the player."
