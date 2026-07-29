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
$campLsp = Join-Path $scriptDir "..\..\..\bin\camp-lsp.exe"
if (Test-Path $campLsp) {
    $campLsp = [System.IO.Path]::GetFullPath($campLsp)
}
else {
    $campLsp = $null
}
if (-not (Test-Path $vsix)) {
    $sourceVsix = Join-Path $scriptDir "..\..\vscode-camp\vscode-camp-0.0.1.vsix"
    if (Test-Path $sourceVsix) {
        $vsix = $sourceVsix
    }
}

function Get-VsCodeSettingsPath($command) {
    $name = $command.Name.ToLowerInvariant()
    $root = if ($env:APPDATA) { $env:APPDATA } else { Join-Path $HOME ".config" }
    if ($name -eq "code-insiders" -or $name -eq "code-insiders.cmd") {
        return Join-Path $root "Code - Insiders\User\settings.json"
    }
    if ($name -eq "codium" -or $name -eq "codium.cmd") {
        return Join-Path $root "VSCodium\User\settings.json"
    }
    return Join-Path $root "Code\User\settings.json"
}

function ConvertTo-JsonString([string]$value) {
    return $value | ConvertTo-Json -Compress
}

function Add-CampServerPathSetting([string]$settings, [string]$serverPath, [bool]$replaceExisting) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settings) | Out-Null
    $jsonValue = ConvertTo-JsonString $serverPath
    if (-not (Test-Path $settings) -or [string]::IsNullOrWhiteSpace((Get-Content $settings -Raw))) {
        "{`n    `"camp.server.path`": $jsonValue`n}" | Set-Content -Encoding utf8 $settings
        return "set"
    }

    $text = Get-Content $settings -Raw
    if ($text -match '"camp\.server\.path"\s*:\s*"([^"]*)"') {
        if ($matches[1] -eq "camp-lsp" -or $replaceExisting) {
            $backup = "$settings.backup.$(Get-Date -Format yyyyMMddHHmmss)"
            Copy-Item $settings $backup
            $updated = [regex]::Replace($text, '"camp\.server\.path"\s*:\s*"([^"]*)"', "`"camp.server.path`": $jsonValue", 1)
            $updated | Set-Content -Encoding utf8 $settings
            return "set: $backup"
        }
        return "exists"
    }
    if ($text -match '"camp\.server\.path"\s*:') {
        return "exists"
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($text -split "`r?`n", -1)) {
        $lines.Add($line)
    }

    $close = -1
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match '^\s*}\s*$') {
            $close = $i
            break
        }
    }
    if ($close -lt 0) {
        return "unsupported"
    }

    $previous = -1
    for ($i = $close - 1; $i -ge 0; $i--) {
        if (-not [string]::IsNullOrWhiteSpace($lines[$i])) {
            $previous = $i
            break
        }
    }
    if ($previous -ge 0 -and $lines[$previous] -notmatch '[{,]\s*$') {
        $lines[$previous] = $lines[$previous].TrimEnd() + ","
    }
    $lines.Insert($close, "    `"camp.server.path`": $jsonValue")
    $backup = "$settings.backup.$(Get-Date -Format yyyyMMddHHmmss)"
    Copy-Item $settings $backup
    $lines -join "`r`n" | Set-Content -Encoding utf8 $settings
    return "set: $backup"
}

if (-not (Test-Path $vsix)) {
    Write-Error "No bundled Camp VS Code extension was found.`nUse a Camp release distribution, or build the VS Code extension first."
}

$codeCommand = $null
foreach ($candidate in @("code", "code-insiders", "codium")) {
    $command = Get-Command $candidate -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        $codeCommand = $command
        break
    }
}

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

$codeExecutable = if ($codeCommand.Source) { $codeCommand.Source } elseif ($codeCommand.Path) { $codeCommand.Path } else { $codeCommand.Name }

Write-Host "VS Code CLI: $codeExecutable"
if ($DryRun) {
    Write-Host "dry run: would run '$codeExecutable --install-extension $vsix --force'"
    exit 0
}

& $codeExecutable --install-extension $vsix --force
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
Write-Host "installed: Camp VS Code extension"

if (-not ($SyntaxOnly -or $NoLsp) -and $campLsp) {
    $settings = Get-VsCodeSettingsPath $codeCommand
    $result = Add-CampServerPathSetting $settings $campLsp $Force
    if ($result -eq "exists") {
        Write-Host "lsp: existing camp.server.path left unchanged in $settings"
    }
    elseif ($result -eq "unsupported") {
        Write-Host "lsp: add `"camp.server.path`": `"${campLsp}`" to $settings"
    }
    else {
        Write-Host "lsp: camp.server.path = $campLsp"
        if ($result.StartsWith("set: ")) {
            Write-Host "backup: $($result.Substring(5))"
        }
    }
}
elseif (-not ($SyntaxOnly -or $NoLsp)) {
    Write-Host "lsp: camp-lsp was not found beside this install; configure camp.server.path if camp-lsp is not on PATH."
}
