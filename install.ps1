param(
    [string]$Version = "latest",
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Camp"),
    [switch]$AddToPath,
    [switch]$NoPath,
    [string]$Repo = "camplang-dev/camp",
    [ValidateSet("x64", "x86")][string]$Arch,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
if ($AddToPath -and $NoPath) {
    throw "-AddToPath and -NoPath cannot be used together."
}

function Resolve-Rid {
    param([string]$RequestedArch)
    if ($RequestedArch -eq "x86") {
        return "win-x86"
    }
    return "win-x64"
}

function Resolve-LatestVersion {
    param([string]$Repository)
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=20"
    if ($release -is [array]) {
        $release = $release | Select-Object -First 1
    }
    if (-not $release.tag_name) {
        throw "Could not resolve a published release for $Repository."
    }
    return $release.tag_name
}

function Add-CampToPath {
    param([string]$BinDir)
    $current = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($null -eq $current) {
        $current = ""
    }
    $parts = $current -split ';' | Where-Object { $_ }
    if ($parts -contains $BinDir) {
        Write-Host "Camp bin directory is already on the user PATH."
        return
    }
    $newPath = if ($current.Length -eq 0) { $BinDir } else { "$current;$BinDir" }
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    Write-Host "Added Camp to the user PATH. Open a new terminal to use campc directly."
}

function Write-EditorSetup {
    param([string]$Version, [string]$InstallDir)
    Write-Host ""
    Write-Host "Camp $Version installed."
    Write-Host ""
    Write-Host "Optional editor setup:"
    Write-Host "  VS Code:  & `"$InstallDir\extras\editors\vscode\install.ps1`""
    Write-Host "  Sublime:  & `"$InstallDir\extras\editors\sublime\install.ps1`""
    Write-Host "  micro:    & `"$InstallDir\extras\editors\micro\install.ps1`""
    Write-Host "  Vim:      & `"$InstallDir\extras\editors\vim\install.ps1`""
    Write-Host "  Fresh:    & `"$InstallDir\extras\editors\fresh\install.ps1`""
    Write-Host ""
    Write-Host "Run the command for your editor. VS Code includes syntax, language server, and debugging."
    Write-Host "Other editors install syntax highlighting and language-server support when supported."
    Write-Host "Use -Help on an editor command for advanced options."
}

if (-not $Arch) {
    $Arch = if ([Environment]::Is64BitOperatingSystem) { "x64" } else { "x86" }
}
$rid = Resolve-Rid $Arch
if ($Version -eq "latest") {
    $Version = Resolve-LatestVersion $Repo
}

$asset = "camp-$Version-$rid.zip"
$checksum = "$asset.sha256"
$downloadBase = "https://github.com/$Repo/releases/download/$Version"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
$backupDir = $null

Write-Host "Camp $Version"
Write-Host "platform: $rid"
Write-Host "install:  $InstallDir"
if ($DryRun) {
    Write-Host "dry run: would download $downloadBase/$asset"
    return
}

New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
try {
    $archivePath = Join-Path $tempRoot $asset
    $checksumPath = Join-Path $tempRoot $checksum
    Invoke-WebRequest -Uri "$downloadBase/$asset" -OutFile $archivePath
    Invoke-WebRequest -Uri "$downloadBase/$checksum" -OutFile $checksumPath

    $expectedHash = ((Get-Content $checksumPath -Raw) -split '\s+')[0].ToLowerInvariant()
    $actualHash = (Get-FileHash -Algorithm SHA256 $archivePath).Hash.ToLowerInvariant()
    if ($expectedHash -ne $actualHash) {
        throw "Checksum mismatch for $asset. Expected $expectedHash, got $actualHash."
    }

    Expand-Archive -Path $archivePath -DestinationPath $tempRoot
    $unpacked = Get-ChildItem -Path $tempRoot -Directory | Where-Object { $_.Name -like "camp-*" } | Select-Object -First 1
    if (-not $unpacked -or -not (Test-Path (Join-Path $unpacked.FullName "bin\campc.exe"))) {
        throw "Downloaded archive does not contain a valid Camp install layout."
    }

    $parent = Split-Path -Parent $InstallDir
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    if (Test-Path $InstallDir) {
        $backupDir = "$InstallDir.backup.$(Get-Date -Format yyyyMMddHHmmss)"
        Move-Item $InstallDir $backupDir
    }
    try {
        Move-Item $unpacked.FullName $InstallDir
    }
    catch {
        if ($backupDir -and (Test-Path $backupDir)) {
            Move-Item $backupDir $InstallDir
        }
        throw
    }
    if ($backupDir -and (Test-Path $backupDir)) {
        Remove-Item -Recurse -Force $backupDir
    }

    $env:CAMP_HOME = $InstallDir
    & (Join-Path $InstallDir "bin\campc.exe") --help | Out-Null
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $binDir = Join-Path $InstallDir "bin"
    if ($AddToPath) {
        Add-CampToPath $binDir
    }
    elseif (-not $NoPath) {
        Write-Host ""
        Write-Host "Add Camp to PATH with:"
        Write-Host "  [Environment]::SetEnvironmentVariable('Path', [Environment]::GetEnvironmentVariable('Path', 'User') + ';$binDir', 'User')"
    }

    Write-Host "installed: $InstallDir"
    Write-Host "verify:    $binDir\campc.exe --help"
    Write-EditorSetup $Version $InstallDir
}
finally {
    Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
    Remove-Item Env:CAMP_HOME -ErrorAction SilentlyContinue
}
