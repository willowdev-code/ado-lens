<#
.SYNOPSIS
    Builds the ADO Lens distribution packages: the ZIP (install.ps1), the installer EXE and the Claude Desktop extension.

.DESCRIPTION
    Publishes a self-contained single EXE (nothing to install on the target machine, not even .NET),
    compiled ahead with ReadyToRun so the server starts fast every time Claude opens a session.
    Outputs in OutDir, each with a .sha256 file:
      ado-lens-X.Y.Z.zip            program, scripts and skills (get.ps1 / install.ps1, updates)
      ado-lens-setup-X.Y.Z.exe      installer with a wizard (Inno Setup; skipped when ISCC is not found, unless -Installer)
      ado-lens-X.Y.Z.mcpb           Claude Desktop extension (double-click in Claude Desktop)

.EXAMPLE
    .\pack.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'dist'),
    [switch] $NoZip,
    # Fail when Inno Setup (ISCC.exe) is not found instead of skipping the installer (CI).
    [switch] $Installer
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
foreach ($f in 'install.ps1', 'uninstall.ps1', 'README.md', 'README.pl.md', 'CHANGELOG.md', 'SECURITY.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $PSScriptRoot $f) $stage -Force
}
# Skills for Claude (SKILL.md per folder), installed next to the server.
Copy-Item (Join-Path $PSScriptRoot 'skills') $stage -Recurse -Force

$size = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "package: $stage ($size MB)" -ForegroundColor Cyan

function Write-Checksum([string] $file, [string] $label) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$file.sha256" -Value "$hash  $(Split-Path $file -Leaf)" -Encoding ascii
    Write-Host "${label}: $file ($([math]::Round((Get-Item $file).Length / 1MB, 1)) MB, sha256 $hash)" -ForegroundColor Cyan
}

# Entries with '/' as the ZIP format requires (Compress-Archive in Windows PowerShell 5.1 writes '\').
# $withFolder: entries start with the folder's own name (a skill ZIP holds "<skill>/SKILL.md").
function New-Zip([string] $folder, [string] $zip, [bool] $withFolder) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # The long form of the path: entry names are cut from the files' full (long) paths.
    $folder = (Get-Item -LiteralPath $folder).FullName.TrimEnd('\')
    $root = if ($withFolder) { Split-Path $folder -Parent } else { $folder }
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $folder -Recurse -File) {
            $entry = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
}

if (-not $NoZip) {
    $version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $zip = Join-Path $OutDir "ado-lens-$version.zip"
    New-Zip $stage $zip $true
    Write-Checksum $zip 'zip'

    # Claude Desktop extension: the manifest (version filled in) and the program.
    $mcpbStage = Join-Path $OutDir 'mcpb'
    if (Test-Path $mcpbStage) { Remove-Item $mcpbStage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $mcpbStage | Out-Null
    $manifest = (Get-Content (Join-Path $PSScriptRoot 'packaging\mcpb\manifest.json') -Raw -Encoding UTF8).Replace('{{VERSION}}', $version)
    [IO.File]::WriteAllText((Join-Path $mcpbStage 'manifest.json'), $manifest, (New-Object Text.UTF8Encoding $false))
    Copy-Item (Join-Path $stage 'bin') $mcpbStage -Recurse
    Copy-Item (Join-Path $stage 'LICENSE') $mcpbStage
    Copy-Item (Join-Path $stage 'THIRD-PARTY-NOTICES.md') $mcpbStage
    Copy-Item (Join-Path $PSScriptRoot 'packaging\icon\icon.png') $mcpbStage
    $mcpb = Join-Path $OutDir "ado-lens-$version.mcpb"
    New-Zip $mcpbStage $mcpb $false
    Write-Checksum $mcpb 'mcpb'

    # Skills as ZIPs for Claude Desktop (Settings -> Capabilities -> Skills), shipped by the installer.
    $desktopSkills = Join-Path $OutDir 'desktop-skills'
    if (Test-Path $desktopSkills) { Remove-Item $desktopSkills -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $desktopSkills | Out-Null
    foreach ($skill in Get-ChildItem (Join-Path $stage 'skills') -Directory) {
        New-Zip $skill.FullName (Join-Path $desktopSkills "$($skill.Name).zip") $true
    }

    # Installer with a wizard (Inno Setup 6).
    $iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($iscc) {
        Write-Host 'building the installer (Inno Setup)...' -ForegroundColor Cyan
        & $iscc /Q "/DVersion=$version" "/DSource=$stage" "/DDesktopSkills=$desktopSkills" "/DOutDir=$OutDir" (Join-Path $PSScriptRoot 'packaging\inno\ado-lens.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
        Write-Checksum (Join-Path $OutDir "ado-lens-setup-$version.exe") 'installer'
    }
    elseif ($Installer) { throw 'Inno Setup 6 (ISCC.exe) not found' }
    else { Write-Host 'installer skipped: Inno Setup 6 (ISCC.exe) not found' -ForegroundColor Yellow }
}

Write-Host "`nOn the target machine: run the installer EXE, or unzip and run install.ps1" -ForegroundColor Green
