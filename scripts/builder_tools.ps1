# Shared preflight: resolve tools in this process, including installs made after
# the player started. Does not modify the user's persistent PATH.
function Resolve-BuilderTools {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $ErrorActionPreference = 'Continue' # Probe failures are collected into the explicit error below.
    $env:PATH = (@($env:PATH, [Environment]::GetEnvironmentVariable('Path', 'User'),
        [Environment]::GetEnvironmentVariable('Path', 'Machine')) | Where-Object { $_ }) -join ';'
    $candidates = @(
        (Join-Path $RepoRoot '.venv\Scripts\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
        (Join-Path $env:ProgramFiles 'Python312\python.exe')
    )
    $command = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source -notlike '*\WindowsApps\*') { $candidates += $command.Source }
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        $detected = & $launcher.Source -3.12 -c 'import sys; print(sys.executable)' 2>$null
        if ($LASTEXITCODE -eq 0) { $candidates += $detected }
    }
    $python = $null
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        & $candidate -c 'import sys, struct; sys.exit(0 if sys.version_info[:2] == (3,12) and struct.calcsize(chr(80)) == 8 else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { $python = $candidate; break }
        if ($candidate -eq (Join-Path $RepoRoot '.venv\Scripts\python.exe')) {
            throw 'Existing .venv cannot run Python 3.12 x64. Rename that generated .venv folder and retry to recreate it; do not copy virtual environments between PCs.'
        }
    }
    $missing = @()
    if (-not $python) { $missing += 'Python 3.12 x64: https://www.python.org/downloads/windows/ (or winget install -e --id Python.Python.3.12)' }
    foreach ($name in @('git', 'ffmpeg', 'ffprobe')) {
        if (-not (Get-Command "$name.exe" -ErrorAction SilentlyContinue)) { $missing += "$name.exe on PATH" }
    }
    if ($missing.Count -gt 0) {
        throw "Conversion prerequisites missing: $($missing -join '; '). Install Git for Windows and FFmpeg including ffprobe (https://www.gyan.dev/ffmpeg/builds/ or winget install -e --id Gyan.FFmpeg), then retry."
    }
    $encoders = & ffmpeg -hide_banner -encoders 2>&1
    if ($LASTEXITCODE -ne 0 -or -not ($encoders -match '\blibx264\b')) { throw 'FFmpeg must include the libx264 encoder. Install a full/essentials build from https://www.gyan.dev/ffmpeg/builds/.' }
    & ffprobe -v error -version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ffprobe could not run. Reinstall FFmpeg including ffprobe.' }
    & git --version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Git could not run. Reinstall Git for Windows.' }
    return $python
}
