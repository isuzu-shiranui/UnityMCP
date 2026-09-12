#!/usr/bin/env bash
# Prints true when this version of the CLI is due for a pull request to microsoft/winget-pkgs,
# false when it is not, and exits 1 when GitHub does not give a usable answer. Needs GH_TOKEN.
#
#   bash scripts/winget-due.sh 4.3.2
#
# Not due: a prerelease; a package winget-pkgs does not have yet, because wingetcreate builds each
# manifest from the previous one and the first is submitted by hand; a version already merged; and
# a version with a pull request in any state that changes its manifest folder. Counting only open
# pull requests would submit a closed one again on every push to main, so a version whose pull
# request was closed is left to be submitted by hand.
set -euo pipefail

VERSION="${1:?usage: winget-due.sh <version>}"
: "${GH_TOKEN:?GH_TOKEN is not set}"

PACKAGE="IsuzuShiranui.IsuzuUnityCli"
MANIFESTS="manifests/i/IsuzuShiranui/IsuzuUnityCli"
REPOSITORY="https://api.github.com/repos/microsoft/winget-pkgs"

case "$VERSION" in
  *-*)
    echo "$VERSION is a prerelease." >&2
    echo false
    exit 0
    ;;
esac

BODY=$(mktemp)
trap 'rm -f "$BODY"' EXIT

# Writes the response to $BODY and prints its status, which is 000 when nothing came back.
get() {
  curl -s --connect-timeout 20 --max-time 60 -o "$BODY" -w '%{http_code}' \
    -H "Authorization: Bearer $GH_TOKEN" \
    -H "Accept: application/vnd.github+json" \
    "$@" || true
}

# Only 200 and 404 are answers. A rate limit read as "not there" submits the same version twice.
exists() {
  local status
  status=$(get "$REPOSITORY/contents/$1")

  case "$status" in
    200) return 0 ;;
    404) return 1 ;;
    *)
      echo "GitHub answered '$status' for microsoft/winget-pkgs/$1." >&2
      exit 1
      ;;
  esac
}

# Reads a JSON array of pull requests and prints "number url (state)" for each whose title names
# this package and version. Each word of a title is compared without case and without a leading v
# or trailing dots, so "IsuzuShiranui.IsuzuUnityCli v4.3.2" counts and a title naming 4.3.20 does not.
titled() {
  jq -r --arg package "$PACKAGE" --arg version "$VERSION" '
    def words: ascii_downcase | [splits("[^a-z0-9.]+")] | map(sub("^v(?=[0-9])"; "") | sub("[.]+$"; ""));
    ($package | ascii_downcase) as $p
    | .[]
    | (.title | words) as $w
    | select(any($w[]; . == $p) and any($w[]; . == $version))
    | "\(.number) \(.html_url) (\(.state))"'
}

# Reads those lines and prints the first whose pull request changes this version's manifest folder.
# A title is only a claim: a pull request that names the version without submitting it must not
# stop the submission.
submitted() {
  local number rest status

  while read -r number rest; do
    [ -n "$number" ] || continue
    status=$(get "$REPOSITORY/pulls/$number/files?per_page=100")

    if [ "$status" != "200" ]; then
      echo "GitHub answered '$status' for the files of pull request $number." >&2
      exit 1
    fi

    if jq -e --arg dir "$MANIFESTS/$VERSION/" 'any(.[]; .filename | startswith($dir))' "$BODY" > /dev/null; then
      echo "$rest"
      return
    fi
  done
}

if ! exists "$MANIFESTS"; then
  echo "$PACKAGE is not in winget-pkgs yet. Its first manifest is submitted by hand." >&2
  echo false
  exit 0
fi

if exists "$MANIFESTS/$VERSION"; then
  echo "$PACKAGE $VERSION is already in winget-pkgs." >&2
  echo false
  exit 0
fi

STATUS=$(get -G \
  --data-urlencode "q=repo:microsoft/winget-pkgs is:pr in:title \"$PACKAGE\" \"$VERSION\"" \
  "https://api.github.com/search/issues")

if [ "$STATUS" != "200" ] || [ "$(jq -r .incomplete_results "$BODY")" != "false" ]; then
  echo "GitHub's search answered '$STATUS' without a complete result." >&2
  exit 1
fi

# Read in full first: checking a candidate's files overwrites the response it came from.
TITLED=$(jq '.items' "$BODY" | titled)
SUBMITTED=$(printf '%s\n' "$TITLED" | submitted)

# Search results can trail a pull request opened moments ago, which is when a second run asks, so
# the newest hundred are read from the pull request list as well.
if [ -z "$SUBMITTED" ]; then
  STATUS=$(get "$REPOSITORY/pulls?state=all&sort=created&direction=desc&per_page=100")

  if [ "$STATUS" != "200" ]; then
    echo "GitHub answered '$STATUS' for the newest pull requests." >&2
    exit 1
  fi

  TITLED=$(titled < "$BODY")
  SUBMITTED=$(printf '%s\n' "$TITLED" | submitted)
fi

if [ -n "$SUBMITTED" ]; then
  echo "$PACKAGE $VERSION was already submitted: $SUBMITTED" >&2
  echo false
  exit 0
fi

echo "$PACKAGE $VERSION is due." >&2
echo true
