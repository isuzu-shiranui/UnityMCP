#!/usr/bin/env bash
# Assembles the Claude Desktop Extension bundle isuzu-unity-cli.mcpb from the four release
# binaries and the checked-in manifest.
#
#   scripts/build-mcpb.sh <version> <directory holding isuzu-unity-cli-<rid>[.exe]> [output dir]
#
# Needs node/npx; the mcpb CLI is fetched with npx on each run, pinned so that a new release of
# it cannot break a release of this one. The checked-in manifest carries version 0.0.0 so that
# the csproj stays the only place the version is written.
set -euo pipefail

VERSION=${1:?version, e.g. 4.0.0}
BIN_DIR=${2:?directory holding the four binaries}
OUT_DIR=${3:-dist/out}

ROOT=$(cd "$(dirname "$0")/.." && pwd)
SRC="$ROOT/isuzu-unity-cli/mcpb"
STAGE="$ROOT/dist/mcpb"
MCPB="npx --yes @anthropic-ai/mcpb@2.1.2"

rm -rf "$STAGE"
mkdir -p "$STAGE/server" "$OUT_DIR"

for bin in isuzu-unity-cli-win-x64.exe isuzu-unity-cli-osx-arm64 isuzu-unity-cli-osx-x64 isuzu-unity-cli-linux-x64; do
  if [ ! -s "$BIN_DIR/$bin" ]; then
    echo "$BIN_DIR/$bin is missing or empty"
    exit 1
  fi
  cp "$BIN_DIR/$bin" "$STAGE/server/$bin"
done
cp "$SRC/server/isuzu-unity-cli-osx" "$STAGE/server/"

# The zip keeps the mode bits, and Claude Desktop runs the command as extracted, so a binary
# that is not executable here fails at first launch on macOS.
chmod +x "$STAGE"/server/*

sed "s/\"version\": \"0.0.0\"/\"version\": \"$VERSION\"/" "$SRC/manifest.json" > "$STAGE/manifest.json"
if ! grep -q "\"version\": \"$VERSION\"" "$STAGE/manifest.json"; then
  echo "the version placeholder was not found in $SRC/manifest.json"
  exit 1
fi

$MCPB validate "$STAGE/manifest.json"

OUT=$(cd "$OUT_DIR" && pwd)/isuzu-unity-cli.mcpb
rm -f "$OUT"
$MCPB pack "$STAGE" "$OUT"

# --self-signed keeps its certificate inside the mcpb package's own install directory, so a
# fresh npx cache (every CI run) mints a new one and nothing lands next to the bundle. Claude
# Desktop installs the bundle signed or not, and logs "Installing unsigned extension" for an
# unsigned one. `mcpb verify` and `mcpb info` report a self-signed bundle as "Not signed"
# because they check the certificate against the OS trust store, so neither is run here.
$MCPB sign --self-signed "$OUT"

ls -la "$OUT"
