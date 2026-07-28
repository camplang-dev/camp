param(
    [switch]$SyntaxOnly,
    [switch]$NoLsp,
    [switch]$Force,
    [switch]$DryRun,
    [switch]$Help
)

$ErrorActionPreference = "Stop"

if ($Help) {
@"
Usage:
  install.ps1 [options]

Installs or updates Camp support for Sublime Text.
Syntax highlighting is always installed. Language-server support is configured
when the Sublime LSP package is detected.

Options:
  -SyntaxOnly    Install syntax highlighting only
  -NoLsp         Do not configure language-server support
  -Force         Overwrite Camp-owned files without prompting
  -DryRun        Show changes without applying them
  -Help          Show help
"@ | Write-Host
    exit 0
}

function Install-File {
    param([string]$Source, [string]$Destination)
    if ((Test-Path $Destination) -and
        ((Get-FileHash $Source).Hash -ne (Get-FileHash $Destination).Hash) -and
        -not $Force) {
        $backup = "$Destination.backup.$(Get-Date -Format yyyyMMddHHmmss)"
        if ($DryRun) {
            Write-Host "dry run: would back up $Destination to $backup"
        }
        else {
            Copy-Item $Destination $backup
            Write-Host "backup: $backup"
        }
    }
    if ($DryRun) {
        Write-Host "dry run: would install $Destination"
    }
    else {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
        Copy-Item $Source $Destination -Force
        Write-Host "installed: $Destination"
    }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$userDir = Join-Path $env:APPDATA "Sublime Text\Packages\User"
Install-File (Join-Path $scriptDir "Camp.sublime-syntax") (Join-Path $userDir "Camp.sublime-syntax")

if ($SyntaxOnly -or $NoLsp) {
    Write-Host "LSP configuration skipped."
    exit 0
}

$packageRoot = Split-Path -Parent $userDir
if ((Test-Path (Join-Path $packageRoot "LSP")) -or
    (Test-Path (Join-Path $userDir "LanguageServers.sublime-settings")) -or
    (Test-Path (Join-Path $userDir "LSP-camp.sublime-settings"))) {
    Install-File (Join-Path $scriptDir "LSP-camp.sublime-settings.example") (Join-Path $userDir "LSP-camp.sublime-settings")
}
else {
    Write-Host "Syntax highlighting was installed."
    Write-Host "For language-server support, install the Sublime LSP package and rerun this script."
}
