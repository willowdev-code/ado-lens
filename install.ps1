<#
.SYNOPSIS
    Installs or updates ADO Lens for Azure DevOps: work items, boards, wiki, Git and TFVC served to Claude over MCP.

.DESCRIPTION
    Run it from an unpacked package, in a regular (non-elevated) PowerShell window outside Claude.
      1. checks whether the Claude registration has to change,
      2. copies the program to the installation folder (no administrator rights needed),
      3. sets up a connection when there is none: Azure DevOps address, PAT (hidden input, stored in
         Windows Credential Manager, never in a file) and default project,
      4. registers the 'ado-lens' MCP server in Claude Desktop (and Claude Code, when installed).
    Claude Desktop writes its configuration back while it runs, so when the registration has to change the
    script asks you to quit Claude, waits for it and starts it again afterwards. Settings, PATs and metadata
    stay in place on an update.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -Organization https://dev.azure.com/contoso/Demo -Mode dev
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ADO Lens'),
    [string] $Organization,
    [ValidateSet('po', 'dev', 'full')]
    [string] $Mode,
    [switch] $NoRegister,
    [switch] $DesktopOnly,
    [switch] $CodeOnly,
    [switch] $NoRestart
)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host "  $m" }
function Head($m) { Write-Host "`n$m" -ForegroundColor Cyan }
function Get-ClaudeDesktop {
    Get-Process -Name claude -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like '*\WindowsApps\Claude_*' -or $_.Path -like '*\AnthropicClaude\*' }
}
function Start-ClaudeDesktop {
    $pkg = Get-AppxPackage -Name Claude -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pkg) { Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!Claude"; return $true }
    $exe = Join-Path $env:LOCALAPPDATA 'AnthropicClaude\claude.exe'
    if (Test-Path $exe) { Start-Process $exe; return $true }
    return $false
}

$src = $PSScriptRoot
$pkgExe = Join-Path $src 'bin\adolens.exe'
if (-not (Test-Path $pkgExe)) { throw "$pkgExe not found - run install.ps1 from an unpacked ADO Lens package." }
$exe = Join-Path ([IO.Path]::GetFullPath($InstallDir)) 'bin\adolens.exe'
$scope = @()
if ($DesktopOnly) { $scope += '--desktop' }
if ($CodeOnly) { $scope += '--code' }

Head '1/6 Claude registration'
$needsRegister = $false
if ($NoRegister) {
    Say 'skipped (-NoRegister)'
}
else {
    & $pkgExe register --check --exe $exe @scope
    $needsRegister = ($LASTEXITCODE -eq 3)
}
$closedClaude = $false
if ($needsRegister -and (Get-ClaudeDesktop)) {
    Write-Host '  The Claude registration has to change and Claude Desktop is running.' -ForegroundColor Yellow
    Write-Host '  Quit Claude now: tray icon (next to the clock) -> right click -> Quit. Waiting...' -ForegroundColor Yellow
    while (Get-ClaudeDesktop) { Start-Sleep -Seconds 1 }
    Start-Sleep -Seconds 2
    $closedClaude = $true
    Say 'Claude is closed'
}

Head "2/6 Copying files to $InstallDir"
$binDst = Join-Path $InstallDir 'bin'
New-Item -ItemType Directory -Force -Path $binDst | Out-Null
$target = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
if ((Resolve-Path $src).Path.TrimEnd('\') -ieq $target) {
    Say 'the package is already in the target folder - skipping copy'
}
else {
    # A running adolens.exe (an open Claude session) cannot be overwritten, but it can be renamed: that session keeps
    # running from the *.old file, new sessions start the new version, a later install deletes the leftover.
    Get-ChildItem $binDst -Filter '*.old' | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    foreach ($f in Get-ChildItem (Join-Path $src 'bin') -File) {
        $dst = Join-Path $binDst $f.Name
        if (Test-Path $dst) {
            try { Remove-Item $dst -Force -ErrorAction Stop }
            catch {
                $oldName = "$($f.Name).$((Get-Date).ToString('yyyyMMddHHmmss')).old"
                Rename-Item $dst $oldName
                Say "$($f.Name) is in use by an open Claude session - renamed to $oldName"
            }
        }
        Copy-Item $f.FullName $dst -Force
    }
    foreach ($f in 'install.ps1', 'uninstall.ps1', 'README.md', 'README.pl.md', 'CHANGELOG.md', 'SECURITY.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        if (Test-Path (Join-Path $src $f)) { Copy-Item (Join-Path $src $f) $InstallDir -Force }
    }
    if (Test-Path (Join-Path $src 'skills')) { Copy-Item (Join-Path $src 'skills') $InstallDir -Recurse -Force }
}
Say "version: $(& $exe version)"

# "adolens" in any new terminal: the program folder on the user's PATH, as the setup EXE does. Read and written raw
# (REG_EXPAND_SZ) so entries like %USERPROFILE%\... stay as they are.
$binFull = [IO.Path]::GetFullPath($binDst).TrimEnd('\')
$envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
$userPath = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$entries = @($userPath -split ';' | Where-Object { $_ })
if (-not ($entries | Where-Object { [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') -ieq $binFull })) {
    $envKey.SetValue('Path', (($entries + $binFull) -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
    # Tells Windows (Explorer, new terminals) that the environment changed.
    [Environment]::SetEnvironmentVariable('ADOLENS_PATH_REFRESH', '1', 'User')
    [Environment]::SetEnvironmentVariable('ADOLENS_PATH_REFRESH', $null, 'User')
    Say "added to PATH: $binFull ('adolens' works in terminals opened from now on)"
}
$envKey.Close()

Head '3/6 Azure DevOps connection'
& $exe status --quiet --offline
$state = $LASTEXITCODE
if ($state -eq 4 -or $Organization) {
    $loginArgs = @('login')
    if ($Organization) { $loginArgs += @('--org', $Organization) }
    & $exe @loginArgs
    if ($LASTEXITCODE -ne 0) { throw "Login failed - run the installer again or later: `"$exe`" login" }
}
else {
    Say 'already configured'
}

Head '4/6 Who uses it and where code goes'
# Interactive questions with defaults (Enter accepts); skipped in a non-interactive run.
$canAsk = [Environment]::UserInteractive -and $Host.Name -ne 'Default Host' -and
    -not ([Environment]::GetCommandLineArgs() | Where-Object { $_ -like '-NonI*' })
if ($canAsk) {
    $setupArgs = @('setup', '--wizard')
    if ($Mode) { $setupArgs += @('--mode', $Mode) }
    & $exe @setupArgs
}
elseif ($Mode) { & $exe config --mode $Mode | Out-Null; Say "mode: $Mode" }
else { Say 'skipped (not interactive) - later: "adolens setup", or Claude asks when needed' }

Head '5/6 Skills for Claude'
# Claude Code reads skills from %USERPROFILE%\.claude\skills; Claude Desktop takes each skill as a ZIP uploaded in
# Settings -> Capabilities -> Skills, so the ZIPs are prepared next to the program.
$skillSrc = Join-Path $InstallDir 'skills'
if (Test-Path $skillSrc) {
    # Claude Code loads every installed skill in each session: only the skills of the mode go there. The ZIPs for
    # Claude Desktop are written next to the program; both with descriptions in the language of the installation.
    Say (& $exe skills | Out-String).Trim()
}
else { Say 'no skills in this package' }

Head '6/6 Registering in Claude'
if ($needsRegister) {
    & $exe register @scope --force
    if ($LASTEXITCODE -ne 0) { throw 'Registration failed.' }
}
elseif ($NoRegister) {
    Say "skipped (-NoRegister). Later: `"$exe`" register"
}
else {
    Say 'already registered'
}

Head 'Done'
if ($closedClaude -and -not $NoRestart) {
    if (Start-ClaudeDesktop) { Say 'Claude Desktop started again' } else { Say 'start Claude Desktop again' }
}
else {
    Say 'Claude Desktop: restart it (tray icon -> Quit, then start it). Claude Code: start a new session or /mcp -> reconnect.'
}
Say "Try in Claude:  what work item types and fields does our project have?"
Say "Status:     `"$exe`" status"
Say "Uninstall:  $InstallDir\uninstall.ps1"
