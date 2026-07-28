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

Installs or updates Camp support for micro.
Syntax highlighting is always installed. Language-server support is configured
when Micro's LSP plugin is detected.

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
config_root="${XDG_CONFIG_HOME:-$HOME/.config}"
micro_dir="$config_root/micro"
syntax_dir="${MICRO_SYNTAX_DIR:-$micro_dir/syntax}"
source_file="$script_dir/camp.yaml"
target_file="$syntax_dir/camp.yaml"

if [ -f "$target_file" ] && ! cmp -s "$source_file" "$target_file" && [ "$force" -eq 0 ]; then
    backup="$target_file.backup.$(date +%Y%m%d%H%M%S)"
    if [ "$dry_run" -eq 1 ]; then
        echo "dry run: would back up $target_file to $backup"
    else
        cp "$target_file" "$backup"
        echo "backup: $backup"
    fi
fi

if [ "$dry_run" -eq 1 ]; then
    echo "dry run: would install $target_file"
else
    mkdir -p "$syntax_dir"
    cp "$source_file" "$target_file"
    echo "installed: $target_file"
fi

if [ "$syntax_only" -eq 1 ] || [ "$no_lsp" -eq 1 ]; then
    echo "LSP configuration skipped."
    exit 0
fi

if command -v micro >/dev/null 2>&1 && { [ -d "$micro_dir/plug/lsp" ] || [ -f "$micro_dir/plug/lsp/lsp.lua" ]; }; then
    settings="$micro_dir/settings.json"
    if [ "$dry_run" -eq 1 ]; then
        echo "dry run: would ensure micro setting lsp.server contains camp=camp-lsp in $settings"
    else
        mkdir -p "$micro_dir"
        if [ ! -f "$settings" ]; then
            printf '{\n    "lsp.server": "camp=camp-lsp"\n}\n' > "$settings"
        elif grep -F '"lsp.server"' "$settings" >/dev/null 2>&1; then
            if grep -F 'camp=camp-lsp' "$settings" >/dev/null 2>&1; then
                :
            elif grep -E '"lsp.server"[[:space:]]*:[[:space:]]*"' "$settings" >/dev/null 2>&1; then
                backup="$settings.backup.$(date +%Y%m%d%H%M%S)"
                cp "$settings" "$backup"
                sed 's/"lsp.server"[[:space:]]*:[[:space:]]*"\([^"]*\)"/"lsp.server": "\1,camp=camp-lsp"/' "$backup" > "$settings"
                echo "backup: $backup"
            else
                echo "Micro settings already contain lsp.server in a shape this script will not rewrite."
            fi
        else
            echo "Micro settings exist; add \"lsp.server\": \"camp=camp-lsp\" or rerun after removing custom formatting."
        fi
        echo "lsp: camp=camp-lsp"
    fi
else
    echo "Syntax highlighting was installed."
    echo "For language-server support, install Micro's LSP plugin and rerun this script."
fi
