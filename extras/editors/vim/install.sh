#!/usr/bin/env sh
set -eu

dry_run=0
syntax_only=0
no_lsp=0
force=0
neovim=0

usage() {
    cat <<'USAGE'
Usage:
  install.sh [options]

Installs or updates Camp support for Vim.
Syntax highlighting is always installed. Language-server support is configured
when a supported Vim LSP client is detected.

Options:
  --neovim        Install to Neovim instead of Vim
  --syntax-only   Install syntax highlighting only
  --no-lsp        Do not configure language-server support
  --force         Overwrite Camp-owned files without prompting
  --dry-run       Show changes without applying them
  -h, --help      Show help
USAGE
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --neovim) neovim=1; shift ;;
        --syntax-only) syntax_only=1; shift ;;
        --no-lsp) no_lsp=1; shift ;;
        --force) force=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
source_pack="$script_dir/pack/camp/start/camp"
if [ "$neovim" -eq 1 ]; then
    target_pack="${XDG_DATA_HOME:-$HOME/.local/share}/nvim/site/pack/camp/start/camp"
else
    target_pack="$HOME/.vim/pack/camp/start/camp"
fi

if [ -e "$target_pack" ] && [ "$force" -eq 0 ]; then
    backup="$target_pack.backup.$(date +%Y%m%d%H%M%S)"
    if [ "$dry_run" -eq 1 ]; then
        echo "dry run: would back up $target_pack to $backup"
    else
        rm -rf "$backup"
        cp -R "$target_pack" "$backup"
        echo "backup: $backup"
    fi
fi

if [ "$dry_run" -eq 1 ]; then
    echo "dry run: would install $target_pack"
else
    mkdir -p "$(dirname "$target_pack")"
    rm -rf "$target_pack"
    cp -R "$source_pack" "$target_pack"
    echo "installed: $target_pack"
fi

if [ "$syntax_only" -eq 1 ] || [ "$no_lsp" -eq 1 ]; then
    echo "LSP configuration skipped."
elif [ "$neovim" -eq 1 ]; then
    echo "Neovim syntax package installed."
    echo "For Neovim built-in LSP, configure camp-lsp from your init.lua."
else
    echo "Vim package includes a guarded vim-lsp hook when vim-lsp is installed."
fi
