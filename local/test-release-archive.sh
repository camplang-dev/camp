#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: local/test-release-archive.sh <archive>

Unpacks a Camp release archive and verifies the installed layout.
USAGE
}

if [ "$#" -eq 1 ] && { [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; }; then
	usage
	exit 0
fi
if [ "$#" -ne 1 ]; then
	usage >&2
	exit 2
fi

archive="$1"
if [ ! -f "$archive" ]; then
    echo "Archive not found: $archive" >&2
    exit 1
fi

temp_root="$(mktemp -d "${TMPDIR:-/tmp}/camp-archive-test.XXXXXX")"
trap 'rm -rf "$temp_root"' EXIT

case "$archive" in
    *.zip)
        unzip -q "$archive" -d "$temp_root"
        tool_ext=".exe"
        ;;
    *.tar.gz)
        tar -xzf "$archive" -C "$temp_root"
        tool_ext=""
        ;;
    *)
        echo "Unsupported archive extension: $archive" >&2
        exit 1
        ;;
esac

install_root="$(find "$temp_root" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
if [ -z "$install_root" ]; then
    echo "Archive did not contain a top-level install directory." >&2
    exit 1
fi

required_paths=(
    "bin/campc$tool_ext"
    "bin/camp-lsp$tool_ext"
    "bin/camp-dap$tool_ext"
    "lib/global.camp"
    "lib/std/src"
    "targets"
    "cache/lib"
    "cache/pkg"
    "VERSION"
    "LICENSE"
    "README.md"
)
for path in "${required_paths[@]}"; do
    if [ ! -e "$install_root/$path" ]; then
        echo "Installed layout is missing: $path" >&2
        exit 1
    fi
done

campc="$install_root/bin/campc$tool_ext"
chmod +x "$install_root/bin/campc"* 2>/dev/null || true
CAMP_HOME="$install_root" "$campc" --help >/dev/null

tiny="$temp_root/tiny.camp"
cat > "$tiny" <<'EOF_TINY'
export int main()
{
    return 0;
}
EOF_TINY
CAMP_HOME="$install_root" "$campc" dump tokens "$tiny" --nostdlib >/dev/null

native_status="skipped"
if command -v cc >/dev/null 2>&1 || command -v clang >/dev/null 2>&1 || command -v gcc >/dev/null 2>&1 || command -v cl.exe >/dev/null 2>&1; then
    if CAMP_HOME="$install_root" "$campc" run "$tiny" --out-dir "$temp_root/native-out" >/dev/null 2>&1; then
        native_status="passed"
    fi
fi

echo "archive smoke passed: $archive"
echo "native compile smoke: $native_status"
