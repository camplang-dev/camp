#!/usr/bin/env sh
set -eu

version="latest"
install_dir="${HOME}/.camp"
repo="camplang-dev/camp"
add_to_path=0
no_path=0
dry_run=0

usage() {
    cat <<'USAGE'
Usage: install.sh [options]

Options:
  --version <version>       Release tag to install. Defaults to latest.
  --install-dir <path>      Install directory. Defaults to ~/.camp.
  --add-to-path             Add the Camp bin directory to a recognized shell profile.
  --no-path                 Do not update PATH; print instructions only.
  --repo <owner/name>       GitHub repository. Defaults to camplang-dev/camp.
  --dry-run                 Print actions without changing the system.
  -h, --help                Show this help.
USAGE
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)
            version="${2:-}"
            shift 2
            ;;
        --install-dir)
            install_dir="${2:-}"
            shift 2
            ;;
        --add-to-path)
            add_to_path=1
            shift
            ;;
        --no-path)
            no_path=1
            shift
            ;;
        --repo)
            repo="${2:-}"
            shift 2
            ;;
        --dry-run)
            dry_run=1
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

if [ "$add_to_path" -eq 1 ] && [ "$no_path" -eq 1 ]; then
    echo "--add-to-path and --no-path cannot be used together." >&2
    exit 2
fi

need_tool() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "Required tool is missing: $1" >&2
        exit 1
    fi
}

detect_rid() {
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os:$arch" in
        Darwin:x86_64) echo "osx-x64" ;;
        Darwin:arm64) echo "osx-arm64" ;;
        Linux:x86_64|Linux:amd64) echo "linux-x64" ;;
        Linux:i386|Linux:i686)
            echo "Camp does not provide a Linux x86 host tool distribution." >&2
            exit 1
            ;;
        *)
            echo "Unsupported platform: $os $arch" >&2
            exit 1
            ;;
    esac
}

resolve_latest() {
    releases_url="https://api.github.com/repos/${repo}/releases?per_page=20"
    response="$(curl -fsSL "$releases_url" 2>/dev/null || true)"
    tag="$(printf '%s\n' "$response" | tr '{' '\n' | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
    if [ -z "$tag" ]; then
        echo "Could not resolve a published release for ${repo}." >&2
        exit 1
    fi
    echo "$tag"
}

sha256_verify() {
    archive_name="$1"
    checksum_name="$2"
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 -c "$checksum_name"
    elif command -v sha256sum >/dev/null 2>&1; then
        sha256sum -c "$checksum_name"
    else
        echo "Neither shasum nor sha256sum is available." >&2
        exit 1
    fi
}

best_profile() {
    shell_name="$(basename "${SHELL:-}")"
    if [ "$shell_name" = "zsh" ]; then
        echo "${HOME}/.zshrc"
    elif [ "$shell_name" = "bash" ]; then
        if [ "$(uname -s)" = "Darwin" ]; then
            echo "${HOME}/.bash_profile"
        else
            echo "${HOME}/.bashrc"
        fi
    else
        echo "${HOME}/.profile"
    fi
}

need_tool curl
need_tool tar
rid="$(detect_rid)"
if [ "$version" = "latest" ]; then
    version="$(resolve_latest)"
fi

asset="camp-${version}-${rid}.tar.gz"
checksum="${asset}.sha256"
download_base="https://github.com/${repo}/releases/download/${version}"
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/camp-install.XXXXXX")"
backup_dir=""
cleanup() {
    rm -rf "$temp_root"
}
trap cleanup EXIT

echo "Camp ${version}"
echo "platform: ${rid}"
echo "install:  ${install_dir}"
if [ "$dry_run" -eq 1 ]; then
    echo "dry run: would download ${download_base}/${asset}"
    exit 0
fi

cd "$temp_root"
curl -fL "${download_base}/${asset}" -o "$asset"
curl -fL "${download_base}/${checksum}" -o "$checksum"
sha256_verify "$asset" "$checksum"
tar -xzf "$asset"
unpacked="$(find "$temp_root" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
if [ -z "$unpacked" ] || [ ! -x "$unpacked/bin/campc" ]; then
    echo "Downloaded archive does not contain a valid Camp install layout." >&2
    exit 1
fi

parent_dir="$(dirname "$install_dir")"
mkdir -p "$parent_dir"
if [ -e "$install_dir" ]; then
    backup_dir="${install_dir}.backup.$(date +%Y%m%d%H%M%S)"
    mv "$install_dir" "$backup_dir"
fi
if ! mv "$unpacked" "$install_dir"; then
    if [ -n "$backup_dir" ] && [ -e "$backup_dir" ]; then
        mv "$backup_dir" "$install_dir"
    fi
    echo "Install failed while replacing ${install_dir}." >&2
    exit 1
fi
rm -rf "$backup_dir"
chmod +x "$install_dir/bin/campc" "$install_dir/bin/camp-lsp" "$install_dir/bin/camp-dap" 2>/dev/null || true
CAMP_HOME="$install_dir" "$install_dir/bin/campc" --help >/dev/null

bin_dir="${install_dir}/bin"
if [ "$add_to_path" -eq 1 ]; then
    profile="$(best_profile)"
    mkdir -p "$(dirname "$profile")"
    touch "$profile"
    marker="# Camp language tools"
    line="export PATH=\"$bin_dir:\$PATH\""
    if ! grep -F "$bin_dir" "$profile" >/dev/null 2>&1; then
        {
            echo ""
            echo "$marker"
            echo "$line"
        } >> "$profile"
        echo "Added Camp to PATH in $profile"
    else
        echo "Camp bin directory is already mentioned in $profile"
    fi
elif [ "$no_path" -eq 0 ]; then
    echo ""
    echo "Add Camp to PATH with:"
    echo "  export PATH=\"$bin_dir:\$PATH\""
fi

echo "installed: $install_dir"
echo "verify:    $install_dir/bin/campc --help"
