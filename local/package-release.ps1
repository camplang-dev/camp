param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$RepoRoot,
    [string]$OutputDir,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$supportedRids = @("win-x64", "win-x86", "linux-x64", "osx-x64", "osx-arm64")
if ($supportedRids -notcontains $Rid) {
    throw "Unsupported RID '$Rid'. Supported RIDs: $($supportedRids -join ', ')"
}

if (-not $RepoRoot) {
    $RepoRoot = (git rev-parse --show-toplevel).Trim()
}
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
if (-not $OutputDir) {
    $OutputDir = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot "..\releases"))
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

foreach ($required in @("src\publish-tools.proj", "lib", "targets", "LICENSE", "README.md")) {
    $path = Join-Path $RepoRoot $required
    if (-not (Test-Path $path)) {
        throw "Required path is missing: $path"
    }
}

if (-not $SkipPublish) {
    dotnet msbuild (Join-Path $RepoRoot "src\publish-tools.proj") "-p:RuntimeIdentifier=$Rid"
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$toolExt = ""
$archiveExt = "tar.gz"
if ($Rid.StartsWith("win-")) {
    $toolExt = ".exe"
    $archiveExt = "zip"
}

$publishDir = Join-Path $RepoRoot "bin\publish\$Rid"
foreach ($tool in @("campc", "camp-lsp", "camp-dap")) {
    $path = Join-Path $publishDir "$tool$toolExt"
    if (-not (Test-Path $path)) {
        throw "Published tool is missing: $path"
    }
}

$commitSha = (git -C $RepoRoot rev-parse HEAD).Trim()
$packageName = "camp-$Version-$Rid"
$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
$layout = Join-Path $stageRoot $packageName
New-Item -ItemType Directory -Force -Path (Join-Path $layout "bin") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $layout "cache\lib") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $layout "cache\pkg") | Out-Null

try {
    Copy-Item (Join-Path $publishDir "campc$toolExt") (Join-Path $layout "bin")
    Copy-Item (Join-Path $publishDir "camp-lsp$toolExt") (Join-Path $layout "bin")
    Copy-Item (Join-Path $publishDir "camp-dap$toolExt") (Join-Path $layout "bin")
    Copy-Item (Join-Path $RepoRoot "lib") (Join-Path $layout "lib") -Recurse
    Copy-Item (Join-Path $RepoRoot "targets") (Join-Path $layout "targets") -Recurse
    Get-ChildItem $layout -Filter ".DS_Store" -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Force
    Copy-Item (Join-Path $RepoRoot "LICENSE") (Join-Path $layout "LICENSE")
    Copy-Item (Join-Path $RepoRoot "README.md") (Join-Path $layout "README.md")
    $trademarks = Join-Path $RepoRoot "TRADEMARKS.md"
    if (Test-Path $trademarks) {
        Copy-Item $trademarks (Join-Path $layout "TRADEMARKS.md")
    }
    "$Version`n$commitSha`n$Rid`n" | Set-Content -NoNewline -Encoding utf8 (Join-Path $layout "VERSION")

    $archive = Join-Path $OutputDir "$packageName.$archiveExt"
    $sha = "$archive.sha256"
    Remove-Item -Force $archive, $sha -ErrorAction SilentlyContinue
    if ($archiveExt -eq "zip") {
        Compress-Archive -Path $layout -DestinationPath $archive
    }
    else {
        tar -czf $archive -C $stageRoot $packageName
    }

    $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($archive))`n" | Set-Content -NoNewline -Encoding ascii $sha
    Write-Host "archive: $archive"
    Write-Host "sha256:  $sha"
}
finally {
    Remove-Item -Recurse -Force $stageRoot -ErrorAction SilentlyContinue
}
