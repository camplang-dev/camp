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

Installs or updates Camp support for Fresh.
Syntax highlighting is always installed. Language-server support is enabled
unless syntax-only or no-lsp mode is selected.

Options:
  -SyntaxOnly    Install syntax highlighting only
  -NoLsp         Do not configure language-server support
  -Force         Overwrite Camp-owned files without prompting
  -DryRun        Show changes without applying them
  -Help          Show help
"@ | Write-Host
    exit 0
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-FreshConfigDir {
    $fresh = Get-Command fresh -ErrorAction SilentlyContinue
    if ($fresh) {
        $paths = & $fresh.Source --cmd config paths 2>$null
        foreach ($line in $paths) {
            if ($line -match '^Config:\s+(.+)$') {
                return $Matches[1].Trim()
            }
        }
    }

    if ($env:APPDATA) {
        return Join-Path $env:APPDATA "fresh"
    }
    if ($env:XDG_CONFIG_HOME) {
        return Join-Path $env:XDG_CONFIG_HOME "fresh"
    }
    return Join-Path $HOME ".config\fresh"
}

$targetRoot = if ($env:FRESH_PACKAGE_DIR) {
    $env:FRESH_PACKAGE_DIR
}
else {
    Join-Path (Get-FreshConfigDir) "bundles\packages\camp"
}

function Copy-Package {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetRoot) | Out-Null
    Remove-Item -Recurse -Force $targetRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $targetRoot "grammars") | Out-Null
    Copy-Item (Join-Path $scriptDir "package.json") (Join-Path $targetRoot "package.json")
    Copy-Item (Join-Path $scriptDir "README.md") (Join-Path $targetRoot "README.md")
    Copy-Item (Join-Path $scriptDir "grammars\Camp.sublime-syntax") (Join-Path $targetRoot "grammars\Camp.sublime-syntax")
}

function Disable-Lsp {
    $packagePath = Join-Path $targetRoot "package.json"
    $package = Get-Content $packagePath -Raw | ConvertFrom-Json
    foreach ($language in $package.fresh.languages) {
        $language.lsp = $null
    }
    $package | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8 $packagePath
}

if ((Test-Path $targetRoot) -and -not $Force) {
    $backup = "$targetRoot.backup.$(Get-Date -Format yyyyMMddHHmmss)"
    if ($DryRun) {
        Write-Host "dry run: would back up $targetRoot to $backup"
    }
    else {
        Copy-Item $targetRoot $backup -Recurse
        Write-Host "backup: $backup"
    }
}

if ($DryRun) {
    Write-Host "dry run: would install $targetRoot"
}
else {
    Copy-Package
    if ($SyntaxOnly -or $NoLsp) {
        Disable-Lsp
    }
    Write-Host "installed: $targetRoot"
}

if ($SyntaxOnly -or $NoLsp) {
    Write-Host "LSP configuration skipped."
}
else {
    Write-Host "lsp: camp-lsp"
}
