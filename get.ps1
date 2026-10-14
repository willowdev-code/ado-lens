<#
.SYNOPSIS
    Downloads the latest ADO Lens release, checks it and runs its installer - one line, no browser.

.DESCRIPTION
    Meant to be run straight from GitHub in a regular (non-administrator) PowerShell window:

        irm https://raw.githubusercontent.com/willowdev-code/ado-lens/main/get.ps1 | iex

    It reads the latest release from the GitHub API, downloads ado-lens-X.Y.Z.zip, compares its SHA-256 with the
    digest GitHub publishes for it, unpacks it into a temporary folder and runs its install.ps1. Nothing is installed
    when the checksum does not match. A file downloaded by PowerShell is not marked as coming from the internet, so
    Windows SmartScreen does not stop the (not yet signed) program.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'willowdev-code/ado-lens'
Write-Host 'ADO Lens for Azure DevOps - looking up the latest release...' -ForegroundColor Cyan
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'ado-lens-get' }
$asset = $release.assets | Where-Object { $_.name -like 'ado-lens-*.zip' } | Select-Object -First 1
if (-not $asset) { throw "release $($release.tag_name) has no ado-lens-*.zip package" }
if (-not $asset.digest -or $asset.digest -notlike 'sha256:*') { throw "GitHub publishes no SHA-256 digest for $($asset.name) - not installing an unverified package" }
$expected = $asset.digest.Substring(7).ToLowerInvariant()

$work = Join-Path ([IO.Path]::GetTempPath()) ("ado-lens-get-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$zip = Join-Path $work $asset.name
Write-Host "downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'ado-lens-get' }

$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch for $($asset.name): expected $expected, got $actual - not installing" }
Write-Host "SHA-256 verified: $actual"

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $work 'package'))
$installer = Get-ChildItem (Join-Path $work 'package') -Recurse -Filter install.ps1 | Select-Object -First 1
if (-not $installer) { throw 'the package has no install.ps1' }

& powershell -NoProfile -ExecutionPolicy Bypass -File $installer.FullName
