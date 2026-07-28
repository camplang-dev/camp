param(
    [switch]$Neovim,
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

Installs or updates Camp support for Vim.
Syntax highlighting is always installed. Language-server support is configured
when a supported Vim LSP client is detected.

Options:
  -Neovim        Install to Neovim instead of Vim
  -SyntaxOnly    Install syntax highlighting only
  -NoLsp         Do not configure language-server support
  -Force         Overwrite Camp-owned files without prompting
  -DryRun        Show changes without applying them
  -Help          Show help
"@ | Write-Host
    exit 0
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePack = Join-Path $scriptDir "pack\camp\start\camp"
if ($Neovim) {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { $HOME }
    $targetPack = Join-Path $base "nvim-data\site\pack\camp\start\camp"
}
else {
    $targetPack = Join-Path $HOME "vimfiles\pack\camp\start\camp"
}

if ((Test-Path $targetPack) -and -not $Force) {
    $backup = "$targetPack.backup.$(Get-Date -Format yyyyMMddHHmmss)"
    if ($DryRun) {
        Write-Host "dry run: would back up $targetPack to $backup"
    }
    else {
        Copy-Item $targetPack $backup -Recurse
        Write-Host "backup: $backup"
    }
}

if ($DryRun) {
    Write-Host "dry run: would install $targetPack"
}
else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetPack) | Out-Null
    Remove-Item -Recurse -Force $targetPack -ErrorAction SilentlyContinue
    Copy-Item $sourcePack $targetPack -Recurse
    Write-Host "installed: $targetPack"
}

if ($SyntaxOnly -or $NoLsp) {
    Write-Host "LSP configuration skipped."
}
elseif ($Neovim) {
    Write-Host "Neovim syntax package installed."
    Write-Host "For Neovim built-in LSP, configure camp-lsp from your init.lua."
}
else {
    Write-Host "Vim package includes a guarded vim-lsp hook when vim-lsp is installed."
}
