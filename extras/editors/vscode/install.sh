#!/usr/bin/env sh
set -eu

dry_run=0
syntax_only=0
no_lsp=0
no_dap=0
force=0

usage() {
    cat <<'USAGE'
Usage:
  install.sh [options]

Installs or updates the bundled Camp VS Code extension.
The VS Code extension includes syntax highlighting, language-server support,
and debugging.

Options:
  --syntax-only   Install only syntax support when supported by the package
  --no-lsp        Disable language-server support when supported by the extension
  --no-dap        Disable debugger support when supported by the extension
  --force         Overwrite Camp-owned files without prompting
  --dry-run       Show changes without applying them
  -h, --help      Show help
USAGE
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --syntax-only) syntax_only=1; shift ;;
        --no-lsp) no_lsp=1; shift ;;
        --no-dap) no_dap=1; shift ;;
        --force) force=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
vsix="$script_dir/vscode-camp.vsix"
if [ ! -f "$vsix" ]; then
    source_vsix="$script_dir/../../vscode-camp/vscode-camp-0.0.1.vsix"
    if [ -f "$source_vsix" ]; then
        vsix="$source_vsix"
    fi
fi

if [ ! -f "$vsix" ]; then
    echo "No bundled Camp VS Code extension was found." >&2
    echo "Use a Camp release distribution, or build the VS Code extension first." >&2
    exit 1
fi

code_cmd=""
for candidate in code code-insiders codium; do
    if command -v "$candidate" >/dev/null 2>&1; then
        code_cmd="$candidate"
        break
    fi
done

echo "Camp VS Code extension: $vsix"
if [ "$syntax_only" -eq 1 ] || [ "$no_lsp" -eq 1 ] || [ "$no_dap" -eq 1 ]; then
    echo "note: the bundled VS Code extension package controls syntax, LSP, and DAP contributions."
fi
if [ "$force" -eq 1 ]; then
    echo "force: VS Code extension install will use --force."
fi

if [ -z "$code_cmd" ]; then
    echo "VS Code CLI was not found on PATH."
    echo "Install VS Code's 'code' command and rerun this script."
    if [ "$dry_run" -eq 1 ]; then
        echo "dry run: would install $vsix with code --install-extension --force"
        exit 0
    fi
    exit 1
fi

echo "VS Code CLI: $code_cmd"
if [ "$dry_run" -eq 1 ]; then
    echo "dry run: would run '$code_cmd --install-extension $vsix --force'"
    exit 0
fi

"$code_cmd" --install-extension "$vsix" --force
echo "installed: Camp VS Code extension"
