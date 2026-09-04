#!/bin/sh
# Installs isuzu-unity-cli from GitHub Releases (isuzu-shiranui/UnityMCP).
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh | sh
#   curl -fsSL .../install.sh | sh -s -- --version v4.0.0 --dir /custom/path
#
# Options / env fallbacks:
#   --version <tag>   Release tag, e.g. v4.0.0 or 4.0.0. Default: latest.
#                      Falls back to $ISUZU_UNITY_CLI_VERSION.
#   --dir <path>      Install directory. Default: $HOME/.local/bin.
#                      Falls back to $ISUZU_UNITY_CLI_DIR.
#
# POSIX sh only: piped into "sh", not necessarily bash.

set -eu

version="${ISUZU_UNITY_CLI_VERSION:-latest}"
install_dir="${ISUZU_UNITY_CLI_DIR:-$HOME/.local/bin}"

while [ $# -gt 0 ]; do
    case "$1" in
        --version)
            version="$2"
            shift 2
            ;;
        --dir)
            install_dir="$2"
            shift 2
            ;;
        *)
            echo "install.sh: unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

case "$version" in
    latest) ;;
    v*) ;;
    *) version="v$version" ;;
esac

# Test hook only: replaces the release download base with a local server so
# the whole flow (download, hash check, install) can be exercised without
# hitting GitHub. Not documented to end users.
if [ -n "${ISUZU_UNITY_CLI_BASE_URL:-}" ]; then
    base_url="$ISUZU_UNITY_CLI_BASE_URL"
elif [ "$version" = "latest" ]; then
    base_url="https://github.com/isuzu-shiranui/UnityMCP/releases/latest/download"
else
    base_url="https://github.com/isuzu-shiranui/UnityMCP/releases/download/$version"
fi
base_url="${base_url%/}"

os_name=$(uname -s)
arch_name=$(uname -m)
case "$os_name" in
    Darwin)
        case "$arch_name" in
            arm64) target="osx-arm64" ;;
            x86_64) target="osx-x64" ;;
            *)
                echo "install.sh: unsupported macOS architecture: $arch_name" >&2
                exit 1
                ;;
        esac
        ;;
    Linux)
        case "$arch_name" in
            x86_64 | amd64) target="linux-x64" ;;
            *)
                echo "install.sh: unsupported Linux architecture: $arch_name" >&2
                exit 1
                ;;
        esac
        ;;
    *)
        echo "install.sh: unsupported OS: $os_name (isuzu-unity-cli ships Linux, macOS, and Windows builds only)" >&2
        exit 1
        ;;
esac

asset_name="isuzu-unity-cli-$target"

if command -v curl >/dev/null 2>&1; then
    downloader="curl -fsSL -o"
elif command -v wget >/dev/null 2>&1; then
    downloader="wget -q -O"
else
    echo "install.sh: need curl or wget to download" >&2
    exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
    hasher="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
    hasher="shasum -a 256"
else
    echo "install.sh: need sha256sum or shasum to verify the download" >&2
    exit 1
fi

tmp_dir=$(mktemp -d "${TMPDIR:-/tmp}/isuzu-unity-cli-install.XXXXXX")
trap 'rm -rf "$tmp_dir"' EXIT

echo "Downloading $asset_name ($version)..."
if ! $downloader "$tmp_dir/$asset_name" "$base_url/$asset_name"; then
    echo "install.sh: download failed for version '$version'" >&2
    exit 1
fi
if ! $downloader "$tmp_dir/SHA256SUMS" "$base_url/SHA256SUMS"; then
    echo "install.sh: failed to download SHA256SUMS for version '$version'" >&2
    exit 1
fi

# sha256sum in binary mode prefixes the filename field with "*"; strip it
# before comparing so both text- and binary-mode SHA256SUMS files match.
expected_hash=$(awk -v f="$asset_name" '{name=$2; sub(/^\*/, "", name); if (name == f) print $1}' "$tmp_dir/SHA256SUMS")
if [ -z "$expected_hash" ]; then
    echo "install.sh: SHA256SUMS has no entry for $asset_name. The release may be malformed." >&2
    exit 1
fi
actual_hash=$($hasher "$tmp_dir/$asset_name" | awk '{print $1}')
if [ "$actual_hash" != "$expected_hash" ]; then
    echo "install.sh: checksum mismatch for $asset_name (expected $expected_hash, got $actual_hash). Aborting install." >&2
    exit 1
fi
echo "Checksum verified."

mkdir -p "$install_dir"
chmod +x "$tmp_dir/$asset_name"
mv -f "$tmp_dir/$asset_name" "$install_dir/isuzu-unity-cli"

case ":$PATH:" in
    *":$install_dir:"*) ;;
    *)
        echo ""
        echo "warning: $install_dir is not on your PATH."
        echo "  Add this to your shell profile (~/.bashrc, ~/.zshrc, etc.):"
        echo "    export PATH=\"$install_dir:\$PATH\""
        ;;
esac

echo ""
if ! "$install_dir/isuzu-unity-cli" --version; then
    echo "warning: installed, but '$install_dir/isuzu-unity-cli --version' failed to run" >&2
fi

echo ""
echo "Next steps:"
echo "  1. Add the Unity package via Package Manager -> Add package from git URL:"
echo "     https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp"
echo "  2. Run 'isuzu-unity-cli setup' to install the agent skill,"
echo "     or 'isuzu-unity-cli setup --mcp' to also register the MCP endpoint."
