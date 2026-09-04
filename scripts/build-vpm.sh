#!/usr/bin/env bash
#
# Writes the VPM repository listing that the VRChat Creator Companion and ALCOM read. Those two
# install a package by downloading the zip a listing names, so this is the route that works
# without a Git client on PATH.
#
# The listing is derived from this repository's releases rather than stored: every release that
# carries the package zip becomes one version in it. Nothing has to remember the older versions
# for them to stay installable, and the listing cannot describe something that never shipped.
#
# Each version's manifest is read out of that version's own zip rather than out of the working
# tree, so an older version is described by the package.json that shipped with it.
#
# Usage: build-vpm.sh <output>
#   GH_TOKEN=... bash scripts/build-vpm.sh site/vpm.json

set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: build-vpm.sh <output>" >&2
  exit 1
fi

OUT=$1

ROOT=$(cd "$(dirname "$0")/.." && pwd)
MANIFEST="$ROOT/jp.shiranui-isuzu.unity-mcp/package.json"

REPOSITORY=${GITHUB_REPOSITORY:-isuzu-shiranui/UnityMCP}

# Written into the listing as its own `url`, and it is also the address the one-click
# vcc://vpm/addRepo link carries, so the two have to name the same file.
LISTING_URL=${VPM_LISTING_URL:-https://unity-mcp.shiranui-isuzu.dev/vpm.json}
LISTING_ID=dev.shiranui-isuzu.vpm
LISTING_NAME="Unity MCP"
LISTING_AUTHOR=Shiranui_Isuzu

# How many releases to examine, capped at the 100 the API returns in one page. Releases past
# this many are not examined and their versions would drop out of the listing, so the run says
# so when the page comes back full rather than letting that happen quietly.
PER_PAGE=${VPM_PER_PAGE:-100}

if [ ! -f "$MANIFEST" ]; then
  echo "$MANIFEST does not exist" >&2
  exit 1
fi

# Only the package id is taken from the working tree. Everything describing a version comes from
# that version's own zip.
PACKAGE=$(jq -r .name "$MANIFEST")
PREFIX="$PACKAGE-"

RELEASES=$(mktemp)
CANDIDATES=$(mktemp)
ACC=$(mktemp)
WORK=$(mktemp -d)
trap 'rm -rf "$RELEASES" "$CANDIDATES" "$ACC" "$WORK"' EXIT

gh api "repos/$REPOSITORY/releases?per_page=$PER_PAGE" >"$RELEASES"

TOTAL=$(jq length "$RELEASES")
echo "$REPOSITORY has $TOTAL releases"

if [ "$TOTAL" -ge "$PER_PAGE" ]; then
  echo "the releases page came back full at $PER_PAGE, so older releases were not looked at." >&2
  echo "Raise VPM_PER_PAGE, or page through the API once 100 is not enough, before that drops" >&2
  echo "a published version out of the listing." >&2
fi

# A draft release's assets cannot be downloaded by anyone but the maintainer, so a version built
# from one would hand every reader a URL that answers 404.
jq -c --arg prefix "$PREFIX" '
  .[]
  | select(.draft | not)
  | . as $release
  | .assets[]
  | select((.name | startswith($prefix)) and (.name | endswith(".zip")))
  | { tag: $release.tag_name, name: .name, url: .browser_download_url }
' "$RELEASES" >"$CANDIDATES"

# A release without the zip is simply not a version of this package: every release before the
# package was distributed this way is one, and so is a release whose upload failed.
jq -r --arg prefix "$PREFIX" '
  .[]
  | select(.draft | not)
  | select([.assets[] | select((.name | startswith($prefix)) and (.name | endswith(".zip")))] | length == 0)
  | "  skipped " + .tag_name + ", it carries no " + $prefix + "*.zip"
' "$RELEASES"

while read -r candidate; do
  TAG=$(echo "$candidate" | jq -r .tag)
  NAME=$(echo "$candidate" | jq -r .name)
  URL=$(echo "$candidate" | jq -r .url)

  ZIP="$WORK/$NAME"

  if ! curl -fsSL --retry 3 --max-time 300 -o "$ZIP" "$URL"; then
    echo "could not download $URL" >&2
    exit 1
  fi

  # Hashed from the bytes fetched from the URL the listing hands the client, so the digest and
  # the download cannot disagree. SHA256SUMS on the release states the same number, but reading
  # it would make a second copy that can drift from the file it describes.
  SHA=$(sha256sum "$ZIP" | cut -d ' ' -f 1)

  if ! unzip -p "$ZIP" package.json >"$WORK/manifest.json" 2>/dev/null; then
    echo "$NAME has no package.json at the root of the archive, so it is not a VPM package." >&2
    exit 1
  fi

  VERSION=$(jq -r .version "$WORK/manifest.json")
  echo "  $TAG carries $VERSION"

  # The author is converted to the object form the VPM documentation describes and every
  # published listing uses. package.json keeps the plain string, which is what Unity's own
  # Package Manager reads and where the string is valid.
  jq -c --arg url "$URL" --arg sha "$SHA" --arg version "$VERSION" '
    {
      version: $version,
      entry: (
        . + { url: $url, zipSHA256: $sha }
        | if (.author | type) == "string" then .author = { name: .author } else . end
      )
    }
  ' "$WORK/manifest.json" >>"$ACC"
done <"$CANDIDATES"

jq -s \
  --arg name "$LISTING_NAME" \
  --arg id "$LISTING_ID" \
  --arg url "$LISTING_URL" \
  --arg author "$LISTING_AUTHOR" \
  --arg package "$PACKAGE" '
  {
    name: $name,
    author: $author,
    url: $url,
    id: $id,
    packages: (
      if length == 0 then {}
      else { ($package): { versions: (map({ (.version): .entry }) | add) } }
      end
    )
  }
' "$ACC" >"$OUT.tmp"

mv "$OUT.tmp" "$OUT"

echo "$OUT names:"
jq -r --arg package "$PACKAGE" '
  if (.packages | length) == 0 then "  no versions yet"
  else (.packages[$package].versions | keys_unsorted[] | "  " + .)
  end
' "$OUT"
