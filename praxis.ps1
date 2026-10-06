# Runs this project's pinned Praxis CLI (DF-ROS-2026-A049, DF-ROS-2026-A050), on
# Windows. The self-contained native release for the pinned version is
# installed side by side under $env:ECHELON_HOME (default ~\.echelon) on
# first use, without changing which version your global commands run.
$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$homeDir = if ($env:ECHELON_HOME) { $env:ECHELON_HOME } else { Join-Path $HOME ".echelon" }

function Read-PinnedVersion([string]$path, [string]$property) {
    if (-not (Test-Path $path)) { return $null }
    $value = (Get-Content -Raw $path | ConvertFrom-Json).$property
    if ($value -is [string] -and $value.Trim().Length -gt 0) { return $value.Trim() }
    return $null
}

$version = Read-PinnedVersion (Join-Path $projectRoot ".echelon\ros.json") "installedVersion"
if (-not $version) { $version = Read-PinnedVersion (Join-Path $projectRoot "ros.json") "rosVersion" }
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+$') {
    [Console]::Error.WriteLine("praxis: no usable pinned version in .echelon\ros.json (installedVersion) or ros.json (rosVersion).")
    exit 1
}

$praxis = Join-Path $homeDir "tools\praxis\$version\praxis.cmd"
if (-not (Test-Path $praxis)) {
    $installerUrl = if ($env:PRAXIS_INSTALLER_URL) { $env:PRAXIS_INSTALLER_URL } else { "https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-native.ps1" }
    $installer = Join-Path ([IO.Path]::GetTempPath()) ("praxis-install-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $installerUrl -OutFile $installer
        & $installer -Version $version -InstallBase $homeDir -NoActivate | Out-Host
    }
    finally {
        Remove-Item -Force -ErrorAction SilentlyContinue $installer
    }
}

& $praxis @args
exit $LASTEXITCODE
