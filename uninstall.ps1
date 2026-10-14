<#
.SYNOPSIS
    Removes ADO Lens: the Claude registrations of this installation, optionally the stored PATs and local data, and the program files.

.EXAMPLE
    .\uninstall.ps1
.EXAMPLE
    .\uninstall.ps1 -RemovePats -RemoveData
#>
[CmdletBinding()]
param(
    [string] $InstallDir = $PSScriptRoot,
    [switch] $RemovePats,
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $InstallDir 'bin\adolens.exe'
$data = Join-Path $env:LOCALAPPDATA 'ado-lens'
if (-not (Test-Path $exe)) { throw "$exe not found - run uninstall.ps1 from the installation folder or pass -InstallDir." }

& $exe unregister --check
if ($LASTEXITCODE -eq 3) {
    & $exe unregister
    # 7: Claude Desktop is running and would write the old entry back.
    while ($LASTEXITCODE -eq 7) {
        Write-Host '  Quit Claude Desktop (tray icon -> Quit); waiting...' -ForegroundColor Yellow
        Start-Sleep -Seconds 3
        & $exe unregister
    }
}

$canAsk = [Environment]::UserInteractive -and $Host.Name -ne 'Default Host'
if (-not $RemovePats -and $canAsk) { $RemovePats = (Read-Host '  Remove the stored PATs from Windows Credential Manager? [y/N]') -match '^\s*(y|yes|t|tak)\s*$' }
if ($RemovePats) {
    $cfg = Join-Path $data 'adolens.json'
    if (Test-Path $cfg) {
        foreach ($name in (Get-Content $cfg -Raw | ConvertFrom-Json).connections.PSObject.Properties.Name) { & $exe logout --connection $name }
    }
}
if (-not $RemoveData -and $canAsk) { $RemoveData = (Read-Host "  Remove settings, metadata and usage log ($data)? [y/N]") -match '^\s*(y|yes|t|tak)\s*$' }

Get-CimInstance Win32_Process -Filter "Name='adolens.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

# Skills this installation put into Claude Code.
$skillSrc = Join-Path $InstallDir 'skills'
if (Test-Path $skillSrc) {
    foreach ($skill in Get-ChildItem $skillSrc -Directory) {
        $installed = Join-Path $env:USERPROFILE ".claude\skills\$($skill.Name)"
        if (Test-Path (Join-Path $installed 'SKILL.md')) { Remove-Item $installed -Recurse -Force; Write-Host "  removed skill $($skill.Name) from Claude Code" }
    }
}

if ($RemoveData -and (Test-Path $data)) { Remove-Item $data -Recurse -Force; Write-Host "  removed $data" }
# This script lives in the folder it removes: delete everything else now, the folder itself after it exits.
Get-ChildItem $InstallDir -Force | Where-Object { $_.FullName -ne $PSCommandPath } | Remove-Item -Recurse -Force
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$InstallDir`"" -WindowStyle Hidden
Write-Host "  ADO Lens removed from $InstallDir" -ForegroundColor Cyan
