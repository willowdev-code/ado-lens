<#
.SYNOPSIS
    Writes the release notes of one version: install instructions plus its section of CHANGELOG.md.

.DESCRIPTION
    Used by the release workflow (.github/workflows/release.yml); can be run locally to preview the text.
    Fails when CHANGELOG.md has no (or an empty) section '## <Version>'.

.EXAMPLE
    .\release-notes.ps1 -Version 0.1.0
.EXAMPLE
    .\release-notes.ps1 -Version 0.1.0 -OutFile dist\notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$changelog = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CHANGELOG.md'), [Text.Encoding]::UTF8) -replace "`r`n", "`n"
$lines = $changelog -split "`n"

# A test release (0.2.0-beta.2) is described by the section of the version it leads to (## 0.2.0 - unreleased).
$isTest = $Version -match '-'
$sectionVersion = ($Version -split '-')[0]
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match ('^## ' + [regex]::Escape($sectionVersion) + '(\s|$)')) { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no section '## $sectionVersion'" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## ') { $end = $i; break }
}
$section = if ($end - 1 -gt $start) { ($lines[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
if (-not $section) { throw "the CHANGELOG.md section for $Version is empty" }

$testNote = if ($isTest) {
    @"
**Test release** of $sectionVersion, for trying it out before the regular release.


"@
} else { '' }

$notes = @"
$($testNote)ADO Lens for Azure DevOps: work items, boards, wiki, Git and TFVC served to Claude (Desktop and Code) over MCP.

## Install

Download ``ado-lens-$Version.zip``, unzip it and run in a regular (non-admin) PowerShell:

``````powershell
powershell -ExecutionPolicy Bypass -File .\ado-lens\install.ps1
``````

Self-contained build - no .NET required on the target machine. Already installed? Run ``install.ps1`` from this
package: settings, PATs and the local metadata are kept. The SHA-256 of the package is in ``ado-lens-$Version.zip.sha256``.

## Changes

$section

Released under the MIT License. Not affiliated with or endorsed by Microsoft.
"@ -replace "`r`n", "`n"

if ($OutFile) {
    $dir = Split-Path $OutFile -Parent
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($OutFile, $notes, (New-Object Text.UTF8Encoding($false)))
    Write-Host "notes: $OutFile"
}
else {
    $notes
}
