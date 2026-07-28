#!/usr/bin/env bash
set -euo pipefail

SUPPORTED_RIDS="win-x64 win-x86 linux-x64 osx-x64 osx-arm64"

usage() {
    cat <<'USAGE'
Usage: local/package-release.sh --version <version> --rid <rid> [options]

Options:
  --repo-root <path>     Repository root. Defaults to the current git root.
  --output-dir <path>    Output directory. Defaults to ../releases from dev.
  --skip-publish         Use existing bin/publish/<rid> output.
  -h, --help             Show this help.
USAGE
}

repo_root=""
output_dir=""
version=""
rid=""
skip_publish=0

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)
            version="${2:-}"
            shift 2
            ;;
        --rid)
            rid="${2:-}"
            shift 2
            ;;
        --repo-root)
            repo_root="${2:-}"
            shift 2
            ;;
        --output-dir)
            output_dir="${2:-}"
            shift 2
            ;;
        --skip-publish)
            skip_publish=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [ -z "$version" ]; then
    echo "--version is required." >&2
    exit 2
fi
if [ -z "$rid" ]; then
    echo "--rid is required." >&2
    exit 2
fi
case " $SUPPORTED_RIDS " in
    *" $rid "*) ;;
    *)
        echo "Unsupported RID '$rid'. Supported RIDs: $SUPPORTED_RIDS" >&2
        exit 2
        ;;
esac

if [ -z "$repo_root" ]; then
    repo_root="$(git rev-parse --show-toplevel)"
fi
repo_root="$(cd "$repo_root" && pwd)"
if [ -z "$output_dir" ]; then
    output_dir="$(cd "$repo_root/.." && pwd)/releases"
fi
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"

resolve_vscode_vsix() {
    release_asset="$repo_root/extras/editors/vscode/vscode-camp.vsix"
    if [ -f "$release_asset" ]; then
        echo "$release_asset"
        return
    fi

    existing="$(find "$repo_root/extras/vscode-camp" -maxdepth 1 -name 'vscode-camp-*.vsix' -type f | head -n 1)"
    if [ -n "$existing" ]; then
        echo "$existing"
        return
    fi

    if ! command -v npm >/dev/null 2>&1; then
        echo "No bundled VS Code extension was found, and npm is not available to build it." >&2
        exit 1
    fi

    (cd "$repo_root/extras/vscode-camp" && npm ci && npm run package)
    built="$(find "$repo_root/extras/vscode-camp" -maxdepth 1 -name 'vscode-camp-*.vsix' -type f | head -n 1)"
    if [ -z "$built" ]; then
        echo "VS Code extension package build did not produce a vscode-camp-*.vsix file." >&2
        exit 1
    fi
    echo "$built"
}

for required in "$repo_root/src/publish-tools.proj" "$repo_root/lib" "$repo_root/targets" "$repo_root/extras/editors" "$repo_root/extras/vscode-camp/package.json" "$repo_root/LICENSE" "$repo_root/README.md"; do
    if [ ! -e "$required" ]; then
        echo "Required path is missing: $required" >&2
        exit 1
    fi
done

vscode_vsix="$(resolve_vscode_vsix)"

if [ "$skip_publish" -eq 0 ]; then
    dotnet msbuild "$repo_root/src/publish-tools.proj" -p:RuntimeIdentifier="$rid"
fi

tool_ext=""
archive_ext="tar.gz"
if [[ "$rid" == win-* ]]; then
    tool_ext=".exe"
    archive_ext="zip"
fi

publish_dir="$repo_root/bin/publish/$rid"
for tool in campc camp-lsp camp-dap; do
    if [ ! -f "$publish_dir/$tool$tool_ext" ]; then
        echo "Published tool is missing: $publish_dir/$tool$tool_ext" >&2
        exit 1
    fi
done

commit_sha="$(git -C "$repo_root" rev-parse HEAD)"
package_name="camp-$version-$rid"
stage_root="$(mktemp -d "${TMPDIR:-/tmp}/camp-release.XXXXXX")"
trap 'rm -rf "$stage_root"' EXIT
layout="$stage_root/$package_name"

mkdir -p "$layout/bin" "$layout/cache/lib" "$layout/cache/pkg"
cp "$publish_dir/campc$tool_ext" "$layout/bin/"
cp "$publish_dir/camp-lsp$tool_ext" "$layout/bin/"
cp "$publish_dir/camp-dap$tool_ext" "$layout/bin/"
cp -R "$repo_root/lib" "$layout/lib"
cp -R "$repo_root/targets" "$layout/targets"
mkdir -p "$layout/extras"
cp -R "$repo_root/extras/editors" "$layout/extras/editors"
if [ "$vscode_vsix" != "$repo_root/extras/editors/vscode/vscode-camp.vsix" ]; then
    cp "$vscode_vsix" "$layout/extras/editors/vscode/vscode-camp.vsix"
fi
find "$layout/lib" "$layout/targets" "$layout/extras" -name .DS_Store -delete
cp "$repo_root/LICENSE" "$layout/LICENSE"
cp "$repo_root/README.md" "$layout/README.md"
if [ -f "$repo_root/TRADEMARKS.md" ]; then
    cp "$repo_root/TRADEMARKS.md" "$layout/TRADEMARKS.md"
fi
cat > "$layout/VERSION" <<EOF_VERSION
$version
$commit_sha
$rid
EOF_VERSION

chmod +x "$layout/bin/campc$tool_ext" "$layout/bin/camp-lsp$tool_ext" "$layout/bin/camp-dap$tool_ext" 2>/dev/null || true
find "$layout/extras/editors" -name install.sh -exec chmod +x {} \; 2>/dev/null || true

archive="$output_dir/$package_name.$archive_ext"
rm -f "$archive" "$archive.sha256"
if [ "$archive_ext" = "zip" ]; then
    (cd "$stage_root" && zip -qr "$archive" "$package_name")
else
    (cd "$stage_root" && tar -czf "$archive" "$package_name")
fi

if command -v shasum >/dev/null 2>&1; then
    (cd "$output_dir" && shasum -a 256 "$(basename "$archive")" > "$(basename "$archive").sha256")
else
    (cd "$output_dir" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")
fi

echo "archive: $archive"
echo "sha256:  $archive.sha256"
