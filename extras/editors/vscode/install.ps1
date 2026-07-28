param(
    [switch]$SyntaxOnly,
    [switch]$NoLsp,
    [switch]$NoDap,
    [switch]$Force,
    [switch]$DryRun,
    [switch]$Help
)

$ErrorActionPreference = "Stop"

if ($Help) {
@"
Usage:
  install.ps1 [options]

Installs or updates the bundled Camp VS Code extension.
The VS Code extension includes syntax highlighting, language-server support,
and debugging.

Options:
  -SyntaxOnly    Install only syntax support when supported by the package
  -NoLsp         Disable language-server support when supported by the extension
  -NoDap         Disable debugger support when supported by the extension
  -Force         Overwrite Camp-owned files without prompting
  -DryRun        Show changes without applying them
  -Help          Show help
"@ | Write-Host
    exit 0
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$vsix = Join-Path $scriptDir "vscode-camp.vsix"
if (-not (Test-Path $vsix)) {
    $sourceVsix = Join-Path $scriptDir "..\..\vscode-camp\vscode-camp-0.0.1.vsix"
    if (Test-Path $sourceVsix) {
        $vsix = $sourceVsix
    }
}

if (-not (Test-Path $vsix)) {
    Write-Error "No bundled Camp VS Code extension was found.`nUse a Camp release distribution, or build the VS Code extension first."
}

$codeCommand = @("code", "code-insiders", "codium") |
    Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } |
    Select-Object -First 1

Write-Host "Camp VS Code extension: $vsix"
if ($SyntaxOnly -or $NoLsp -or $NoDap) {
    Write-Host "note: the bundled VS Code extension package controls syntax, LSP, and DAP contributions."
}
if ($Force) {
    Write-Host "force: VS Code extension install will use --force."
}

if (-not $codeCommand) {
    Write-Host "VS Code CLI was not found on PATH."
    Write-Host "Install VS Code's 'code' command and rerun this script."
    if ($DryRun) {
        Write-Host "dry run: would install $vsix with code --install-extension --force"
        exit 0
    }
    exit 1
}

Write-Host "VS Code CLI: $($codeCommand.Name)"
if ($DryRun) {
    Write-Host "dry run: would run '$($codeCommand.Name) --install-extension $vsix --force'"
    exit 0
}

& $codeCommand.Name --install-extension $vsix --force
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
Write-Host "installed: Camp VS Code extension"
