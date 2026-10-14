<#
.SYNOPSIS
    Builds the ADO Lens distribution package (ZIP) for installing on another machine.

.DESCRIPTION
    Publishes a self-contained single EXE (nothing to install on the target machine, not even .NET),
    compiled ahead with ReadyToRun so the server starts fast every time Claude opens a session.

.EXAMPLE
    .\pack.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'dist'),
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
# Absolute: ZIP entry names are cut from full paths.
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$proj = Join-Path $PSScriptRoot 'src\AdoLens\AdoLens.csproj'
$stage = Join-Path $OutDir 'ado-lens'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$publishArgs = @(
    'publish', $proj, '-c', 'Release', '-r', 'win-x64', '-o', (Join-Path $stage 'bin'), '--nologo',
    '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:PublishReadyToRun=true',
    # No debug information: a release build would otherwise carry the local path of its .pdb file.
    '-p:DebugType=None', '-p:DebugSymbols=false'
)
Write-Host 'publishing (self-contained)...' -ForegroundColor Cyan
& dotnet @publishArgs | Where-Object { $_ -match 'error|warning CS|->' }
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Remove-Item (Join-Path $stage 'bin\*.pdb') -Force -ErrorAction SilentlyContinue
foreach ($f in 'install.ps1', 'uninstall.ps1', 'README.md', 'README.pl.md', 'CHANGELOG.md', 'SECURITY.md', 'LICENSE') {
    Copy-Item (Join-Path $PSScriptRoot $f) $stage -Force
}
# Skills for Claude (SKILL.md per folder), installed next to the server.
Copy-Item (Join-Path $PSScriptRoot 'skills') $stage -Recurse -Force

$size = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "package: $stage ($size MB)" -ForegroundColor Cyan

if (-not $NoZip) {
    $version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $zip = Join-Path $OutDir "ado-lens-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Entries with '/' as the ZIP format requires (Compress-Archive in Windows PowerShell 5.1 writes '\').
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = Split-Path $stage -Parent
        foreach ($file in Get-ChildItem $stage -Recurse -File) {
            $entry = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ascii
    Write-Host "zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, sha256 $hash)" -ForegroundColor Cyan
}

Write-Host "`nOn the target machine: unzip and run install.ps1" -ForegroundColor Green
