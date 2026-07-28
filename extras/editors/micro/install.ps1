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

Installs or updates Camp support for micro.
Syntax highlighting is always installed. Language-server support is configured
when Micro's LSP plugin is detected.

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
$configRoot = if ($env:APPDATA) { $env:APPDATA } else { Join-Path $HOME ".config" }
$microDir = Join-Path $configRoot "micro"
$syntaxDir = Join-Path $microDir "syntax"
$source = Join-Path $scriptDir "camp.yaml"
$target = Join-Path $syntaxDir "camp.yaml"

if ((Test-Path $target) -and
    ((Get-FileHash $source).Hash -ne (Get-FileHash $target).Hash) -and
    -not $Force) {
    $backup = "$target.backup.$(Get-Date -Format yyyyMMddHHmmss)"
    if ($DryRun) {
        Write-Host "dry run: would back up $target to $backup"
    }
    else {
        Copy-Item $target $backup
        Write-Host "backup: $backup"
    }
}

if ($DryRun) {
    Write-Host "dry run: would install $target"
}
else {
    New-Item -ItemType Directory -Force -Path $syntaxDir | Out-Null
    Copy-Item $source $target -Force
    Write-Host "installed: $target"
}

if ($SyntaxOnly -or $NoLsp) {
    Write-Host "LSP configuration skipped."
    exit 0
}

$pluginDir = Join-Path $microDir "plug\lsp"
if ((Get-Command micro -ErrorAction SilentlyContinue) -and (Test-Path $pluginDir)) {
    $settings = Join-Path $microDir "settings.json"
    if ($DryRun) {
        Write-Host "dry run: would ensure micro setting lsp.server contains camp=camp-lsp in $settings"
    }
    else {
        New-Item -ItemType Directory -Force -Path $microDir | Out-Null
        $data = @{}
        if (Test-Path $settings) {
            $data = Get-Content $settings -Raw | ConvertFrom-Json -AsHashtable
        }
        $current = if ($data.ContainsKey("lsp.server")) { [string]$data["lsp.server"] } else { "" }
        if ($current -notmatch '(^|,)camp=') {
            $data["lsp.server"] = if ($current.Length -eq 0) { "camp=camp-lsp" } else { "$current,camp=camp-lsp" }
            $data | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $settings
        }
        Write-Host "lsp: camp=camp-lsp"
    }
}
else {
    Write-Host "Syntax highlighting was installed."
    Write-Host "For language-server support, install Micro's LSP plugin and rerun this script."
}
