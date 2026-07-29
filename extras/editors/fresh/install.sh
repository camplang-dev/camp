#!/usr/bin/env sh
set -eu

dry_run=0
syntax_only=0
no_lsp=0
force=0

usage() {
    cat <<'USAGE'
Usage:
  install.sh [options]

Installs or updates Camp support for Fresh.
Syntax highlighting is always installed. Language-server support is enabled
unless syntax-only or no-lsp mode is selected.

Options:
  --syntax-only   Install syntax highlighting only
  --no-lsp        Do not configure language-server support
  --force         Overwrite Camp-owned files without prompting
  --dry-run       Show changes without applying them
  -h, --help      Show help
USAGE
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --syntax-only) syntax_only=1; shift ;;
        --no-lsp) no_lsp=1; shift ;;
        --force) force=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"

fresh_config_dir() {
    if command -v fresh >/dev/null 2>&1; then
        config="$(fresh --cmd config paths 2>/dev/null | awk '/^Config:/ { print $2; exit }')"
        if [ -n "$config" ]; then
            echo "$config"
            return
        fi
    fi
    echo "${XDG_CONFIG_HOME:-$HOME/.config}/fresh"
}

target_root="${FRESH_PACKAGE_DIR:-$(fresh_config_dir)/bundles/packages/camp}"

copy_package() {
    mkdir -p "$(dirname "$target_root")"
    rm -rf "$target_root"
    mkdir -p "$target_root/grammars"
    cp "$script_dir/package.json" "$target_root/package.json"
    cp "$script_dir/README.md" "$target_root/README.md"
    cp "$script_dir/grammars/Camp.sublime-syntax" "$target_root/grammars/Camp.sublime-syntax"
}

disable_lsp() {
    python3 - "$target_root/package.json" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as file:
    package = json.load(file)

for language in package.get("fresh", {}).get("languages", []):
    if isinstance(language, dict):
        language["lsp"] = None

with open(path, "w", encoding="utf-8") as file:
    json.dump(package, file, indent=2)
    file.write("\n")
PY
}

if [ -e "$target_root" ] && [ "$force" -eq 0 ]; then
    backup="$target_root.backup.$(date +%Y%m%d%H%M%S)"
    if [ "$dry_run" -eq 1 ]; then
        echo "dry run: would back up $target_root to $backup"
    else
        rm -rf "$backup"
        cp -R "$target_root" "$backup"
        echo "backup: $backup"
    fi
fi

if [ "$dry_run" -eq 1 ]; then
    echo "dry run: would install $target_root"
else
    copy_package
    if [ "$syntax_only" -eq 1 ] || [ "$no_lsp" -eq 1 ]; then
        disable_lsp
    fi
    echo "installed: $target_root"
fi

if [ "$syntax_only" -eq 1 ] || [ "$no_lsp" -eq 1 ]; then
    echo "LSP configuration skipped."
else
    echo "lsp: camp-lsp"
fi
