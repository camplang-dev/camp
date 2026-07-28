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

Installs or updates Camp support for Sublime Text.
Syntax highlighting is always installed. Language-server support is configured
when the Sublime LSP package is detected.

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
source_syntax="$script_dir/Camp.sublime-syntax"
source_lsp="$script_dir/LSP-camp.sublime-settings.example"

case "$(uname -s)" in
    Darwin) user_dir="$HOME/Library/Application Support/Sublime Text/Packages/User" ;;
    *) user_dir="${XDG_CONFIG_HOME:-$HOME/.config}/sublime-text/Packages/User" ;;
esac

backup_if_needed() {
    src="$1"
    dest="$2"
    if [ -f "$dest" ] && ! cmp -s "$src" "$dest"; then
        if [ "$force" -eq 0 ]; then
            backup="$dest.backup.$(date +%Y%m%d%H%M%S)"
            if [ "$dry_run" -eq 1 ]; then
                echo "dry run: would back up $dest to $backup"
            else
                cp "$dest" "$backup"
                echo "backup: $backup"
            fi
        fi
    fi
}

install_file() {
    src="$1"
    dest="$2"
    backup_if_needed "$src" "$dest"
    if [ "$dry_run" -eq 1 ]; then
        echo "dry run: would install $dest"
    else
        mkdir -p "$(dirname "$dest")"
        cp "$src" "$dest"
        echo "installed: $dest"
    fi
}

install_file "$source_syntax" "$user_dir/Camp.sublime-syntax"

if [ "$syntax_only" -eq 1 ] || [ "$no_lsp" -eq 1 ]; then
    echo "LSP configuration skipped."
    exit 0
fi

if [ -d "$(dirname "$user_dir")/LSP" ] || [ -f "$user_dir/LanguageServers.sublime-settings" ] || [ -f "$user_dir/LSP-camp.sublime-settings" ]; then
    install_file "$source_lsp" "$user_dir/LSP-camp.sublime-settings"
else
    echo "Syntax highlighting was installed."
    echo "For language-server support, install the Sublime LSP package and rerun this script."
fi
