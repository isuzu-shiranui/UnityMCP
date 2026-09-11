#!/usr/bin/env bash
#
# Sets the release version in every file that states one.
#
# Four fields in three files have to agree, and nothing derives one from another: the Unity
# package manifest, the CLI's csproj, and server.json twice — once for the registry entry and
# once for the NuGet package it names. A push to main whose package.json version changed is
# what starts a release, so a field left behind here does not stop anything; it publishes a
# registry entry pointing at a version that is not the one being released.
#
# Usage: set-version.sh 4.4.0

set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: set-version.sh <version>" >&2
  exit 1
fi

VERSION=$1

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "'$VERSION' is not a version. It takes major.minor.patch, optionally with a pre-release suffix." >&2
  exit 1
fi

ROOT=$(cd "$(dirname "$0")/.." && pwd)
MANIFEST="$ROOT/jp.shiranui-isuzu.unity-mcp/package.json"
CSPROJ="$ROOT/isuzu-unity-cli/src/IsuzuUnityCli/IsuzuUnityCli.csproj"
SERVER="$ROOT/server.json"

for file in "$MANIFEST" "$CSPROJ" "$SERVER"; do
  if [ ! -f "$file" ]; then
    echo "$file does not exist" >&2
    exit 1
  fi
done

# jq rather than a text substitution: the version string appears in server.json's package list
# as well as at the top level, and a blind replace would also reach any dependency that happens
# to carry the same number.
#
# The carriage returns are stripped because jq on Windows writes CRLF, and .gitattributes checks
# these files out as LF: without this a version bump rewrites every line of both files.
jq --arg v "$VERSION" '.version = $v' "$MANIFEST" | tr -d '\r' >"$MANIFEST.tmp" && mv "$MANIFEST.tmp" "$MANIFEST"
jq --arg v "$VERSION" '.version = $v | .packages = [.packages[] | .version = $v]' "$SERVER" | tr -d '\r' >"$SERVER.tmp" && mv "$SERVER.tmp" "$SERVER"

perl -0pi -e "s{<Version>[^<]+</Version>}{<Version>$VERSION</Version>}" "$CSPROJ"

echo "package.json:  $(jq -r .version "$MANIFEST")"
# sed rather than grep -oP: Git Bash ships a grep whose -P refuses to run outside a unibyte
# locale, and this script is run on the workstation that cuts the release.
echo "csproj:        $(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$CSPROJ")"
echo "server.json:   $(jq -r .version "$SERVER")"
echo "its nuget entry: $(jq -r '.packages[0].version' "$SERVER")"
echo
echo "Add the CHANGELOG section for $VERSION before this reaches main; the release takes its"
echo "tag from package.json and does not read the changelog."
