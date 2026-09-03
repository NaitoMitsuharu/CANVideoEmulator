# Run from the parent directory after closing applications using this checkout.
[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$SourcePath = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ParentPath = Split-Path -Parent $SourcePath
$TargetPath = Join-Path $ParentPath 'CANVideoEmulator'
if ((Split-Path -Leaf $SourcePath) -eq 'CANVideoEmulator') { Write-Host 'Already named CANVideoEmulator.'; return }
if ((Split-Path -Leaf $SourcePath) -ne 'PCANUSBDemo') { throw "Unexpected source: $SourcePath" }
if (-not (Test-Path -LiteralPath (Join-Path $SourcePath '.git'))) { throw 'Not a repository checkout.' }
if (Test-Path -LiteralPath $TargetPath) { throw "Destination already exists: $TargetPath" }
if ([IO.Path]::GetDirectoryName($TargetPath) -ne $ParentPath) { throw 'Target is outside the parent directory.' }
Set-Location -LiteralPath $ParentPath
if ($PSCmdlet.ShouldProcess($SourcePath, "Rename to $TargetPath")) {
    Rename-Item -LiteralPath $SourcePath -NewName 'CANVideoEmulator'
    Write-Host "Renamed: $TargetPath"
    Write-Host 'Reopen this folder. Rerun setup.ps1 -WithExample to refresh local Python paths.'
}
