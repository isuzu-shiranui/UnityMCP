#!/usr/bin/env bash
# Runs scripts/winget-due.sh against canned GitHub answers. A fake curl earlier on PATH answers from
# a folder of files, so nothing here reaches the network.
#
#   bash scripts/tests/winget-due.test.sh
set -uo pipefail

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/bin"
cat > "$WORK/bin/curl" <<'FAKE'
#!/usr/bin/env bash
# The URL's path, without its query and with / written as _, names a file in $FIXTURES whose
# contents are the body of a 200. A file named the same with .status answers that status instead,
# and a name with neither answers 404.
out=""
url=""
while [ $# -gt 0 ]; do
  case "$1" in
    -o) out="$2"; shift 2 ;;
    -w|-H|--connect-timeout|--max-time|--data-urlencode) shift 2 ;;
    -s|-G) shift ;;
    *) url="$1"; shift ;;
  esac
done
name=$(printf '%s' "${url#https://api.github.com/}" | sed 's/?.*//' | tr '/' '_')
if [ -f "$FIXTURES/$name.status" ]; then
  : > "$out"
  cat "$FIXTURES/$name.status"
elif [ -f "$FIXTURES/$name" ]; then
  cp "$FIXTURES/$name" "$out"
  printf 200
else
  : > "$out"
  printf 404
fi
FAKE
chmod +x "$WORK/bin/curl"

# jq on Windows ends its lines with CRLF, which the script compares as text. Only the Git Bash on a
# workstation needs this; the CI runner's jq writes LF.
if command -v cygpath > /dev/null 2>&1; then
  REAL_JQ=$(command -v jq)
  cat > "$WORK/bin/jq" <<SHIM
#!/usr/bin/env bash
"$REAL_JQ" "\$@" | tr -d '\r'
exit \${PIPESTATUS[0]}
SHIM
  chmod +x "$WORK/bin/jq"
fi

PACKAGE_DIR="repos_microsoft_winget-pkgs_contents_manifests_i_IsuzuShiranui_IsuzuUnityCli"
PULLS="repos_microsoft_winget-pkgs_pulls"
FAILURES=0

# fixture <case> <file> <body>
fixture() {
  mkdir -p "$WORK/$1"
  printf '%s' "$3" > "$WORK/$1/$2"
}

# expect <case> <version> <printed> <exit code>
expect() {
  local printed code
  mkdir -p "$WORK/$1"
  printed=$(FIXTURES="$WORK/$1" PATH="$WORK/bin:$PATH" GH_TOKEN=test bash "$ROOT/scripts/winget-due.sh" "$2" 2> "$WORK/$1.err")
  code=$?

  if [ "$printed" = "$3" ] && [ "$code" = "$4" ]; then
    echo "ok   $1"
  else
    echo "FAIL $1: printed '$printed' and exited $code, expected '$3' and $4"
    sed 's/^/     /' "$WORK/$1.err"
    FAILURES=$((FAILURES + 1))
  fi
}

NONE='{"incomplete_results":false,"items":[]}'
PR='{"number":5,"title":"New version: IsuzuShiranui.IsuzuUnityCli version 4.3.2","html_url":"https://github.com/microsoft/winget-pkgs/pull/5","state":"open"}'
FOUND="{\"incomplete_results\":false,\"items\":[$PR]}"
OWN_FILES='[{"filename":"manifests/i/IsuzuShiranui/IsuzuUnityCli/4.3.2/IsuzuShiranui.IsuzuUnityCli.yaml"}]'
OTHER_FILES='[{"filename":"manifests/o/Other/Tool/1.0.0/Other.Tool.yaml"}]'

expect prerelease 4.3.2-rc.1 false 0

expect not-in-winget 4.3.2 false 0

fixture merged "$PACKAGE_DIR" '[]'
fixture merged "${PACKAGE_DIR}_4.3.2" '[]'
expect merged 4.3.2 false 0

fixture due "$PACKAGE_DIR" '[]'
fixture due search_issues "$NONE"
fixture due "$PULLS" '[]'
expect due 4.3.2 true 0

fixture submitted "$PACKAGE_DIR" '[]'
fixture submitted search_issues "$FOUND"
fixture submitted "${PULLS}_5_files" "$OWN_FILES"
expect submitted 4.3.2 false 0

fixture title-only "$PACKAGE_DIR" '[]'
fixture title-only search_issues "$FOUND"
fixture title-only "${PULLS}_5_files" "$OTHER_FILES"
fixture title-only "$PULLS" '[]'
expect title-only 4.3.2 true 0

fixture search-lag "$PACKAGE_DIR" '[]'
fixture search-lag search_issues "$NONE"
fixture search-lag "$PULLS" "[$PR]"
fixture search-lag "${PULLS}_5_files" "$OWN_FILES"
expect search-lag 4.3.2 false 0

fixture rate-limited "$PACKAGE_DIR" '[]'
fixture rate-limited search_issues.status 403
expect rate-limited 4.3.2 '' 1

fixture files-unreadable "$PACKAGE_DIR" '[]'
fixture files-unreadable search_issues "$FOUND"
fixture files-unreadable "${PULLS}_5_files.status" 502
expect files-unreadable 4.3.2 '' 1

if [ "$FAILURES" != 0 ]; then
  echo "$FAILURES case(s) failed"
  exit 1
fi

echo "all cases passed"
