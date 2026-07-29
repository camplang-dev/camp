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
camp_lsp="$script_dir/../../../bin/camp-lsp"
if [ -f "$camp_lsp" ]; then
    camp_lsp="$(cd "$(dirname "$camp_lsp")" && pwd)/$(basename "$camp_lsp")"
else
    camp_lsp=""
fi
if [ ! -f "$vsix" ]; then
    source_vsix="$script_dir/../../vscode-camp/vscode-camp-0.0.1.vsix"
    if [ -f "$source_vsix" ]; then
        vsix="$source_vsix"
    fi
fi

json_string() {
    printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g; s/^/"/; s/$/"/'
}

settings_path_for_code() {
    case "$code_cmd" in
        code-insiders)
            product="Code - Insiders"
            ;;
        codium)
            product="VSCodium"
            ;;
        *)
            product="Code"
            ;;
    esac
    case "$(uname -s)" in
        Darwin)
            printf '%s/Library/Application Support/%s/User/settings.json\n' "$HOME" "$product"
            ;;
        *)
            printf '%s/%s/User/settings.json\n' "${XDG_CONFIG_HOME:-$HOME/.config}" "$product"
            ;;
    esac
}

add_camp_server_path_setting() {
    settings="$1"
    server_path="$2"
    replace_existing="$3"
    mkdir -p "$(dirname "$settings")"
    json_value="$(json_string "$server_path")"
    if [ ! -f "$settings" ] || ! grep -q '[^[:space:]]' "$settings"; then
        printf '{\n    "camp.server.path": %s\n}\n' "$json_value" > "$settings"
        return 0
    fi
    if grep -Eq '"camp\.server\.path"[[:space:]]*:[[:space:]]*"camp-lsp"' "$settings" || { [ "$replace_existing" -eq 1 ] && grep -Eq '"camp\.server\.path"[[:space:]]*:[[:space:]]*"[^"]*"' "$settings"; }; then
        backup="$settings.backup.$(date +%Y%m%d%H%M%S)"
        cp "$settings" "$backup"
        awk -v value="$json_value" '
            replaced == 0 && /"camp\.server\.path"[[:space:]]*:[[:space:]]*"[^"]*"/ {
                sub(/"camp\.server\.path"[[:space:]]*:[[:space:]]*"[^"]*"/, "\"camp.server.path\": " value)
                replaced = 1
            }
            { print }
        ' "$backup" > "$settings"
        printf 'backup: %s\n' "$backup"
        return 0
    fi
    if grep -Eq '"camp\.server\.path"[[:space:]]*:' "$settings"; then
        return 2
    fi
    if grep -q '^[[:space:]]*{' "$settings" && grep -q '}[[:space:]]*$' "$settings"; then
        backup="$settings.backup.$(date +%Y%m%d%H%M%S)"
        temp="$settings.tmp.$$"
        cp "$settings" "$backup"
        if awk -v value="$json_value" '
            { lines[NR] = $0 }
            END {
                close = 0
                for (i = NR; i >= 1; i--) {
                    if (lines[i] ~ /^[[:space:]]*}[[:space:]]*$/) {
                        close = i
                        break
                    }
                }
                if (close == 0) {
                    exit 1
                }
                prev = 0
                for (i = close - 1; i >= 1; i--) {
                    if (lines[i] !~ /^[[:space:]]*$/) {
                        prev = i
                        break
                    }
                }
                if (prev > 0 && lines[prev] !~ /[{,][[:space:]]*$/) {
                    sub(/[[:space:]]*$/, ",", lines[prev])
                }
                for (i = 1; i <= NR; i++) {
                    if (i == close) {
                        print "    \"camp.server.path\": " value
                    }
                    print lines[i]
                }
            }' "$backup" > "$temp"; then
            mv "$temp" "$settings"
            printf 'backup: %s\n' "$backup"
            return 0
        fi
        rm -f "$temp"
        cp "$backup" "$settings"
    fi
    return 1
}

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

if [ "$syntax_only" -eq 0 ] && [ "$no_lsp" -eq 0 ]; then
    if [ -n "$camp_lsp" ]; then
        settings="$(settings_path_for_code)"
        if output="$(add_camp_server_path_setting "$settings" "$camp_lsp" "$force")"; then
            echo "lsp: camp.server.path = $camp_lsp"
            if [ -n "$output" ]; then
                echo "$output"
            fi
        else
            status="$?"
            if [ "$status" -eq 2 ]; then
                echo "lsp: existing camp.server.path left unchanged in $settings"
            else
                echo "lsp: add \"camp.server.path\": \"$(printf '%s' "$camp_lsp")\" to $settings"
            fi
        fi
    else
        echo "lsp: camp-lsp was not found beside this install; configure camp.server.path if camp-lsp is not on PATH."
    fi
fi
