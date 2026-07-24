param(
    [Parameter(Mandatory = $true)][string]$Archive
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Archive)) {
    throw "Archive not found: $Archive"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
try {
    $toolExt = ""
    if ($Archive.EndsWith(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -Path $Archive -DestinationPath $tempRoot
        $toolExt = ".exe"
    }
    elseif ($Archive.EndsWith(".tar.gz", [System.StringComparison]::OrdinalIgnoreCase)) {
        tar -xzf $Archive -C $tempRoot
    }
    else {
        throw "Unsupported archive extension: $Archive"
    }

    $installRoot = Get-ChildItem -Path $tempRoot -Directory | Select-Object -First 1
    if (-not $installRoot) {
        throw "Archive did not contain a top-level install directory."
    }
    $root = $installRoot.FullName
    foreach ($path in @(
        "bin\campc$toolExt",
        "bin\camp-lsp$toolExt",
        "bin\camp-dap$toolExt",
        "lib\global.camp",
        "lib\std\src",
        "targets",
        "cache\lib",
        "cache\pkg",
        "VERSION",
        "LICENSE",
        "README.md"
    )) {
        if (-not (Test-Path (Join-Path $root $path))) {
            throw "Installed layout is missing: $path"
        }
    }

    $campc = Join-Path $root "bin\campc$toolExt"
    $env:CAMP_HOME = $root
    & $campc --help | Out-Null
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $tiny = Join-Path $tempRoot "tiny.camp"
    @"
export int main()
{
    return 0;
}
"@ | Set-Content -Encoding utf8 $tiny
    & $campc dump tokens $tiny --nostdlib | Out-Null
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $nativeStatus = "skipped"
    $hasNativeCompiler = [bool](Get-Command cl.exe -ErrorAction SilentlyContinue)
    if ($hasNativeCompiler) {
        & $campc run $tiny --out-dir (Join-Path $tempRoot "native-out") | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $nativeStatus = "passed"
        }
    }

    Write-Host "archive smoke passed: $Archive"
    Write-Host "native compile smoke: $nativeStatus"
}
finally {
    Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
    Remove-Item Env:CAMP_HOME -ErrorAction SilentlyContinue
}
